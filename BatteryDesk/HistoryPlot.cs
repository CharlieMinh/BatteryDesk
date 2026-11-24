using System.Drawing.Drawing2D;
using BatteryDesk.Core;

namespace BatteryDesk;

internal sealed class HistoryPlot : Control
{
    private HistoryPoint[] points = [];
    private DateTimeOffset start;
    private DateTimeOffset end;

    public HistoryPlot()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        AccessibleName = "Biểu đồ lịch sử pin";
        AccessibleRole = AccessibleRole.Graphic;
    }

    public void SetData(HistoryPoint[] next, DateTimeOffset since, DateTimeOffset until)
    {
        if (next.SequenceEqual(points) && (until - end).Duration().TotalSeconds < 20 && (since - start).Duration().TotalMinutes < 1)
            return;
        points = next;
        start = since;
        end = until;
        var recent = points.LastOrDefault(point => point.Percent is not null);
        AccessibleDescription = recent is null ? "Chưa có mẫu pin trong khoảng này."
            : $"Mẫu mới nhất trong biểu đồ: {recent.Percent}% lúc {recent.At.ToLocalTime():g}.";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var area = new Rectangle(45, 18, Width - 65, Height - 50);
        if (area.Width < 30 || area.Height < 30 || end <= start) return;
        var graphics = e.Graphics;
        using var ruler = new Pen(SystemColors.ControlDark);
        using var stroke = new Pen(SystemColors.Highlight, 1.8f);
        using var marker = new SolidBrush(SystemColors.Highlight);
        foreach (var level in new[] { 100, 50, 0 })
        {
            var y = area.Bottom - area.Height * level / 100;
            graphics.DrawLine(ruler, area.Left, y, area.Right, y);
            TextRenderer.DrawText(graphics, $"{level}%", Font, new Point(4, y - 7), ForeColor);
        }
        TextRenderer.DrawText(graphics, start.ToLocalTime().ToString("dd/MM HH:mm"), Font,
            new Point(area.Left, area.Bottom + 8), ForeColor);
        TextRenderer.DrawText(graphics, "Hiện tại", Font, new Rectangle(area.Left, area.Bottom + 8, area.Width, 24),
            ForeColor, TextFormatFlags.Right);
        if (!points.Any(point => point.Percent is not null))
        {
            TextRenderer.DrawText(graphics, "Chưa có mẫu pin trong khoảng này.", Font, area, SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var series in points.GroupBy(point => (point.EndpointKey, point.Segment)))
        {
            PointF? previous = null;
            foreach (var sample in series)
            {
                if (sample.Percent is not int level)
                {
                    previous = null;
                    continue;
                }
                var fraction = Math.Clamp((sample.At - start).TotalSeconds / (end - start).TotalSeconds, 0, 1);
                PointF point = new(area.Left + (float)fraction * area.Width, area.Bottom - area.Height * level / 100f);
                if (previous is PointF last)
                    graphics.DrawLine(stroke, last, point);
                graphics.FillEllipse(marker, point.X - 2f, point.Y - 2f, 4f, 4f);
                previous = point;
            }
        }
    }
}
