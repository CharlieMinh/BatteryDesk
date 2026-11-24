using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using BatteryDesk.Core;

namespace BatteryDesk;

internal sealed record SavedDevices(DeviceProfile[] Profiles, bool RecordHistory);

internal static class DeviceStorage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string Profiles = Path.Combine(BatterySettings.DataDirectory, "devices.json");
    private static readonly string History = Path.Combine(BatterySettings.DataDirectory, "history.json");

    public static SavedDevices ReadDevices()
    {
        var saved = Read(Profiles, new SavedDevices([], false));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        List<DeviceProfile> profiles = [];
        foreach (var profile in saved.Profiles ?? [])
        {
            if (profile?.Id is not { Length: 64 } || profile.Preferences is null || profile.Endpoints is not { Length: > 0 }
                || !profile.Endpoints.All(endpoint => endpoint?.Key is { Length: 64 } && endpoint.Model is not null)
                || !profile.Endpoints.Any(endpoint => endpoint.Key == profile.Id)
                || profile.Endpoints.Select(endpoint => endpoint.Key).Distinct().Count() != profile.Endpoints.Length
                || ids.Contains(profile.Id) || profile.Endpoints.Any(endpoint => keys.Contains(endpoint.Key)))
                continue;
            ids.Add(profile.Id);
            keys.UnionWith(profile.Endpoints.Select(endpoint => endpoint.Key));
            profiles.Add(profile);
        }
        return saved with { Profiles = profiles.ToArray() };
    }
    public static HistoryPoint[] ReadHistory() => Read(History, Array.Empty<HistoryPoint>());
    public static bool SaveDevices(SavedDevices saved) => Write(Profiles, saved);
    public static bool SaveHistory(HistoryPoint[] saved) => Write(History, saved);

    private static T Read<T>(string path, T fallback) where T : class
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? fallback;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Cannot read BatteryDesk preferences: {0}", error.Message);
        }
        return fallback;
    }

    private static bool Write<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(BatterySettings.DataDirectory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Cannot save BatteryDesk preferences: {0}", error.Message);
            return false;
        }
    }
}

internal static class StartupSetting
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Run);
                return key?.GetValue("BatteryDesk") is string { Length: > 0 };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Trace.TraceWarning("Cannot read BatteryDesk auto-start: {0}", error.Message);
                return false;
            }
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Run);
            if (enabled && Environment.ProcessPath is { } exe && File.Exists(exe))
                key.SetValue("BatteryDesk", $"\"{exe}\"");
            else if (!enabled)
                key.DeleteValue("BatteryDesk", throwOnMissingValue: false);
            else
                return false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Trace.TraceWarning("Cannot change BatteryDesk auto-start: {0}", error.Message);
            return false;
        }
    }
}
