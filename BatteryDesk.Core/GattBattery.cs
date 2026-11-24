using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BatteryDesk.Core;

public sealed class GattBattery : IDisposable
{
    private BluetoothLEDevice? device;
    private IReadOnlyList<GattDeviceService> services = [];
    private GattCharacteristic? characteristic;
    private int disposed;
    public bool Subscribed { get; private set; }
    public event Action<BatterySample>? Changed;

    public async Task<Reading> ReadAsync(string endpointId, bool subscribe, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (characteristic is null)
            {
                device = await BluetoothLEDevice.FromIdAsync(endpointId).AsTask(deadline.Token);
                if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    return new(ReadStatus.Unavailable, null, "BLE endpoint was not connected");
                var result = await device.GetGattServicesForUuidAsync(BluetoothUuidHelper.FromShortId(0x180F),
                    BluetoothCacheMode.Cached).AsTask(deadline.Token);
                services = result.Services;
                if (result.Status != GattCommunicationStatus.Success)
                    return Error("Battery Service", result.Status);
                if (services.Count != 1)
                    return new(ReadStatus.Unsupported, null, services.Count == 0 ? "No Battery Service"
                        : "Multiple Battery Services; a single device percentage is ambiguous");
                var characteristics = await services[0].GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,
                    BluetoothCacheMode.Cached).AsTask(deadline.Token);
                if (characteristics.Status != GattCommunicationStatus.Success)
                    return Error("Battery Level", characteristics.Status);
                if (characteristics.Characteristics.Count != 1)
                    return new(ReadStatus.Unsupported, null, "No unambiguous Battery Level characteristic");
                characteristic = characteristics.Characteristics[0];
            }

            if (device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return new(ReadStatus.Unavailable, null, "BLE endpoint was disconnected");

            var reading = new Reading(ReadStatus.Unsupported, null, "Battery Level is not readable");
            if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
            {
                var response = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(deadline.Token);
                var sample = response.Status == GattCommunicationStatus.Success
                    ? Parse(response.Value, BatterySource.GattRead, DateTimeOffset.UtcNow) : null;
                reading = sample is not null ? Reading.Success(sample) : response.Status == GattCommunicationStatus.Success
                    ? new(ReadStatus.Unavailable, null, "Invalid Battery Level payload") : Error("Read", response.Status);
            }

            if (subscribe && !Subscribed)
            {
                var properties = characteristic.CharacteristicProperties;
                var mode = properties.HasFlag(GattCharacteristicProperties.Notify)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : properties.HasFlag(GattCharacteristicProperties.Indicate)
                        ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                        : GattClientCharacteristicConfigurationDescriptorValue.None;
                if (mode != GattClientCharacteristicConfigurationDescriptorValue.None)
                {
                    characteristic.ValueChanged += OnChanged;
                    using var subscription = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    subscription.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        Subscribed = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(mode)
                            .AsTask(subscription.Token) == GattCommunicationStatus.Success;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                    catch (Exception error) when (error is COMException or UnauthorizedAccessException) { }
                    if (!Subscribed)
                        characteristic.ValueChanged -= OnChanged;
                }
            }
            return Subscribed && reading.Status == ReadStatus.Unsupported
                ? new(ReadStatus.Unavailable, null, "Waiting for the first Battery Level notification") : reading;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ReadStatus.Unavailable, null, "BLE read timed out");
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or IOException)
        {
            return new(ReadStatus.Unavailable, null, $"BLE access failed (0x{error.HResult:X8})");
        }
    }

    private void OnChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0
            && Parse(args.CharacteristicValue, BatterySource.GattNotification, args.Timestamp) is { } sample)
            Changed?.Invoke(sample);
    }

    private static BatterySample? Parse(IBuffer? buffer, BatterySource source, DateTimeOffset received)
    {
        if (buffer?.Length != 1)
            return null;
        using var reader = DataReader.FromBuffer(buffer);
        var value = reader.ReadByte();
        return value <= 100 ? new BatterySample(value, source, received) : null;
    }

    private static Reading Error(string operation, GattCommunicationStatus status) =>
        new(ReadStatus.Unavailable, null, $"{operation}: {status}");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        if (characteristic is not null)
            characteristic.ValueChanged -= OnChanged;
        foreach (var service in services)
            service.Dispose();
        device?.Dispose();
    }
}
