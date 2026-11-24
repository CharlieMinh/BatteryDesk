using BatteryDesk.Core;

namespace BatteryDesk.Tests;

public sealed class BatteryStateTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-23T10:00:00Z");

    [Fact]
    public void ZeroIsValidAndLowAlertRearmsAfterRecovery()
    {
        var state = ConnectedState();
        Assert.True(state.Apply(Direct(0), true));
        Assert.Equal(0, state.Sample!.Percent);
        Assert.False(state.Apply(Direct(15, 1), true));
        Assert.False(state.Apply(Direct(16, 2), true));
        Assert.True(state.Apply(Direct(15, 3), true));
    }

    [Fact]
    public void CacheDoesNotRetimestampOrReplaceConfirmedSample()
    {
        var state = ConnectedState();
        state.Apply(Direct(45), true);
        Assert.False(state.Apply(Cache(55), false));
        Assert.Equal(45, state.Sample!.Percent);
        Assert.Equal(Start, state.Sample.ReceivedAt);
    }

    [Fact]
    public void FirstCachedValueDoesNotAlertButAChangeCanCrossThreshold()
    {
        var state = ConnectedState();
        Assert.False(state.Apply(Cache(10), true));
        Assert.Null(state.Sample!.ReceivedAt);
        Assert.False(state.Apply(Cache(50), true));
        Assert.True(state.Apply(Cache(15), true));
        Assert.False(state.Apply(Cache(10), true));
        Assert.Null(state.Sample!.ReceivedAt);
    }

    [Fact]
    public void ReadFailureAndDisconnectPreserveLastValue()
    {
        var state = ConnectedState();
        state.Apply(Direct(50), true);
        state.Apply(new Reading(ReadStatus.Unavailable, null, "timeout"), true);
        state.Apply(Cache(50), false);
        Assert.True(state.IsLast);
        Assert.Equal(50, state.Sample!.Percent);
        Assert.Equal(Start, state.Sample.ReceivedAt);
        state.SetConnection(false);
        Assert.False(state.Apply(Direct(5, 1), true));
        Assert.Equal(50, state.Sample.Percent);
        state.SetConnection(true);
        Assert.True(state.IsLast);
        state.Apply(Direct(55, 2), true);
        Assert.False(state.IsLast);
        Assert.Equal(55, state.Sample!.Percent);
    }

    [Fact]
    public void CustomThresholdAppliesToConfirmedSamples()
    {
        var state = new BatteryState(25);
        state.SetConnection(true);
        Assert.False(state.Apply(Direct(26), true));
        Assert.True(state.Apply(Direct(25, 1), true));
    }

    [Fact]
    public void ReplayedOlderNotificationCannotOverwriteCurrentReading()
    {
        var state = ConnectedState();
        state.Apply(Direct(51, 5), true);
        var older = new BatterySample(5, BatterySource.GattNotification, Start);
        Assert.False(state.Apply(Reading.Success(older), true));
        Assert.Equal(51, state.Sample!.Percent);
    }

    [Fact]
    public void IdentityUsesContainerAndKeepsEqualNamesSeparate()
    {
        var common = Guid.NewGuid();
        Endpoint[] endpoints =
        [
            new("1", "Same Name", Transport.LowEnergy, true, common, null, null),
            new("2", "Same Name", Transport.Classic, false, common, null, null),
            new("3", "Same Name", Transport.LowEnergy, false, Guid.NewGuid(), null, null)
        ];
        var grouped = WindowsBluetooth.Group(endpoints);
        Assert.Equal(2, grouped.Length);
        var dual = Assert.Single(grouped, device => device.Endpoints.Length == 2);
        Assert.True(dual.Connected);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(101, null)]
    public void ValidateBatteryLevel(int incoming, int? expected) =>
        Assert.Equal(expected, WindowsBluetooth.ParseLevel(incoming));

    private static BatteryState ConnectedState()
    {
        var state = new BatteryState();
        state.SetConnection(true);
        return state;
    }

    private static Reading Direct(int value, int seconds = 0) =>
        Reading.Success(new BatterySample(value, BatterySource.GattRead, Start.AddSeconds(seconds)));

    private static Reading Cache(int value) =>
        Reading.Success(new BatterySample(value, BatterySource.WindowsBluetooth, null));
}
