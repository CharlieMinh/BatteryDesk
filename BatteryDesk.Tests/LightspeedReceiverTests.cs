using BatteryDesk.Core;

namespace BatteryDesk.Tests;

public sealed class LightspeedReceiverTests
{
    [Fact]
    public void OnlyTheValidatedReceiverCollectionIsSelected()
    {
        Assert.True(LightspeedReceiver.IsReceiverPath(@"\\?\HID#VID_046D&PID_C53F&MI_02&COL02#instance"));
        Assert.False(LightspeedReceiver.IsReceiverPath(@"\\?\HID#VID_046D&PID_C53F&MI_02&COL03#instance"));
        Assert.False(LightspeedReceiver.IsReceiverPath(@"\\?\HID#VID_046D&PID_C53D&MI_02&COL02#instance"));
    }

    [Fact]
    public void OnlyTheReplyToTheOutstandingQueryMatches()
    {
        var frame = new byte[20];
        frame[0] = 0x11;
        frame[1] = 1;
        frame[2] = 0x12;
        frame[3] = 0x15;
        Assert.True(LightspeedReceiver.MatchesResponse(frame, 0x12, 0x15, out var error));
        Assert.Null(error);
        Assert.False(LightspeedReceiver.MatchesResponse(frame, 0x12, 0x16, out _));
        Assert.False(LightspeedReceiver.MatchesResponse(frame, 0x13, 0x15, out _));
        Assert.False(LightspeedReceiver.MatchesResponse(frame.AsSpan(0, 7), 0x12, 0x15, out _));
        frame[1] = 2;
        Assert.False(LightspeedReceiver.MatchesResponse(frame, 0x12, 0x15, out _));
    }

    [Fact]
    public void ProtocolErrorCannotBecomeBatterySample()
    {
        var frame = new byte[20];
        frame[0] = 0x11;
        frame[1] = 1;
        frame[2] = 0xFF;
        frame[3] = 0x12;
        frame[4] = 0x15;
        frame[5] = 0x08;
        Assert.True(LightspeedReceiver.MatchesResponse(frame, 0x12, 0x15, out var error));
        Assert.Equal<byte?>(0x08, error);
        Assert.False(LightspeedReceiver.MatchesResponse(frame, 0x12, 0x16, out _));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(101, null)]
    [InlineData(255, null)]
    public void InvalidBatteryBytesDoNotBecomePercentages(byte raw, int? expected) =>
        Assert.Equal(expected, LightspeedReceiver.Percent(raw));

    [Fact]
    public void UnresponsiveMouseKeepsLastSampleUntilANewReply()
    {
        var state = new BatteryState();
        var received = DateTimeOffset.UtcNow;
        var original = new BatterySample(90, BatterySource.LogitechHidPlusPlus, received);
        state.SetConnection(true);
        Assert.False(state.Apply(Reading.Success(original), true));
        state.SetConnection(null);
        Assert.False(state.Apply(new Reading(ReadStatus.Unavailable, null, "No HID++ reply"), true));
        Assert.Same(original, state.Sample);
        Assert.True(state.IsLast);
        state.SetConnection(true);
        var fresh = original with { Percent = 89, ReceivedAt = received.AddMinutes(1) };
        Assert.False(state.Apply(Reading.Success(fresh), true));
        Assert.Same(fresh, state.Sample);
        Assert.False(state.IsLast);
    }
}
