using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BatteryDesk.Core;

public enum Transport { LowEnergy, Classic }

public sealed record Endpoint(string Id, string Name, Transport Transport, bool? Connected,
    Guid? Container, string? Address, int? WindowsLevel);

public sealed record Peripheral(string Id, string Name, Endpoint[] Endpoints)
{
    public bool? Connected => Endpoints.Any(endpoint => endpoint.Connected == true) ? true
        : Endpoints.All(endpoint => endpoint.Connected == false) ? false : null;
}

public static partial class WindowsBluetooth
{
    private const string Connected = "System.Devices.Aep.IsConnected";
    private const string Container = "System.Devices.Aep.ContainerId";
    private const string Address = "System.Devices.Aep.DeviceAddress";
    private const string Battery = "System.Devices.BatteryLife";
    private static readonly string[] Properties = [Connected, Container, Address, Battery];

    private static string Selector(Transport transport) => transport == Transport.LowEnergy
        ? BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)
        : BluetoothDevice.GetDeviceSelectorFromPairingState(true);

    public static DeviceWatcher Watch(Transport transport) =>
        DeviceInformation.CreateWatcher(Selector(transport), Properties, DeviceInformationKind.AssociationEndpoint);

    public static Endpoint Describe(DeviceInformation device, Transport transport) =>
        new(device.Id, device.Name, transport, Property(device, Connected) as bool?,
            Property(device, Container) as Guid?, NormalizeAddress(Property(device, Address) as string),
            ParseLevel(Property(device, Battery)));

    public static async Task<Peripheral[]> DiscoverAsync(CancellationToken cancellationToken)
    {
        Task<Endpoint[]>[] queries = Enum.GetValues<Transport>().Select(async transport =>
        {
            var results = await DeviceInformation.FindAllAsync(Selector(transport), Properties,
                DeviceInformationKind.AssociationEndpoint).AsTask(cancellationToken);
            return results.Select(device => Describe(device, transport)).ToArray();
        }).ToArray();
        var endpoints = (await Task.WhenAll(queries)).SelectMany(result => result);
        return Group(endpoints);
    }

    public static Peripheral[] Group(IEnumerable<Endpoint> endpoints) => endpoints
        .GroupBy(endpoint => endpoint.Container is Guid container && container != Guid.Empty
            && container != new Guid("00000000-0000-0000-ffff-ffffffffffff")
            ? container.ToString("D")
            : (endpoint.Address is null ? endpoint.Id : $"{endpoint.Transport}/{endpoint.Address}"),
            StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var ordered = group.OrderByDescending(endpoint => endpoint.Connected == true)
                .ThenBy(endpoint => endpoint.Transport).ThenBy(endpoint => endpoint.Id, StringComparer.Ordinal).ToArray();
            var name = ordered.FirstOrDefault(endpoint => !string.IsNullOrWhiteSpace(endpoint.Name))?.Name;
            return new Peripheral(group.Key, name ?? "Thiết bị Bluetooth", ordered);
        }).ToArray();

    private static object? Property(DeviceInformation device, string key) =>
        device.Properties.TryGetValue(key, out var value) ? value : null;

    private static string? NormalizeAddress(string? address)
    {
        var compact = address?.Replace(":", "").Replace("-", "");
        return compact is { Length: 12 } && compact.All(Uri.IsHexDigit) ? compact.ToUpperInvariant() : null;
    }

    public static int? ParseLevel(object? value) => value switch
    {
        byte level when level <= 100 => level,
        ushort level when level <= 100 => level,
        uint level when level <= 100 => (int)level,
        int level when level is >= 0 and <= 100 => level,
        _ => null
    };

    // Read the Bluetooth root devnode property used by the Windows Bluetooth stack.
    // Profile/service child nodes are deliberately excluded.
    public static Dictionary<(Transport, string), byte> ReadWindowsLevels(IReadOnlyList<Peripheral> devices)
    {
        var wanted = devices.SelectMany(device => device.Endpoints)
            .Where(endpoint => endpoint.Connected == true && endpoint.WindowsLevel is null && endpoint.Address is not null)
            .Select(endpoint => (endpoint.Transport, endpoint.Address!)).ToHashSet();
        Dictionary<(Transport, string), byte> levels = [];
        foreach (var transport in Enum.GetValues<Transport>())
        {
            if (!wanted.Any(key => key.Transport == transport))
                continue;

            var enumerator = transport == Transport.LowEnergy ? "BTHLE" : "BTHENUM";
            var prefix = enumerator + @"\DEV_";
            const uint flags = 0x101; // CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (GetListSize(out var count, enumerator, flags) != 0 || count <= 1)
                    break;
                var buffer = new char[count];
                var result = GetList(enumerator, buffer, count, flags);
                if (result == 0x1A) // CR_BUFFER_SMALL; device tree changed.
                    continue;
                if (result != 0)
                    break;

                var remaining = buffer.AsSpan();
                while (!remaining.IsEmpty)
                {
                    var end = remaining.IndexOf('\0');
                    if (end <= 0)
                        break;
                    var id = remaining[..end];
                    remaining = remaining[(end + 1)..];
                    if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || id.Length <= prefix.Length + 12 || id[prefix.Length + 12] != '\\')
                        continue;
                    var address = NormalizeAddress(id.Slice(prefix.Length, 12).ToString());
                    if (address is null || !wanted.Contains((transport, address)))
                        continue;
                    if (LocateNode(out var node, id.ToString(), 0) != 0)
                        continue;

                    PropertyKey key = new() { Format = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), Id = 2 };
                    uint size = 1;
                    if (GetProperty(node, in key, out var type, out var level, ref size, 0) == 0
                        && type == 3 && size == 1 && level <= 100)
                        levels[(transport, address)] = level;
                }
                break;
            }
        }
        return levels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetListSize(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetList(string filter, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] char[] buffer,
        uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint LocateNode(out uint node, string instanceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint GetProperty(uint node, in PropertyKey key, out uint type, out byte value,
        ref uint size, uint flags);
}
