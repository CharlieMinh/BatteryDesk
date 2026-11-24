using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;

namespace BatteryDesk.Core;

public enum LogitechBatteryFeature : ushort { Status = 0x1000, Unified = 0x1004 }

public sealed record ReceiverReading(string? DeviceName, bool? Connected, Reading Battery,
    LogitechBatteryFeature? Feature = null, bool TimedOut = false);

public static class LightspeedReceiver
{
    public const string UsbId = "046D:C53F";
    public const string DisplayName = "Logitech LIGHTSPEED Receiver";
    private const int ReportSize = 20;
    private const byte Slot = 1;
    private const ushort UsagePage = 0xFF00;
    private const ushort Usage = 2;
    private const ushort VendorId = 0x046D;
    private const ushort ProductId = 0xC53F;
    private static int softwareId = Random.Shared.Next(15);

    public static DeviceWatcher Watch() =>
        DeviceInformation.CreateWatcher(HidDevice.GetDeviceSelector(UsagePage, Usage, VendorId, ProductId));

    public static bool IsReceiverPath(string path) =>
        path.StartsWith(@"\\?\hid#vid_046d&pid_c53f&mi_02&col02#", StringComparison.OrdinalIgnoreCase);

    public static string[] Discover() => UsbHid.Discover(IsReceiverPath);

    public static async Task<ReceiverReading> ReadAsync(string path, bool readName, CancellationToken cancellationToken,
        LogitechBatteryFeature? preferred = null, bool retryInitialTimeout = false)
    {
        if (!IsReceiverPath(path))
            throw new ArgumentException("HID interface is outside the supported receiver scope.", nameof(path));

        var result = await ReadOnceAsync(path, readName, preferred, cancellationToken).ConfigureAwait(false);
        if (!retryInitialTimeout || !result.TimedOut || result.Connected is not null)
            return result;
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        return await ReadOnceAsync(path, readName, preferred, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ReceiverReading> ReadOnceAsync(string path, bool readName,
        LogitechBatteryFeature? preferred, CancellationToken cancellationToken)
    {
        using var handle = UsbHid.Open(path, overlapped: true);
        if (handle.IsInvalid)
            return Failure(null, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        if (!UsbHid.TryGetCapabilities(handle, out var caps) || caps.UsagePage != UsagePage || caps.Usage != Usage
            || caps.InputReportLength != ReportSize || caps.OutputReportLength != ReportSize)
            return Failure(null, "Unexpected receiver HID usage or report lengths.");

        using var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
        var link = new Protocol(stream);
        var identified = false;
        try
        {
            var feature = preferred == LogitechBatteryFeature.Status
                ? LogitechBatteryFeature.Status : LogitechBatteryFeature.Unified;
            var index = await link.GetFeatureAsync((ushort)feature, cancellationToken).ConfigureAwait(false);
            identified = true;
            var percent = await link.ReadPercentAsync(feature, index, cancellationToken).ConfigureAwait(false);
            if (percent is null)
            {
                feature = feature == LogitechBatteryFeature.Status
                    ? LogitechBatteryFeature.Unified : LogitechBatteryFeature.Status;
                index = await link.GetFeatureAsync((ushort)feature, cancellationToken).ConfigureAwait(false);
                percent = await link.ReadPercentAsync(feature, index, cancellationToken).ConfigureAwait(false);
            }

            var battery = percent is int level
                ? new Reading(ReadStatus.Success, new BatterySample(level, BatterySource.LogitechHidPlusPlus,
                    DateTimeOffset.UtcNow), $"HID++ 0x{(ushort)feature:X4}")
                : new Reading(ReadStatus.Unsupported, null, "No HID++ battery percentage available.");
            string? name = null;
            if (readName)
            {
                try { name = await link.ReadNameAsync(cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { }
                catch (IOException) { }
            }
            return new ReceiverReading(name, true, battery, percent is null ? null : feature);
        }
        catch (TimeoutException)
        {
            return Failure(identified ? true : null, "No HID++ reply. The device may be asleep.") with { TimedOut = true };
        }
        catch (IOException error) { return Failure(identified ? true : null, error.Message); }
    }

    private static ReceiverReading Failure(bool? connected, string detail) =>
        new(null, connected, new Reading(ReadStatus.Unavailable, null, detail));

    public static int? Percent(byte value) => value <= 100 ? value : null;

    public static bool MatchesResponse(ReadOnlySpan<byte> frame, byte feature, byte address, out byte? error)
    {
        error = null;
        if (frame.Length != ReportSize || frame[0] != 0x11 || frame[1] != Slot)
            return false;
        if (frame[2] == 0xFF && frame[3] == feature && frame[4] == address)
        {
            error = frame[5];
            return true;
        }
        return frame[2] == feature && frame[3] == address;
    }

    private sealed class Protocol(FileStream stream)
    {
        private readonly byte[] output = new byte[ReportSize];
        private readonly byte[] input = new byte[ReportSize];

        public async Task<byte> GetFeatureAsync(ushort id, CancellationToken token)
        {
            var response = await QueryAsync(0, 0, (byte)(id >> 8), (byte)id, token).ConfigureAwait(false);
            return response[4];
        }

        public async Task<int?> ReadPercentAsync(LogitechBatteryFeature feature, byte index, CancellationToken token)
        {
            if (index == 0)
                return null;
            byte function = 0;
            if (feature == LogitechBatteryFeature.Unified)
            {
                var capabilities = await QueryAsync(index, 0, 0, 0, token).ConfigureAwait(false);
                if ((capabilities[5] & 2) == 0) // Device only supplies coarse battery levels.
                    return null;
                function = 1;
            }
            var state = await QueryAsync(index, function, 0, 0, token).ConfigureAwait(false);
            return Percent(state[4]);
        }

        public async Task<string?> ReadNameAsync(CancellationToken token)
        {
            var feature = await GetFeatureAsync(0x0005, token).ConfigureAwait(false);
            if (feature == 0)
                return null;
            var reply = await QueryAsync(feature, 0, 0, 0, token).ConfigureAwait(false);
            var length = reply[4];
            if (length is 0 or > 64)
                return null;
            var bytes = new byte[length];
            for (byte offset = 0; offset < length; offset += 16)
            {
                reply = await QueryAsync(feature, 1, offset, 0, token).ConfigureAwait(false);
                Array.Copy(reply, 4, bytes, offset, Math.Min(16, length - offset));
            }

            var name = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
            return name.Length > 0 && !name.Any(char.IsControl) ? name : null;
        }

        public async Task<byte[]> QueryAsync(byte feature, byte function, byte first, byte second, CancellationToken token)
        {
            var id = ((uint)Interlocked.Increment(ref softwareId) % 15) + 1;
            var address = (byte)((function << 4) | (byte)id);
            output[0] = 0x11;
            output[1] = Slot;
            output[2] = feature;
            output[3] = address;
            output[4] = first;
            output[5] = second;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(650));
            try
            {
                await stream.WriteAsync(output, deadline.Token).ConfigureAwait(false);
                while (true)
                {
                    var read = await stream.ReadAsync(input, deadline.Token).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException("Receiver closed the HID connection.");
                    if (!MatchesResponse(input.AsSpan(0, read), feature, address, out var error))
                        continue;
                    if (error is byte code)
                        throw new IOException($"Receiver returned HID++ error 0x{code:X2}.");
                    return input;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
        }
    }

}
