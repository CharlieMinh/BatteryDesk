namespace BatteryDesk.Core;

public sealed record HistoryPoint(string EndpointKey, DateTimeOffset At, int? Percent, bool? Charging,
    BatterySource? Source, Guid Segment);

public sealed class BatteryHistory
{
    public const int RetainedDays = 7;
    private const int MaxPoints = 12_000;
    private Guid segment = Guid.NewGuid();
    private readonly List<HistoryPoint> points;
    private readonly Dictionary<string, HistoryPoint> latest = new(StringComparer.Ordinal);
    public bool Dirty { get; private set; }
    public bool Enabled { get; private set; }

    public BatteryHistory(bool enabled, IEnumerable<HistoryPoint> saved, DateTimeOffset now)
    {
        Enabled = enabled;
        points = saved.Where(point => point.At >= now.AddDays(-RetainedDays) && point.At <= now)
            .OrderBy(point => point.At).TakeLast(MaxPoints).ToList();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled != Enabled)
        {
            latest.Clear();
            segment = Guid.NewGuid();
        }
        Enabled = enabled;
    }

    public void Observe(IReadOnlyList<DeviceObservation> current, DateTimeOffset now)
    {
        if (!Enabled)
            return;
        var seen = current.Select(item => item.Endpoint.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var previous in latest.Values.Where(point => !seen.Contains(point.EndpointKey) && point.Percent is not null).ToArray())
            Add(new HistoryPoint(previous.EndpointKey, now, null, null, null, segment));
        foreach (var item in current)
        {
            var key = item.Endpoint.Key;
            var previous = latest.GetValueOrDefault(key);
            if (item.Fresh && item.Sample is { } sample)
            {
                var at = sample.ReceivedAt!.Value;
                if (at > now || at < now.AddDays(-RetainedDays) || (previous is not null && at <= previous.At))
                    continue;
                // A sparse heartbeat confirms long stretches at the same charge level.
                if (previous is null || previous.Percent != sample.Percent || previous.Charging != sample.Charging
                    || previous.Source != sample.Source || at - previous.At >= TimeSpan.FromMinutes(10))
                    Add(new HistoryPoint(key, at, sample.Percent, sample.Charging, sample.Source, segment));
            }
            else if (previous?.Percent is not null)
                Add(new HistoryPoint(key, now, null, null, null, segment));
        }
    }

    public void Clear()
    {
        points.Clear();
        latest.Clear();
        Dirty = true;
    }

    public HistoryPoint[] Read(IEnumerable<string> endpoints, DateTimeOffset since)
    {
        var keys = endpoints.ToHashSet(StringComparer.Ordinal);
        return points.Where(point => point.At >= since && keys.Contains(point.EndpointKey))
            .OrderBy(point => point.At).ToArray();
    }

    public HistoryPoint[] Snapshot(DateTimeOffset now)
    {
        points.RemoveAll(point => point.At < now.AddDays(-RetainedDays));
        return points.OrderBy(point => point.At).ToArray();
    }

    public void MarkSaved() => Dirty = false;

    private void Add(HistoryPoint point)
    {
        latest[point.EndpointKey] = point;
        points.Add(point);
        if (points.Count > MaxPoints)
            points.RemoveRange(0, points.Count - MaxPoints);
        Dirty = true;
    }
}
