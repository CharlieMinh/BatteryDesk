using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BatteryDesk;

internal static partial class LayeredSurface
{
    public static bool Paint(nint window, Size size, int dpi, Action<Graphics> render)
    {
        var context = CreateCompatibleDC(0);
        if (context == 0)
            return false;

        nint bitmap = 0;
        nint previous = 0;
        try
        {
            BitmapHeader header = new()
            {
                Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = size.Width, Height = -size.Height,
                Planes = 1, BitCount = 32
            };
            bitmap = CreateDIBSection(context, in header, 0, out var pixels, 0, 0);
            if (bitmap == 0 || pixels == 0)
                return false;
            previous = SelectObject(context, bitmap);
            if (previous == 0 || previous == -1)
                return false;

            using (var surface = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, pixels))
            {
                surface.SetResolution(dpi, dpi);
                using var graphics = Graphics.FromImage(surface);
                graphics.Clear(Color.Transparent);
                render(graphics);
            }

            NativePair extent = new() { X = size.Width, Y = size.Height };
            NativePair origin = default;
            Blend blend = new() { SourceConstantAlpha = 255, AlphaFormat = 1 };
            return UpdateLayeredWindow(window, 0, 0, in extent, context, in origin, 0, in blend, 2);
        }
        finally
        {
            if (previous != 0 && previous != -1)
                SelectObject(context, previous);
            if (bitmap != 0)
                DeleteObject(bitmap);
            DeleteDC(context);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePair { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blend { public byte Operation, Flags, SourceConstantAlpha, AlphaFormat; }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint device);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint context, in BitmapHeader header, uint usage, out nint pixels,
        nint mapping, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint context, nint graphicsObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint graphicsObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint context);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(nint window, nint destinationContext, nint destination,
        in NativePair size, nint sourceContext, in NativePair source, uint colorKey, in Blend blend, uint flags);
}
