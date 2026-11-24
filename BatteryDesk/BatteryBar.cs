using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryDesk;

internal sealed partial class BatteryBar : Form
{
    private readonly ToolTip tips = new() { InitialDelay = 350 };
    private readonly System.Windows.Forms.Timer visibilityCheck = new() { Interval = 400 };
    private readonly FlowLayoutPanel strip = new() { Dock = DockStyle.Fill, WrapContents = false };
    private readonly int low;
    private readonly Font? fluentIcons = LoadIconFont();
    private readonly TaskbarText taskbarText = new();
    private Chip[] chips = [];
    private BatteryRow[] rows = [];
    private Palette palette;
    private bool layerDirty = true;
    public TaskbarStyle Style { get; private set; }
    public event Action? DetailsRequested;

    public BatteryBar(int low, TaskbarStyle style)
    {
        this.low = low;
        Style = style;
        Text = "BatteryDesk · Thanh pin";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Controls.Add(strip);
        ApplyTheme();
        DpiChanged += (_, _) => { layerDirty = true; PinToTaskbar(); };
        visibilityCheck.Tick += VisibilityTick;
        SetRows([]);
    }

    protected override bool ShowWithoutActivation => true;

    private void VisibilityTick(object? sender, EventArgs e)
    {
        DrawLayerIfNeeded();
        KeepVisible();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        visibilityCheck.Enabled = Visible;
        if (Visible)
        {
            layerDirty = true;
            PinToTaskbar();
            KeepVisible();
        }
    }

