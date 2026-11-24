using System.Drawing.Drawing2D;
using BatteryDesk.Core;

namespace BatteryDesk;

internal enum ConnectionKind { Bluetooth, Receiver, Cable }

internal sealed record BatteryRow(string Name, bool? Connected, BatterySample? Sample, bool IsLast,
    ConnectionKind Kind = ConnectionKind.Bluetooth, string? HardwareModel = null, int? DeviceLow = null, string? Id = null)
{
    public string Level => Sample is { } sample ? $"{sample.Percent}%" : "—";
    public bool ReportsCharging => Connected == true && !IsLast && Sample?.Charging == true;
    public string? ChargeLabel => ReportsCharging ? (Sample!.Percent == 100 ? "Đầy pin" : "Đang sạc") : null;
    public string Connection => Kind switch
    {
        ConnectionKind.Cable when Connected == false => "Cáp USB · Đã ngắt kết nối",
        ConnectionKind.Cable => $"Cáp USB · {ChargeLabel ?? (Connected == true && !IsLast && Sample is not null
            ? (Sample.Charging == false ? "Đã cắm" : "Chưa rõ trạng thái sạc") : "Chưa có dữ liệu mới")}",
        ConnectionKind.Receiver when Connected == false => "USB 2.4 GHz · Đã ngắt kết nối",
        ConnectionKind.Receiver => Connected == true ? "USB 2.4 GHz · Đang kết nối" : "USB 2.4 GHz · Chưa có phản hồi",
        _ => Connected switch { true => "Đang kết nối", false => "Đã ngắt kết nối", null => "Chưa rõ kết nối" }
    };
    public string SourceDescription => Sample switch
    {
        null => "Chưa có dữ liệu pin",
        { ReceivedAt: { } time } => $"{(IsLast ? "Lần đọc cuối" : "Nhận lúc")} {time.ToLocalTime():HH:mm:ss}",
        _ => IsLast ? "Mức pin cuối từ Windows" : "Windows báo · chưa rõ thời điểm"
    };
    public override string ToString() => $"{Name}, {Level}, {Connection}, {SourceDescription}";
}

internal sealed class BatteryPanel : Form
{
    private static readonly Color Background = Color.FromArgb(19, 24, 31);
    private static readonly Color Card = Color.FromArgb(31, 39, 50);
    private static readonly Color TextColor = Color.FromArgb(241, 245, 249);
    private static readonly Color Secondary = Color.FromArgb(170, 186, 200);
    private static readonly Color Mint = Color.FromArgb(131, 234, 192);
    private static readonly Color Low = Color.FromArgb(255, 200, 122);
    private readonly Font deviceFont = new("Segoe UI", 11, FontStyle.Bold);
    private readonly Font levelFont = new("Segoe UI", 16, FontStyle.Bold);
    private readonly Font titleFont = new("Segoe UI", 20, FontStyle.Bold);
    private readonly Button refresh;
    private readonly Label footer;
    private readonly Label empty;
    private readonly ListBox list;
    private readonly int lowThreshold;
    private BatteryRow[] rows = [];
    private string? errorMessage;
    private bool busy;
    private bool hasSnapshot;

    public event Action? RefreshRequested;
    public event Action? DevicesRequested;

