using System.Security.Cryptography;
using System.Text;

namespace BatteryDesk.Core;

public enum DeviceConnection { Bluetooth, Receiver, Cable }

public sealed record DeviceEndpoint(string Key, string Model, DeviceConnection Connection, string? UsbId = null)
{
    // Windows may include a serial or an address in its raw identifiers. Keep the key local and opaque.
    public static string KeyFor(DeviceConnection connection, string windowsId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{connection}:{windowsId.ToUpperInvariant()}")));
}

public sealed record DeviceObservation(DeviceEndpoint Endpoint, bool? Connected, BatterySample? Sample, bool IsLast,
    ReadStatus? Status = null)
{
    public bool Fresh => Connected == true && !IsLast && Sample?.ReceivedAt is not null;

    public static DeviceObservation From(DeviceEndpoint endpoint, BatteryState state) =>
        new(endpoint, state.Connected, state.Sample, state.IsLast, state.Status);

    public DeviceObservation Offline() => this with
        { Connected = false, IsLast = Sample is not null, Status = ReadStatus.Offline };
}

public sealed record DevicePreferences(string? Alias = null, bool Pinned = false, bool Hidden = false,
    int? Low = null, int? ChargeTarget = null, int Order = int.MaxValue);

public sealed record DeviceProfile(string Id, DeviceEndpoint[] Endpoints, DevicePreferences Preferences);

public sealed record DeviceView(DeviceProfile Profile, DeviceObservation Display)
{
    public string Name => Profile.Preferences.Alias ?? Display.Endpoint.Model;
}

public sealed class DeviceDirectory(IEnumerable<DeviceProfile>? saved = null)
{
    private readonly Dictionary<string, DeviceProfile> profiles =
        (saved ?? []).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceObservation> observations = new(StringComparer.Ordinal);
    public bool Dirty { get; private set; }
    public DeviceProfile[] Profiles => profiles.Values.ToArray();
    public void MarkSaved() => Dirty = false;

    public void Observe(DeviceObservation[] current)
    {
        var present = current.Select(item => item.Endpoint.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in observations.Keys.Where(key => !present.Contains(key)).ToArray())
            observations[key] = observations[key].Offline();

        foreach (var item in current)
        {
            var key = item.Endpoint.Key;
            var profile = profiles.Values.FirstOrDefault(group => group.Endpoints.Any(endpoint => endpoint.Key == key));
            if (profile is null)
            {
                profiles.Add(key, new DeviceProfile(key, [item.Endpoint], new DevicePreferences()));
                Dirty = true;
            }
            else if (!profile.Endpoints.Contains(item.Endpoint))
            {
                profiles[profile.Id] = profile with
                {
                    Endpoints = profile.Endpoints.Select(endpoint => endpoint.Key == key ? item.Endpoint : endpoint).ToArray()
                };
                Dirty = true;
            }

            if (item.Sample is null && observations.TryGetValue(key, out var previous) && previous.Sample is not null)
                observations[key] = item with { Sample = previous.Sample, IsLast = true };
            else
                observations[key] = item;
        }
    }

    public DeviceView[] Views() => profiles.Values.Select(ChooseDisplay)
        .OrderByDescending(view => view.Profile.Preferences.Pinned)
        .ThenBy(view => view.Profile.Preferences.Order)
        .ThenByDescending(view => view.Display.Connected == true)
        .ThenBy(view => view.Display.Sample?.Percent ?? 101)
        .ThenBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(view => view.Profile.Id, StringComparer.Ordinal)
        .ToArray();

    public static IEnumerable<DeviceView> Overview(DeviceView[] devices)
    {
        var visible = devices.Where(view => !view.Profile.Preferences.Hidden);
        // The firmware exposes different Windows IDs for USB and Bluetooth. Collapse only
        // the inactive duplicate in the overview, without linking uncertain identities.
        var ek75 = devices.Where(view => view.Profile.Endpoints.Any(IsEk75Connection)).Take(3).ToArray();
        if (ek75.Length != 2 || ek75.Any(view => view.Profile.Endpoints.Length != 1 || view.Profile.Preferences.Hidden)
            || ek75[0].Display.Endpoint.Connection == ek75[1].Display.Endpoint.Connection
            || ek75.All(view => view.Display.Connected != false))
            return visible;

        var duplicate = ek75.OrderByDescending(view => view.Display.Connected != false)
            .ThenByDescending(view => view.Display.Sample is not null)
            .ThenByDescending(view => view.Display.Sample?.ReceivedAt)
            .Last();
        return visible.Where(view => view.Profile.Id != duplicate.Profile.Id);
    }

