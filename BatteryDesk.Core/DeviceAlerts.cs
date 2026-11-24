namespace BatteryDesk.Core;

[Flags]
public enum BatteryAlert { None = 0, Low = 1, ChargeTarget = 2 }

public sealed class DeviceAlerts
{
    private bool lowShown;
    private bool targetShown;
    private string? armedEndpoint;
    private bool disconnected;
    private int? target;
    private int? lowThreshold;
    private int? lastWindowsLevel;
    private string? lastWindowsEndpoint;
    private BatterySample? lastSample;
    private string? lastEndpoint;

    public BatteryAlert Observe(DeviceObservation observation, int low, int? chargeTarget)
    {
        if (target != chargeTarget)
        {
            target = chargeTarget;
            targetShown = false;
            armedEndpoint = null;
            lastSample = null;
        }
        if (lowThreshold != low)
        {
            lowThreshold = low;
            lowShown = false;
            lastSample = null;
        }
        if (observation.Connected != true || observation.IsLast || observation.Sample is null)
        {
            if (observation.Connected == false)
            {
                disconnected = true;
                armedEndpoint = null;
                lastWindowsLevel = null;
            }
            return BatteryAlert.None;
        }

        var sample = observation.Sample;
        var endpoint = observation.Endpoint.Key;
        if (lastEndpoint == endpoint && lastSample == sample)
            return BatteryAlert.None;
        lastEndpoint = endpoint;
        lastSample = sample;

        var result = BatteryAlert.None;
        var credibleForLow = observation.Fresh;
        if (!credibleForLow)
        {
            credibleForLow = lastWindowsEndpoint == endpoint && lastWindowsLevel is { } previous && previous != sample.Percent;
            lastWindowsEndpoint = endpoint;
            lastWindowsLevel = sample.Percent;
        }
        if (credibleForLow && sample.Percent > low + 2)
            lowShown = false;
        if (credibleForLow && !lowShown && sample.Percent <= low && sample.Charging != true)
        {
            lowShown = true;
            result = BatteryAlert.Low;
        }

        if (!observation.Fresh || target is not int level)
            return result;

        // A small drop during the same charge or a replug at full does not start a new alert.
        if (sample.Percent <= level - 3 && (sample.Charging == false || disconnected))
            targetShown = false;
        disconnected = false;
        if (sample.Charging == true && sample.Percent < level && !targetShown)
            armedEndpoint = endpoint;
        if (!targetShown && armedEndpoint == endpoint && sample.Charging is not null && sample.Percent >= level)
        {
            targetShown = true;
            armedEndpoint = null;
            result |= BatteryAlert.ChargeTarget;
        }
        if (sample.Charging == false && armedEndpoint == endpoint)
            armedEndpoint = null;
        return result;
    }
}