    public BatteryPanel(int lowThreshold)
    {
        this.lowThreshold = lowThreshold;
        Text = "BatteryDesk";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(410, 407);
        BackColor = Background;
        ForeColor = TextColor;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(17, 8, 17, 4), ColumnCount = 1, RowCount = 3,
            BackColor = Background
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        header.Controls.Add(new Label
        {
            Text = "Pin thiết bị", Font = titleFont, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, 0);
        refresh = new Button
        {
            Text = "Làm mới", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, ForeColor = Mint,
            BackColor = Card, Margin = new Padding(0, 13, 0, 13), Cursor = Cursors.Hand
        };
        refresh.FlatAppearance.BorderSize = 0;
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        var devices = new Button
        {
            Text = "Thiết bị", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, ForeColor = TextColor,
            BackColor = Card, Margin = new Padding(0, 13, 6, 13), Cursor = Cursors.Hand
        };
        devices.FlatAppearance.BorderSize = 0;
        devices.Click += (_, _) => DevicesRequested?.Invoke();
        header.Controls.Add(devices, 1, 0);
        header.Controls.Add(refresh, 2, 0);

        var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        list = new ListBox
        {
            Dock = DockStyle.Fill, BackColor = Background, ForeColor = TextColor, BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, AccessibleName = "Danh sách pin thiết bị",
            Visible = false
        };
        list.DrawItem += DrawRow;
        empty = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Secondary,
            Text = "Đang tìm thiết bị không dây…"
        };
        body.Controls.Add(list);
        body.Controls.Add(empty);
        footer = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Secondary, Margin = Padding.Empty
        };
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(body, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        DpiChanged += (_, _) => UpdateItemHeight();
        UpdateItemHeight();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        if (keyData == Keys.F5 && refresh.Enabled)
        {
            RefreshRequested?.Invoke();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void ShowNearTray(Rectangle anchor)
    {
        var area = Screen.FromRectangle(anchor).WorkingArea;
        void Position() => Location = new Point(Math.Clamp(anchor.Right - Width, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Max(area.Top, Math.Min(area.Bottom, anchor.Top) - Height - LogicalToDeviceUnits(5)));
        Position();
        Show();
        Position();
    }

    public void SetBusy(bool value)
    {
        busy = value;
        refresh.Enabled = !value;
        UpdateStatus();
    }

    public void SetRows(BatteryRow[] next, string? error)
    {
        errorMessage = error;
        hasSnapshot |= error is null;
        if (!rows.SequenceEqual(next))
        {
            rows = next;
            var previousTop = list.Items.Count > 0 ? list.TopIndex : 0;
            var selected = (list.SelectedItem as BatteryRow)?.Id;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                list.Items.AddRange(rows);
                if (selected is not null)
                    list.SelectedIndex = Array.FindIndex(rows, row => row.Id == selected);
                if (rows.Length > 0)
                    list.TopIndex = Math.Clamp(previousTop, 0, rows.Length - 1);
            }
            finally { list.EndUpdate(); }
            list.Visible = rows.Length > 0;
            empty.Visible = rows.Length == 0;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        empty.Text = errorMessage ?? (busy || !hasSnapshot ? "Đang tìm thiết bị không dây…"
            : "Chưa tìm thấy thiết bị không dây.");
        footer.Text = rows.Length > 0 && errorMessage is not null ? errorMessage
            : busy ? "Đang cập nhật pin…"
            : rows.Length > 0 ? $"{rows.Count(row => row.Connected == true)} đang kết nối / {rows.Length} thiết bị"
            : "";
    }

    private int Scale(int units) => (int)Math.Round(units * DeviceDpi / 96.0);
    private void UpdateItemHeight() => list.ItemHeight = Scale(88);

    private void DrawRow(object? sender, DrawItemEventArgs args)
    {
        if (args.Index < 0 || args.Index >= list.Items.Count)
            return;
        var row = (BatteryRow)list.Items[args.Index];
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var backdrop = new SolidBrush(Background);
        graphics.FillRectangle(backdrop, args.Bounds);
        var card = new Rectangle(args.Bounds.X, args.Bounds.Y + Scale(3), args.Bounds.Width - 1, args.Bounds.Height - Scale(7));
        if (card.Width < Scale(130))
            return;
        using var shape = new GraphicsPath();
        var arc = Scale(17);
        shape.AddArc(card.Left, card.Top, arc, arc, 180, 90);
        shape.AddArc(card.Right - arc, card.Top, arc, arc, 270, 90);
        shape.AddArc(card.Right - arc, card.Bottom - arc, arc, arc, 0, 90);
        shape.AddArc(card.Left, card.Bottom - arc, arc, arc, 90, 90);
        shape.CloseFigure();
        using var cardBrush = new SolidBrush(Card);
        graphics.FillPath(cardBrush, shape);
        if (args.State.HasFlag(DrawItemState.Focus))
        {
            using var outline = new Pen(Mint);
            graphics.DrawPath(outline, shape);
        }

        var left = card.X + Scale(13);
        var textWidth = card.Width - Scale(103);
        const TextFormatFlags leftFlags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(graphics, row.Name, deviceFont,
            new Rectangle(left, card.Y + Scale(9), textWidth, Scale(22)), TextColor, leftFlags);
        TextRenderer.DrawText(graphics, row.Connection, Font,
            new Rectangle(left, card.Y + Scale(34), textWidth, Scale(19)), Secondary, leftFlags);
        TextRenderer.DrawText(graphics, row.SourceDescription, Font,
            new Rectangle(left, card.Y + Scale(53), textWidth, Scale(19)), Secondary, leftFlags);

        var accent = row.IsLast || row.Connected != true || row.Sample is null ? Secondary
            : row.Sample.Percent <= (row.DeviceLow ?? lowThreshold) && !row.ReportsCharging ? Low : Mint;
        var right = card.Right - Scale(75);
        TextRenderer.DrawText(graphics, row.Level, levelFont,
            new Rectangle(right, card.Y + Scale(12), Scale(62), Scale(34)), accent,
            TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        using var bar = new SolidBrush(Color.FromArgb(65, 82, 96));
        graphics.FillRectangle(bar, right, card.Y + Scale(56), Scale(60), Scale(4));
        if (row.Sample is { Percent: > 0 } sample)
        {
            using var progress = new SolidBrush(accent);
            graphics.FillRectangle(progress, right, card.Y + Scale(56), Math.Max(1, Scale(60) * sample.Percent / 100), Scale(4));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            deviceFont.Dispose();
            levelFont.Dispose();
            titleFont.Dispose();
        }
    }
}
