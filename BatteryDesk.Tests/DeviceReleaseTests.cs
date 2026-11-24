using BatteryDesk.Core;

namespace BatteryDesk.Tests;

public sealed class DeviceReleaseTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DeviceEndpoint Bluetooth = new("bluetooth", "EK75 PRO", DeviceConnection.Bluetooth);
    private static readonly DeviceEndpoint SecondBluetooth = new("second", "EK75 PRO", DeviceConnection.Bluetooth);
    private static readonly DeviceEndpoint Cable = new("usb", "EK75 PRO", DeviceConnection.Cable, "260D:0046");

    [Fact]
    public void TwoEqualNamesAndDifferentTransportsStaySeparateUntilLinked()
    {
        var directory = new DeviceDirectory();
        directory.Observe([Sample(Bluetooth, 48), Sample(SecondBluetooth, 51), Sample(Cable, 49, true)]);
        Assert.Equal(3, directory.Views().Length);
        Assert.False(directory.Link(Bluetooth.Key, SecondBluetooth.Key));
        Assert.True(directory.Link(Bluetooth.Key, Cable.Key));
        var group = Assert.Single(directory.Views(), item => item.Profile.Id == Bluetooth.Key);
        Assert.Equal(2, group.Profile.Endpoints.Length);
        Assert.Equal(Cable, group.Display.Endpoint);
        Assert.Equal(49, group.Display.Sample!.Percent);
        Assert.True(directory.Unlink(group.Profile.Id, Cable.Key));
        Assert.Equal(3, directory.Views().Length);
    }

    [Fact]
    public void OverviewKeepsOneEk75AcrossCableRemovalAndBluetoothReconnect()
    {
        var directory = new DeviceDirectory();
        directory.Observe([Sample(Bluetooth, 47).Offline(), Sample(Cable, 49, true, 1)]);
        var charging = Assert.Single(DeviceDirectory.Overview(directory.Views())).Display;
        Assert.Equal(Cable, charging.Endpoint);
        Assert.True(charging.Sample!.Charging);

        directory.Observe([]);
        var last = Assert.Single(DeviceDirectory.Overview(directory.Views())).Display;
        Assert.True(last.IsLast);
        Assert.Equal(49, last.Sample!.Percent);

        directory.Observe([Sample(Bluetooth, 50, seconds: 2)]);
        var wireless = Assert.Single(DeviceDirectory.Overview(directory.Views())).Display;
        Assert.Equal(Bluetooth, wireless.Endpoint);
        Assert.Equal(50, wireless.Sample!.Percent);
        Assert.Null(wireless.Sample.Charging);
        Assert.Equal(2, directory.Views().Length);

        directory.Observe([Sample(Cable, 51, true, 3)]);
        Assert.Equal(Cable, Assert.Single(DeviceDirectory.Overview(directory.Views())).Display.Endpoint);
    }

    [Fact]
    public void OverviewKeepsDistinctOnlineOrAmbiguousEk75Devices()
    {
        var directory = new DeviceDirectory();
        directory.Observe([Sample(Bluetooth, 48), Sample(Cable, 81, true)]);
        Assert.Equal(2, DeviceDirectory.Overview(directory.Views()).Count());

        directory.Observe([Sample(Bluetooth, 48), Sample(SecondBluetooth, 55)]);
        Assert.Equal(3, DeviceDirectory.Overview(directory.Views()).Count());
    }

    [Fact]
    public void OverviewDoesNotCollapseOtherModelsOrUnverifiedCableIds()
    {
        var directory = new DeviceDirectory();
        directory.Observe([Sample(Bluetooth, 48), Sample(Cable with { UsbId = "1234:5678" }, 81).Offline(),
            Sample(Bluetooth with { Key = "other-bluetooth", Model = "Other keyboard" }, 52),
            Sample(Cable with { Key = "other-cable", Model = "Other keyboard" }, 62).Offline()]);
        Assert.Equal(4, DeviceDirectory.Overview(directory.Views()).Count());
    }

    [Fact]
    public void SwitchingToAnUnreadCableKeepsTheOldSampleAndDoesNotCallItFresh()
    {
        var directory = new DeviceDirectory();
        directory.Observe([Sample(Bluetooth, 48),
            new DeviceObservation(Cable, false, null, false, ReadStatus.Offline)]);
        Assert.True(directory.Link(Bluetooth.Key, Cable.Key));
        directory.Observe([new DeviceObservation(Cable, true, null, false, null)]);
        var display = Assert.Single(directory.Views()).Display;
        Assert.Equal(48, display.Sample!.Percent);
        Assert.Equal(BatterySource.GattRead, display.Sample.Source);
        Assert.True(display.IsLast);
        directory.Observe([Sample(Cable, 49, true, 5)]);
        Assert.True(Assert.Single(directory.Views()).Display.Fresh);
        directory.Observe([]);
        Assert.True(Assert.Single(directory.Views()).Display.IsLast);
    }

    [Fact]
    public void LinkingAndReloadingPreservesAnchorSettingsAndOrdering()
    {
        var directory = new DeviceDirectory();
        directory.Observe([Sample(Bluetooth, 48), Sample(SecondBluetooth, 51), Sample(Cable, 49, true)]);
        directory.Edit(Bluetooth.Key, new DevicePreferences("Bàn phím chính", true, true, 25, 90));
        Assert.True(directory.Link(Bluetooth.Key, Cable.Key));
        var saved = directory.Profiles;
        var reloaded = new DeviceDirectory(saved);
        reloaded.Observe([Sample(Cable, 55, true)]);
        var first = reloaded.Views()[0];
        Assert.Equal("Bàn phím chính", first.Name);
        Assert.True(first.Profile.Preferences.Hidden);
        Assert.Equal(25, first.Profile.Preferences.Low);
        Assert.Equal(90, first.Profile.Preferences.ChargeTarget);
    }

    [Fact]
    public void DevicesCanBeReorderedWithinTheirPinGroup()
    {
        var directory = new DeviceDirectory();
        directory.Observe([Sample(Bluetooth, 48), Sample(SecondBluetooth, 51), Sample(Cable, 49, true)]);
        Assert.True(directory.Move(Cable.Key, -1));
        Assert.Equal(Cable.Key, directory.Views()[0].Profile.Id);
        directory.Edit(SecondBluetooth.Key, new DevicePreferences(Pinned: true));
        Assert.Equal(SecondBluetooth.Key, directory.Views()[0].Profile.Id);
    }

    [Fact]
    public void AtTargetOnStartupDoesNotAlertAndACompleteCycleAlertsOnlyOnce()
    {
        var alerts = new DeviceAlerts();
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 100, true), 15, 100));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 99, true, 5), 15, 100));
        Assert.Equal(BatteryAlert.ChargeTarget, alerts.Observe(Sample(Cable, 100, true, 10), 15, 100));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 99, true, 15), 15, 100));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 100, true, 20), 15, 100));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 100, true, 20).Offline(), 15, 100));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 100, true, 25), 15, 100));
    }

    [Fact]
    public void ConfirmedDischargeRearmsTheNextChargeSession()
    {
        var alerts = new DeviceAlerts();
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 77, true), 15, 80));
        Assert.Equal(BatteryAlert.ChargeTarget, alerts.Observe(Sample(Cable, 80, true, 5), 15, 80));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 76, false, 10), 15, 80));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 78, true, 15), 15, 80));
        Assert.Equal(BatteryAlert.ChargeTarget, alerts.Observe(Sample(Cable, 80, false, 20), 15, 80));
    }

    [Fact]
    public void CachedOrStaleSamplesDoNotFireChargeAlerts()
    {
        var alerts = new DeviceAlerts();
        alerts.Observe(Sample(Cable, 79, true), 15, 80);
        var cached = Sample(Cable, 80, true, 5);
        Assert.Equal(BatteryAlert.None,
            alerts.Observe(cached with { Sample = cached.Sample! with { ReceivedAt = null } }, 15, 80));
        Assert.Equal(BatteryAlert.None, alerts.Observe(cached with { IsLast = true }, 15, 80));
        Assert.Equal(BatteryAlert.ChargeTarget, alerts.Observe(Sample(Cable, 80, true, 10), 15, 80));
    }

    [Fact]
    public void PerDeviceLowThresholdUsesHysteresisAndIgnoresCharging()
    {
        var alerts = new DeviceAlerts();
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 19, true), 20, null));
        Assert.Equal(BatteryAlert.Low, alerts.Observe(Sample(Cable, 19, false, 5), 20, null));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 21, false, 10), 20, null));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 19, false, 15), 20, null));
        Assert.Equal(BatteryAlert.None, alerts.Observe(Sample(Cable, 24, false, 20), 20, null));
        Assert.Equal(BatteryAlert.Low, alerts.Observe(Sample(Cable, 19, false, 25), 20, null));
    }

    [Fact]
    public void HistoryRecordsOnlyFreshPointsAndMarksOfflineGaps()
    {
        var history = new BatteryHistory(true, [], Start);
        history.Observe([Sample(Cable, 50, true)], Start);
        history.Observe([Sample(Cable, 50, true, 5)], Start.AddSeconds(5));
        history.Observe([Sample(Cable, 50, true, 5).Offline()], Start.AddSeconds(6));
        history.Observe([], Start.AddSeconds(7));
        history.Observe([Sample(Cable, 52, true, 10)], Start.AddSeconds(10));
        var points = history.Read([Cable.Key], Start);
        Assert.Equal(new int?[] { 50, null, 52 }, points.Select(point => point.Percent));
        history.Clear();
        Assert.Empty(history.Read([Cable.Key], Start));
        Assert.True(history.Dirty);
    }

    [Fact]
    public void HistoryBreaksWhenRecordingIsPausedAndRetainsSevenDays()
    {
        var old = new HistoryPoint(Cable.Key, Start.AddDays(-8), 20, true, BatterySource.DareuUsb, Guid.NewGuid());
        var history = new BatteryHistory(true, [old], Start);
        history.Observe([Sample(Cable, 60, true)], Start);
        history.SetEnabled(false);
        history.Observe([Sample(Cable, 61, true, 5)], Start.AddSeconds(5));
        history.SetEnabled(true);
        history.Observe([Sample(Cable, 62, true, 10)], Start.AddSeconds(10));
        var points = history.Snapshot(Start.AddSeconds(10));
        Assert.Equal(new int?[] { 60, 62 }, points.Select(point => point.Percent));
        Assert.NotEqual(points[0].Segment, points[1].Segment);
    }

    [Fact]
    public void SupportSummaryContainsOnlyStructuredAndRedactedFields()
    {
        var endpoint = Cable with { Model = "EK75 PRO 12:34:56:78:9A:BC serial: X1024 MYPC testuser", Key = "raw-path-S001" };
        var view = new DeviceView(new DeviceProfile(endpoint.Key, [endpoint], new DevicePreferences("Private alias")),
            Sample(endpoint, 50) with { Status = ReadStatus.Unavailable });
        var report = SupportSummary.Create(view, "1.0.0-rc.1", "10.0.26100", "testuser", "MYPC");
        Assert.Contains("260D:0046", report);
        Assert.Contains("lỗi tạm thời", report);
        foreach (var privateValue in new[] { "12:34:56", "X1024", "MYPC", "testuser", "raw-path-S001", "Private alias" })
            Assert.DoesNotContain(privateValue, report, StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceObservation Sample(DeviceEndpoint endpoint, int percent, bool? charging = null, int seconds = 0) =>
        new(endpoint, true, new BatterySample(percent,
            endpoint.Connection == DeviceConnection.Cable ? BatterySource.DareuUsb : BatterySource.GattRead,
            Start.AddSeconds(seconds), charging), false, ReadStatus.Success);
}
