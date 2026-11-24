namespace BatteryDesk.Core;

public enum BatterySource { WindowsDevice, WindowsBluetooth, GattRead, GattNotification, LogitechHidPlusPlus, DareuUsb }
public enum ReadStatus { Success, Offline, Unsupported, Unavailable }

public sealed record BatterySample(int Percent, BatterySource Source, DateTimeOffset? ReceivedAt, bool? Charging = null);

public sealed record Reading(ReadStatus Status, BatterySample? Sample, string Detail)
{
    public static Reading Success(BatterySample sample) => new(ReadStatus.Success, sample, "");
}

public static class BatterySamples
{
    public static Reading FromWindows(Peripheral device, IReadOnlyDictionary<(Transport, string), byte> native)
    {
        foreach (var endpoint in device.Endpoints.Where(endpoint => endpoint.Connected == true))
        {
            if (endpoint.WindowsLevel is int level)
                return Reading.Success(new BatterySample(level, BatterySource.WindowsDevice, null));
            if (endpoint.Address is string address && native.TryGetValue((endpoint.Transport, address), out var value))
                return Reading.Success(new BatterySample(value, BatterySource.WindowsBluetooth, null));
        }
        return device.Connected == false
            ? new(ReadStatus.Offline, null, "Windows reports disconnected")
            : new(ReadStatus.Unavailable, null, "Windows did not expose a battery level");
    }
}

public sealed class BatteryState
{
    public const int DefaultLowThreshold = 15;
    private readonly int lowThreshold;
    public BatterySample? Sample { get; private set; }
    public bool IsLast { get; private set; }
    public bool? Connected { get; private set; }
    public ReadStatus? Status { get; private set; }
    private bool lowAlertShown;
    private bool freshInConnection;
    private int? previousWindowsLevel;

    public BatteryState(int lowThreshold = DefaultLowThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lowThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lowThreshold, 99);
        this.lowThreshold = lowThreshold;
    }

    public void SetConnection(bool? connected)
    {
        if (connected != true || Connected != true)
        {
            IsLast = Sample is not null;
            freshInConnection = false;
            previousWindowsLevel = null;
        }
        Connected = connected;
        if (connected == false)
            Status = ReadStatus.Offline;
        else if (connected is null)
            Status = Sample is null ? null : ReadStatus.Unavailable;
    }

    public bool Apply(Reading reading, bool authoritative)
    {
        if (authoritative || reading.Sample is not null)
            Status = reading.Status;
        if (Connected != true)
            return false;
        if (reading.Sample is not { } sample)
        {
            if (authoritative)
                IsLast = Sample is not null;
            return false;
        }
        if (sample.ReceivedAt is { } received && Sample?.ReceivedAt > received)
            return false;

        // A Windows cache observation has no device measurement timestamp. A change
        // observed during one connection can trigger a transition, but cannot date it.
        var changedWindows = sample.ReceivedAt is null && previousWindowsLevel is int previous
            && sample.Percent != previous;
        if (sample.ReceivedAt is null)
        {
            previousWindowsLevel = sample.Percent;
            if ((freshInConnection && !IsLast)
                || (Sample?.ReceivedAt is not null && Sample.Percent == sample.Percent))
                return false;
        }
        else
        {
            freshInConnection = true;
        }

        Sample = sample;
        IsLast = false;
        if (sample.ReceivedAt is null && !changedWindows)
            return false;
        if (sample.Percent > lowThreshold)
        {
            lowAlertShown = false;
            return false;
        }
        if (lowAlertShown || sample.Charging == true)
            return false;
        lowAlertShown = true;
        return true;
    }
}
