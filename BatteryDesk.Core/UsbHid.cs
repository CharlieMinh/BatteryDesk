using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryDesk.Core;

internal static partial class UsbHid
{
    private static readonly Guid HidClass = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    public static string[] Discover(Func<string, bool> predicate)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var status = GetInterfaceListSize(out var size, in HidClass, 0, 0);
            if (status != 0)
                throw new IOException($"HID enumeration failed (0x{status:X}).");
            if (size <= 1)
                return [];
            var buffer = new char[size];
            status = GetInterfaceList(in HidClass, 0, buffer, size, 0);
            if (status == 0x1A) // CR_BUFFER_SMALL
                continue;
            if (status != 0)
                throw new IOException($"HID enumeration failed (0x{status:X}).");
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(predicate).ToArray();
        }
        throw new IOException("HID interface list changed repeatedly.");
    }

    public static SafeFileHandle Open(string path, bool overlapped) =>
        CreateFile(path, 0xC0000000, 3, 0, 3, overlapped ? 0x40000000u : 0, 0);

    public static bool TryGetCapabilities(SafeFileHandle handle, out HidCapabilities caps)
    {
        caps = default;
        if (!GetPreparsedData(handle, out var data))
            return false;
        try { return GetCaps(data, out caps) == 0x00110000; }
        finally { FreePreparsedData(data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HidCapabilities
    {
        public ushort Usage, UsagePage, InputReportLength, OutputReportLength, FeatureReportLength;
        private fixed ushort remaining[27];
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial uint GetInterfaceListSize(out uint length, in Guid classId, nint device, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetInterfaceList(in Guid classId, nint device,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] char[] buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [LibraryImport("hid.dll", EntryPoint = "HidD_GetPreparsedData")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool GetPreparsedData(SafeFileHandle handle, out nint data);

    [LibraryImport("hid.dll", EntryPoint = "HidD_FreePreparsedData")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool FreePreparsedData(nint data);

    [LibraryImport("hid.dll", EntryPoint = "HidP_GetCaps")]
    private static partial int GetCaps(nint data, out HidCapabilities capabilities);
}