    private static bool IsEk75Connection(DeviceEndpoint endpoint) =>
        string.Equals(endpoint.Model, DareuKeyboard.Name, StringComparison.OrdinalIgnoreCase)
        && (endpoint.Connection == DeviceConnection.Bluetooth
            || (endpoint.Connection == DeviceConnection.Cable
                && string.Equals(endpoint.UsbId, DareuKeyboard.UsbId, StringComparison.OrdinalIgnoreCase)));

    private DeviceView ChooseDisplay(DeviceProfile profile)
    {
        var all = profile.Endpoints.Select(endpoint => observations.GetValueOrDefault(endpoint.Key)
                    ?? new DeviceObservation(endpoint, false, null, false, ReadStatus.Offline))
                .OrderByDescending(item => item.Fresh)
                .ThenByDescending(item => item.Connected == true && item.Sample is not null && !item.IsLast)
                .ThenByDescending(item => item.Connected == true)
                .ThenByDescending(item => item.Connected == true ? (int)item.Endpoint.Connection : -1)
                .ThenByDescending(item => item.Sample?.ReceivedAt)
                .ThenByDescending(item => item.Sample is not null)
                .ToArray();
        var display = all[0];
        if (display.Sample is null && all.FirstOrDefault(item => item.Sample is not null) is { } last)
            display = display with { Sample = last.Sample, IsLast = true };
        return new DeviceView(profile, display);
    }

    public void Edit(string id, DevicePreferences preferences)
    {
        var profile = profiles[id];
        var alias = string.IsNullOrWhiteSpace(preferences.Alias) ? null : preferences.Alias.Trim();
        profiles[id] = profile with { Preferences = preferences with { Alias = alias } };
        Dirty = true;
    }

    public bool Move(string id, int direction)
    {
        var order = Views().Where(view => view.Profile.Preferences.Pinned == profiles[id].Preferences.Pinned).ToList();
        var index = order.FindIndex(view => view.Profile.Id == id);
        var next = index + direction;
        if (index < 0 || next < 0 || next >= order.Count)
            return false;
        (order[index], order[next]) = (order[next], order[index]);
        for (var position = 0; position < order.Count; position++)
        {
            var profile = order[position].Profile;
            Edit(profile.Id, profile.Preferences with { Order = position });
        }
        return true;
    }

    public bool CanLink(string target, string other) => target != other
        && profiles.TryGetValue(target, out var first) && profiles.TryGetValue(other, out var second)
        && !first.Endpoints.Select(endpoint => endpoint.Connection)
            .Intersect(second.Endpoints.Select(endpoint => endpoint.Connection)).Any();

    public bool Link(string target, string other)
    {
        if (!CanLink(target, other))
            return false;
        var first = profiles[target];
        profiles[target] = first with { Endpoints = [.. first.Endpoints, .. profiles[other].Endpoints] };
        profiles.Remove(other);
        Dirty = true;
        return true;
    }

    public bool Unlink(string profileId, string endpointKey)
    {
        var profile = profiles[profileId];
        var endpoint = profile.Endpoints.FirstOrDefault(item => item.Key == endpointKey);
        // Keep the anchor key on the original profile so its settings remain addressable.
        if (endpoint is null || endpointKey == profileId || profiles.ContainsKey(endpointKey))
            return false;
        profiles[profileId] = profile with { Endpoints = profile.Endpoints.Where(item => item.Key != endpointKey).ToArray() };
        profiles.Add(endpointKey, new DeviceProfile(endpointKey, [endpoint], new DevicePreferences()));
        Dirty = true;
        return true;
    }
}
