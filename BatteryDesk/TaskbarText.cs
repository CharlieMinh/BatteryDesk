using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BatteryDesk;

// Render hinted GDI glyphs as grayscale coverage for the premultiplied taskbar surface.
internal sealed partial class TaskbarText : IDisposable
{
    private readonly Dictionary<Glyph, Bitmap> cache = [];
    private int dpi;

    public void Draw(Graphics graphics, string text, string family, int logicalHeight, int weight, Color color,
        Rectangle bounds, int deviceDpi)
    {
        if (deviceDpi != dpi || cache.Count >= 96)
        {
            Dispose();
            dpi = deviceDpi;
        }

        var glyph = new Glyph(text, family, (logicalHeight * dpi + 48) / 96, weight, color, bounds.Size);
        if (!cache.TryGetValue(glyph, out var image))
        {
            image = Rasterize(glyph);
            if (image is null)
                return;
            cache.Add(glyph, image);
        }
        graphics.DrawImage(image, bounds, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
    }

    private static Bitmap? Rasterize(Glyph glyph)
    {
        using var mask = new Bitmap(glyph.Size.Width, glyph.Size.Height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(mask))
        {
            graphics.Clear(Color.Black);
            var dc = graphics.GetHdc();
            try
            {
                const uint defaultCharset = 1, grayscaleAntialias = 4;
                var font = CreateFont(-glyph.PixelHeight, 0, 0, 0, glyph.Weight, 0, 0, 0, defaultCharset,
                    0, 0, grayscaleAntialias, 0, glyph.Family);
                if (font == 0)
                    return null;
                var previous = SelectObject(dc, font);
                try
                {
                    if (previous == 0 || previous == -1)
                        return null;
                    SetTextColor(dc, 0x00FFFFFF);
                    SetBkMode(dc, 1); // TRANSPARENT; the black bitmap holds the coverage.
                    TextRect rectangle = new() { Right = glyph.Size.Width, Bottom = glyph.Size.Height };
                    const uint verticallyCentered = 0x0004, singleLine = 0x0020, noMnemonic = 0x0800;
                    DrawText(dc, glyph.Text, glyph.Text.Length, in rectangle, verticallyCentered | singleLine | noMnemonic);
                    GdiFlush();
                }
                finally
                {
                    if (previous != 0 && previous != -1)
                        SelectObject(dc, previous);
                    DeleteObject(font);
                }
            }
            finally { graphics.ReleaseHdc(dc); }
        }

        var image = new Bitmap(mask.Width, mask.Height, PixelFormat.Format32bppPArgb);
        var bounds = new Rectangle(Point.Empty, mask.Size);
        var source = mask.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var target = image.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { ApplyCoverage(source, target, glyph.Color); }
            finally { image.UnlockBits(target); }
        }
        finally { mask.UnlockBits(source); }
        return image;
    }

    private static unsafe void ApplyCoverage(BitmapData source, BitmapData target, Color color)
    {
        for (var y = 0; y < source.Height; y++)
        {
            var coverage = (byte*)source.Scan0 + y * source.Stride;
            var pixels = (byte*)target.Scan0 + y * target.Stride;
            for (var x = 0; x < source.Width; x++, coverage += 4, pixels += 4)
            {
                var alpha = coverage[1];
                pixels[0] = (byte)((color.B * alpha + 127) / 255);
                pixels[1] = (byte)((color.G * alpha + 127) / 255);
                pixels[2] = (byte)((color.R * alpha + 127) / 255);
                pixels[3] = alpha;
            }
        }
    }

    public void Dispose()
    {
        foreach (var image in cache.Values)
            image.Dispose();
        cache.Clear();
    }

    private readonly record struct Glyph(string Text, string Family, int PixelHeight, int Weight, Color Color, Size Size);

    [StructLayout(LayoutKind.Sequential)]
    private struct TextRect { public int Left, Top, Right, Bottom; }

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charset, uint outputPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint handle);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GdiFlush();

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DrawText(nint dc, string text, int count, in TextRect rectangle, uint format);
}
