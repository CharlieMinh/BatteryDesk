using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;

namespace BatteryDesk.Core;

public static partial class DareuKeyboard
{
    public const string Name = "EK75 PRO";
    public const string UsbId = "260D:0046";
    private const int ReportLength = 65;
    private const byte ReportId = 0x0A;
    private const byte BatteryClass = 0x07;
    private const byte GetBattery = 0x80;

    public static DeviceWatcher Watch() =>
        DeviceInformation.CreateWatcher(HidDevice.GetDeviceSelector(0x000C, 1, 0x260D, 0x0046));

    public static bool IsKeyboardPath(string path) =>
        path.StartsWith(@"\\?\hid#vid_260d&pid_0046&mi_03#", StringComparison.OrdinalIgnoreCase);

    public static string[] Discover() => UsbHid.Discover(IsKeyboardPath);

    public static async Task<Reading> ReadAsync(string path, CancellationToken token)
    {
        if (!IsKeyboardPath(path))
            throw new ArgumentException("HID interface is outside the supported keyboard scope.", nameof(path));
        token.ThrowIfCancellationRequested();
        using var handle = UsbHid.Open(path, overlapped: false);
        if (handle.IsInvalid)
            return new(ReadStatus.Unavailable, null, "Cannot open EK75 PRO USB collection.");
        if (!UsbHid.TryGetCapabilities(handle, out var caps) || caps.UsagePage != 0x000C || caps.Usage != 1
            || caps.FeatureReportLength != ReportLength)
            return new(ReadStatus.Unsupported, null, $"Unexpected EK75 PRO USB feature-report format ({caps.UsagePage:X4}:{caps.Usage:X4}, {caps.FeatureReportLength} bytes).");

        var report = new byte[ReportLength];
        report[0] = ReportId;
        // Togran slot 0: battery read. The HID request carries no configuration payload.
        report[2] = 4;
        report[3] = BatteryClass;
        report[4] = GetBattery;
        if (!SetFeature(handle, report, report.Length))
            return new(ReadStatus.Unavailable, null, "EK75 PRO did not accept the USB battery query.");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(25, token).ConfigureAwait(false);
            Array.Clear(report);
            report[0] = ReportId;
            if (!GetFeature(handle, report, report.Length))
                return new(ReadStatus.Unavailable, null, "Cannot read the EK75 PRO USB battery reply.");
            if (Decode(report, DateTimeOffset.UtcNow) is { } sample)
                return Reading.Success(sample);
        }
        return new(ReadStatus.Unavailable, null, "No valid EK75 PRO USB battery reply.");
    }

    public static BatterySample? Decode(ReadOnlySpan<byte> report, DateTimeOffset receivedAt)
    {
        if (report.Length != ReportLength || report[0] != ReportId || report[1] != 2
            || report[3] != BatteryClass || report[4] != GetBattery || report[9] == 0 || report[8] > report[9])
            return null;
        bool? charging = report[7] switch { 1 => true, 0 => false, _ => null };
        return new BatterySample(report[8] * 100 / report[9], BatterySource.DareuUsb, receivedAt, charging);
    }

    [LibraryImport("hid.dll", EntryPoint = "HidD_SetFeature")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetFeature(SafeFileHandle handle, byte[] report, int length);

    [LibraryImport("hid.dll", EntryPoint = "HidD_GetFeature")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool GetFeature(SafeFileHandle handle, [In, Out] byte[] report, int length);
}
