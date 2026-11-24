using BatteryDesk.Core;

namespace BatteryDesk.Tests;

public sealed class DareuKeyboardTests
{
    [Theory]
    [InlineData(47, 100, 1, 47, true)]
    [InlineData(3, 8, 0, 37, false)]
    [InlineData(100, 100, 1, 100, true)]
    [InlineData(0, 100, 3, 0, null)]
    public void DecodesLevelAndCharging(byte level, byte max, byte status, int percent, bool? charging)
    {
        var time = DateTimeOffset.UtcNow;
        var sample = Assert.IsType<BatterySample>(DareuKeyboard.Decode(Reply(level, max, status), time));
        Assert.Equal(percent, sample.Percent);
        Assert.Equal(charging, sample.Charging);
        Assert.Equal(BatterySource.DareuUsb, sample.Source);
        Assert.Equal(time, sample.ReceivedAt);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(101, 100)]
    public void RejectsInvalidLevels(byte level, byte max) =>
        Assert.Null(DareuKeyboard.Decode(Reply(level, max, 1), DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void RejectsUnrelatedOrUnreadyReports(int field)
    {
        var report = Reply(42, 100, 1);
        report[field] = 0;
        Assert.Null(DareuKeyboard.Decode(report, DateTimeOffset.UtcNow));
        Assert.Null(DareuKeyboard.Decode(report.AsSpan(0, 10), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CableBatteryReadAvoidsTheKeyboardAndAlternateConsumerCollections()
    {
        Assert.True(DareuKeyboard.IsKeyboardPath(@"\\?\HID#VID_260D&PID_0046&MI_03#instance"));
        Assert.False(DareuKeyboard.IsKeyboardPath(@"\\?\HID#VID_260D&PID_0046&MI_02&COL02#instance"));
        Assert.False(DareuKeyboard.IsKeyboardPath(@"\\?\HID#VID_260D&PID_0046&MI_01#instance"));
    }

    [Fact]
    public void LowBatteryAlertWaitsUntilTheDeviceStopsCharging()
    {
        var state = new BatteryState();
        state.SetConnection(true);
        var sample = new BatterySample(9, BatterySource.DareuUsb, DateTimeOffset.UtcNow, true);
        Assert.False(state.Apply(Reading.Success(sample), true));
        Assert.True(state.Apply(Reading.Success(sample with { Charging = false }), true));
    }

    private static byte[] Reply(byte level, byte max, byte status)
    {
        var report = new byte[65];
        new byte[] { 0x0A, 0x02, 0x04, 0x07, 0x80, 0, 0, status, level, max }.CopyTo(report, 0);
        return report;
    }
}
