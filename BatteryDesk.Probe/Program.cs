using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryDesk.Core;

Console.OutputEncoding = Encoding.UTF8;
if (args.Any(arg => arg is not ("--watch" or "--json" or "--usb-only")))
{
    Console.Error.WriteLine("Usage: BatteryDesk.Probe [--json] [--watch] [--usb-only]");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
try
{
    do
    {
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        discovery.CancelAfter(TimeSpan.FromSeconds(12));
        var devices = args.Contains("--usb-only") ? [] : await WindowsBluetooth.DiscoverAsync(discovery.Token);
        var native = WindowsBluetooth.ReadWindowsLevels(devices);
        if (devices.Length == 0 && !args.Contains("--json") && !args.Contains("--usb-only"))
            Console.WriteLine("Windows did not enumerate any paired Bluetooth devices.");

        foreach (var device in devices)
        {
            Reading windows = BatterySamples.FromWindows(device, native);
            Reading? gatt = null;
            var endpoint = device.Endpoints.FirstOrDefault(endpoint =>
                endpoint.Transport == Transport.LowEnergy && endpoint.Connected == true);
            if (endpoint is not null)
            {
                using var session = new GattBattery();
                gatt = await session.ReadAsync(endpoint.Id, false, stop.Token);
            }
            var snapshot = new
            {
                ObservedAt = DateTimeOffset.Now, device.Name, device.Connected,
                Transports = device.Endpoints.Select(endpoint => endpoint.Transport).Distinct(),
                Windows = windows, Gatt = gatt, Selected = gatt?.Sample ?? windows.Sample
            };
            if (args.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(snapshot, json));
            else
            {
                Console.WriteLine($"{snapshot.ObservedAt:T}  {device.Name}  [{device.Connected switch
                {
                    true => "connected", false => "disconnected", null => "connection unknown"
                }}; {string.Join(", ", snapshot.Transports)}]");
                Console.WriteLine($"  Windows: {Describe(windows)}");
                if (gatt is not null)
                    Console.WriteLine($"  GATT:    {Describe(gatt)}");
            }
        }
        var receivers = LightspeedReceiver.Discover();
        foreach (var path in receivers)
        {
            var reading = await LightspeedReceiver.ReadAsync(path, true, stop.Token, retryInitialTimeout: true);
            var snapshot = new
            {
                ObservedAt = DateTimeOffset.Now, Name = reading.DeviceName ?? LightspeedReceiver.DisplayName,
                reading.Connected, Transport = "USB 2.4 GHz", LightspeedReceiver.UsbId, reading.Battery
            };
            if (args.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(snapshot, json));
            else
            {
                Console.WriteLine($"{snapshot.ObservedAt:T}  {snapshot.Name}  [USB 2.4 GHz, {snapshot.UsbId}]");
                Console.WriteLine($"  HID++: {Describe(reading.Battery)}");
            }
        }
        var keyboards = DareuKeyboard.Discover();
        foreach (var path in keyboards)
        {
            var reading = await DareuKeyboard.ReadAsync(path, stop.Token);
            if (args.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ObservedAt = DateTimeOffset.Now, DareuKeyboard.Name, Transport = "USB cable",
                    DareuKeyboard.UsbId, Battery = reading
                }, json));
            else
                Console.WriteLine($"{DareuKeyboard.Name} [USB {DareuKeyboard.UsbId}]: {Describe(reading)}");
        }
        if (receivers.Length == 0 && keyboards.Length == 0 && args.Contains("--usb-only") && !args.Contains("--json"))
            Console.WriteLine("No supported USB device was found.");
        if (!args.Contains("--watch"))
            break;
        await Task.Delay(TimeSpan.FromSeconds(10), stop.Token);
    } while (!stop.IsCancellationRequested);
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception error)
{
    Console.Error.WriteLine(error is OperationCanceledException ? "Windows Bluetooth discovery timed out." : error.Message);
    return 1;
}

static string Describe(Reading reading) => reading.Sample is { } sample
    ? $"{sample.Percent}% ({sample.Source}, {(sample.ReceivedAt is { } time
        ? time.ToLocalTime().ToString("T") : "measurement time unknown")}){(sample.Charging == true ? " — charging" : "")}"
    : $"{reading.Status} — {reading.Detail}";