    public void KeepVisible()
    {
        if (!Visible || !IsHandleCreated)
            return;
        var center = new WindowPoint { X = Left + Width / 2, Y = Top + Height / 2 };
        var hit = WindowFromPoint(center);
        var taskbar = FindWindow("Shell_TrayWnd", null);
        // Explorer can raise its own topmost taskbar even when this window is still visible.
        // Only recover from Explorer itself so menus and other overlays can still take precedence.
        if (taskbar != 0 && (hit == taskbar || GetAncestor(hit, 2) == taskbar))
            SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x0013); // TOPMOST, no move/resize/activation.
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08080000; // WS_EX_NOACTIVATE | WS_EX_LAYERED
            return parameters;
        }
    }

    public void PinToTaskbar()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Point.Empty).WorkingArea;
        var height = Units(34);
        var width = Math.Min(area.Width, Units(8) + chips.Sum(chip => Units(chip.Width)));
        var bounds = new Rectangle(area.Right - width - Units(8), area.Bottom - height - Units(3), width, height);
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var tray = taskbar == 0 ? 0 : FindWindowEx(taskbar, 0, "TrayNotifyWnd", null);
        // On Windows 11 the notification area begins with the hidden-icons button when present.
        if (tray != 0 && GetWindowRect(tray, out var anchor) && anchor.Right > anchor.Left
            && anchor.Bottom - anchor.Top >= height && anchor.Left - width - Units(3) >= area.Left
            && anchor.Top >= area.Bottom - Units(6) && anchor.Top <= area.Bottom + Units(6))
        {
            bounds = new Rectangle(anchor.Left - width - Units(3),
                anchor.Top + (anchor.Bottom - anchor.Top - height) / 2, width, height);
        }
        layerDirty |= Size != bounds.Size;
        if (Bounds != bounds)
            Bounds = bounds;
        strip.Padding = new Padding(Units(4), Units(2), 0, 0);
        for (var index = 0; index < chips.Length; index++)
            strip.Controls[index].Size = new Size(Units(chips[index].Width), Units(30));
        DrawLayerIfNeeded();
        KeepVisible();
    }

    private void DrawLayerIfNeeded()
    {
        if (!layerDirty || !Visible || !IsHandleCreated || Width < 1 || Height < 1)
            return;
        layerDirty = !LayeredSurface.Paint(Handle, Size, DeviceDpi, graphics =>
        {
            // An alpha of 1 keeps the whole chip clickable without a visible background.
            using var hitArea = new SolidBrush(Color.FromArgb(1, palette.HitTarget));
            var x = Units(4);
            graphics.TranslateTransform(x, Units(2));
            foreach (Cell cell in strip.Controls)
            {
                graphics.FillRectangle(hitArea, cell.ClientRectangle);
                cell.Draw(graphics, palette);
                graphics.TranslateTransform(cell.Width, 0);
            }
        });
    }

    public void SetRows(BatteryRow[] rows)
    {
        this.rows = rows;
        var ordered = rows.Select(row => (Row: row, Kind: KindFor(row.HardwareModel ?? row.Name))).ToArray();
        var next = Style == TaskbarStyle.Hub ? [BuildHub(rows)] : ordered.Take(6).Select(item =>
        {
            var row = item.Row;
            var stale = !IsFresh(row);
            var warning = !stale && !row.ReportsCharging && row.Sample!.Percent <= (row.DeviceLow ?? low);
            return new Chip(row.Name, item.Kind, row.Level, row.Sample?.Percent, stale, warning,
                Style == TaskbarStyle.Meter ? (warning ? 56 : 46) : (row.ReportsCharging || warning ? 76 : 64),
                Style, row.ChargeLabel);
        }).ToArray();
        if (Style != TaskbarStyle.Hub && ordered.Length > next.Length)
            next = [.. next, new Chip("Thiết bị khác", DeviceKind.More, $"+{ordered.Length - next.Length}",
                null, false, false, 44, Style)];
        if (next.Length == 0)
            next = [new Chip("Pin thiết bị", DeviceKind.Other, "—", null, true, false, 54, Style)];
        if (chips.SequenceEqual(next))
            return;
        chips = next;
        layerDirty = true;
        strip.SuspendLayout();
        try
        {
            while (strip.Controls.Count > chips.Length)
                strip.Controls[^1].Dispose();
            for (var index = 0; index < chips.Length; index++)
            {
                if (index == strip.Controls.Count)
                {
                    var cell = new Cell(fluentIcons, taskbarText) { Margin = Padding.Empty, TabStop = false, Cursor = Cursors.Hand };
                    cell.Click += (_, _) => DetailsRequested?.Invoke();
                    strip.Controls.Add(cell);
                }
                var control = (Cell)strip.Controls[index];
                control.UpdateChip(chips[index]);
                tips.SetToolTip(control, control.AccessibleName);
            }
            PinToTaskbar();
        }
        finally { strip.ResumeLayout(); }
    }

    public void SetDisplayStyle(TaskbarStyle style)
    {
        Style = style;
        SetRows(rows);
    }

    private static bool IsFresh(BatteryRow row) => row.Connected == true && !row.IsLast && row.Sample is not null;

    private Chip BuildHub(BatteryRow[] devices)
    {
        var fresh = devices.Where(IsFresh).OrderBy(row => row.Sample!.Percent).ToArray();
        var lowDevice = fresh.FirstOrDefault(row => !row.ReportsCharging && row.Sample!.Percent <= (row.DeviceLow ?? low));
        var representative = lowDevice ?? fresh.FirstOrDefault(row => row.ReportsCharging) ?? fresh.FirstOrDefault();
        var statuses = string.Join(", ", devices.Select(row => row.ChargeLabel is { } charge
            ? $"{row.Name} {row.Level} {charge}"
            : $"{row.Name} {row.Level}{(row.Sample is not null && !IsFresh(row) ? " lần đọc cuối" : "")}"));
        return new Chip("BatteryDesk", DeviceKind.Hub, "", representative?.Sample?.Percent, representative is null,
            lowDevice is not null, lowDevice is null ? 36 : 45, TaskbarStyle.Hub, representative?.ChargeLabel,
            $"BatteryDesk · {(statuses.Length == 0 ? "Chưa có dữ liệu pin" : statuses)} · Mở bảng pin");
    }

    public void ApplyTheme()
    {
        palette = ReadPalette();
        layerDirty = true;
        DrawLayerIfNeeded();
    }

    private static Palette ReadPalette()
    {
        if (SystemInformation.HighContrast)
            return new Palette(SystemColors.Window, SystemColors.WindowText, SystemColors.GrayText,
                SystemColors.WindowText);
        var path = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var preferences = Registry.CurrentUser.OpenSubKey(path);
        return preferences?.GetValue("SystemUsesLightTheme") is 1
            ? new Palette(Color.White, Color.FromArgb(32, 32, 32),
                Color.FromArgb(104, 104, 104), Color.FromArgb(134, 65, 9))
            : new Palette(Color.Black, Color.FromArgb(246, 246, 246),
                Color.FromArgb(174, 174, 174), Color.FromArgb(255, 194, 133));
    }

    private static Font? LoadIconFont()
    {
        using var candidate = new Font("Segoe Fluent Icons", 12, FontStyle.Regular, GraphicsUnit.Point);
        return candidate.Name == "Segoe Fluent Icons" ? (Font)candidate.Clone() : null;
    }

    private int Units(int value) => (value * DeviceDpi + 48) / 96;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint { public int X, Y; }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint after, string className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out WindowRect bounds);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(WindowPoint point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    private static DeviceKind KindFor(string name)
    {
        if (name.Contains("mouse", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G304", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Mouse;
        if (name.Contains("keyboard", StringComparison.OrdinalIgnoreCase)
            || name.Contains("EK75", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Keyboard;
        if (name.Contains("headset", StringComparison.OrdinalIgnoreCase)
            || name.Contains("headphone", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Headset;
        if (name.Contains("controller", StringComparison.OrdinalIgnoreCase)
            || name.Contains("gamepad", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DualShock", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Joy-Con", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Gamepad;
        return DeviceKind.Other;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            visibilityCheck.Stop();
            visibilityCheck.Tick -= VisibilityTick;
            tips.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            visibilityCheck.Dispose();
            fluentIcons?.Dispose();
            taskbarText.Dispose();
        }
    }

    private enum DeviceKind { Mouse, Keyboard, Headset, Gamepad, Other, More, Hub }
    private readonly record struct Palette(Color HitTarget, Color Text, Color Muted, Color Warning);
    private sealed record Chip(string Name, DeviceKind Kind, string Level, int? Percent, bool Stale, bool Low, int Width,
        TaskbarStyle Style, string? ChargeLabel = null, string? Description = null);

    private sealed class Cell(Font? fluentIcons, TaskbarText taskbarText) : Control
    {
        private Chip? chip;

        public void UpdateChip(Chip next)
        {
            if (chip == next) return;
            chip = next;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = next.Description ?? ($"{next.Name} · {next.Level}" +
                (next.ChargeLabel is { } charge ? $" · {charge}"
                    : next.Stale && next.Level != "—" ? " · Lần đọc cuối" : next.Low ? " · Pin yếu" : ""));
        }

        public void Draw(Graphics graphics, Palette palette)
        {
            if (chip is not { } value) return;
            var color = value.Stale ? palette.Muted : value.Low ? palette.Warning : palette.Text;
            var scale = DeviceDpi / 96f;
            if (value.Kind == DeviceKind.Hub)
            {
                DrawBattery(graphics, color, value, 9, scale);
                if (value.Low) DrawText(graphics, "!", color, 30, 9, scale);
                return;
            }
            if (value.Kind == DeviceKind.More)
            {
                DrawText(graphics, value.Level, color, 7, 34, scale);
                return;
            }
            DrawDevice(graphics, value.Kind, color, scale);
            if (value.Style == TaskbarStyle.Meter)
            {
                DrawBattery(graphics, color, value, 23, scale);
                if (value.Low) DrawText(graphics, "!", color, 44, 9, scale);
                return;
            }
            DrawText(graphics, value.Level, color, 26, 34, scale);
            if (value.Low) DrawText(graphics, "!", color, 62, 9, scale);
            if (value.ChargeLabel is not null) DrawBolt(graphics, color, 63, scale);
        }

        private void DrawText(Graphics graphics, string text, Color color, int left, int width, float scale) =>
            taskbarText.Draw(graphics, text, "Segoe UI", 13, 500, color, TextBounds(left, width, scale), DeviceDpi);

        private Rectangle TextBounds(int left, int width, float scale) =>
            new((int)MathF.Round(left * scale), 0, (int)MathF.Round(width * scale), Height);

        private void DrawDevice(Graphics graphics, DeviceKind kind, Color color, float scale)
        {
            var state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(MathF.Round(6 * scale), MathF.Round((Height - 16 * scale) / 2));
            graphics.ScaleTransform(scale, scale);
            using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            DrawIcon(graphics, pen, kind);
            graphics.Restore(state);
        }

        private void DrawBattery(Graphics graphics, Color color, Chip value, int left, float scale)
        {
            var step = value.Stale ? 0 : (value.Percent ?? 0) / 10;
            var charging = value.ChargeLabel is not null;
            var glyph = (char)(charging ? step switch { 10 => 0xEA93, 9 => 0xE83E, _ => 0xE85A + step }
                : step == 10 ? 0xE83F : 0xE850 + step);
            if (DrawGlyph(graphics, glyph, color, left, scale))
                return;
            var state = graphics.Save();
            graphics.TranslateTransform(left * scale, (Height - 16 * scale) / 2);
            graphics.ScaleTransform(scale, scale);
            using var pen = new Pen(color);
            using var fill = new SolidBrush(color);
            graphics.DrawRectangle(pen, 1, 4, 14, 8);
            graphics.DrawLine(pen, 17, 6, 17, 10);
            if (step > 0)
                graphics.FillRectangle(fill, 3, 6, step, 4);
            graphics.Restore(state);
            if (charging) DrawBolt(graphics, color, left + 9, scale);
        }

        private bool DrawGlyph(Graphics graphics, char glyph, Color color, int left, float scale)
        {
            if (fluentIcons is null) return false;
            taskbarText.Draw(graphics, glyph.ToString(), fluentIcons.Name, 16, 400, color, TextBounds(left, 21, scale), DeviceDpi);
            return true;
        }

        private void DrawBolt(Graphics graphics, Color color, int left, float scale)
        {
            var state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(MathF.Round(left * scale), MathF.Round((Height - 12 * scale) / 2));
            graphics.ScaleTransform(scale, scale);
            using var brush = new SolidBrush(color);
            graphics.FillPolygon(brush, new PointF[] { new(5f, 0f), new(0f, 7f), new(4f, 7f),
                new(2f, 12f), new(8f, 5f), new(4.5f, 5f) });
            graphics.Restore(state);
        }

        private static void DrawIcon(Graphics graphics, Pen pen, DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.Mouse:
                    DrawRoundedOutline(graphics, pen, new RectangleF(4.15f, 1.85f, 7.7f, 12.2f), 3.8f);
                    graphics.DrawLine(pen, 8f, 3.45f, 8f, 6.2f);
                    break;
                case DeviceKind.Keyboard:
                    DrawRoundedOutline(graphics, pen, new RectangleF(1.5f, 3.9f, 12.8f, 7.9f), 1.35f);
                    for (var key = 0; key < 4; key++)
                    {
                        var x = 3.55f + key * 2.85f;
                        graphics.DrawLine(pen, x, 6.15f, x + 0.5f, 6.15f);
                    }
                    graphics.DrawLine(pen, 4.8f, 9.15f, 11.15f, 9.15f);
                    break;
                case DeviceKind.Headset:
                    graphics.DrawArc(pen, 2.65f, 2.2f, 10.7f, 11.4f, 180, 180);
                    graphics.DrawLine(pen, 2.65f, 7.9f, 2.65f, 9.3f);
                    graphics.DrawLine(pen, 13.35f, 7.9f, 13.35f, 9.3f);
                    DrawRoundedOutline(graphics, pen, new RectangleF(1.85f, 8.95f, 2.9f, 4.1f), 0.9f);
                    DrawRoundedOutline(graphics, pen, new RectangleF(11.25f, 8.95f, 2.9f, 4.1f), 0.9f);
                    break;
                case DeviceKind.Gamepad:
                    DrawRoundedOutline(graphics, pen, new RectangleF(1.65f, 4.65f, 12.6f, 6.95f), 2.45f);
                    graphics.DrawLine(pen, 4.4f, 8.1f, 7.15f, 8.1f);
                    graphics.DrawLine(pen, 5.775f, 6.75f, 5.775f, 9.45f);
                    using (var buttons = new SolidBrush(pen.Color))
                    {
                        graphics.FillEllipse(buttons, 9.9f, 6.6f, 1.4f, 1.4f);
                        graphics.FillEllipse(buttons, 11.25f, 8.35f, 1.4f, 1.4f);
                    }
                    break;
                default:
                    graphics.DrawRectangle(pen, 1, 4, 12, 8);
                    graphics.DrawLine(pen, 15, 6, 15, 10);
                    break;
            }
        }

        private static void DrawRoundedOutline(Graphics graphics, Pen pen, RectangleF bounds, float radius)
        {
            var diameter = radius * 2;
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            graphics.DrawPath(pen, path);
        }
    }
}
