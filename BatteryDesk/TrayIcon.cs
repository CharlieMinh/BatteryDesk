using System.Runtime.InteropServices;

namespace BatteryDesk;

internal static partial class TrayIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var border = new Pen(Color.White, 2);
        using var fill = new SolidBrush(Color.FromArgb(129, 231, 189));
        graphics.DrawRectangle(border, 3, 8, 23, 16);
        graphics.FillRectangle(Brushes.White, 27, 13, 3, 6);
        graphics.FillRectangle(fill, 7, 12, 12, 8);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
