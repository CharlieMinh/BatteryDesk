using System.Reflection;
using BatteryDesk.Core;

namespace BatteryDesk;

internal sealed class DevicePage : Form
{
    private readonly DeviceDirectory directory;
    private readonly BatteryHistory history;
    private readonly int defaultLow;
    private readonly Func<bool> save;
    private readonly Action changed;
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "Danh sách thiết bị" };
    private readonly TextBox alias = new() { MaxLength = 80, Width = 260, AccessibleName = "Tên hiển thị" };
    private readonly CheckBox pinned = new() { Text = "Ghim lên đầu", AutoSize = true };
    private readonly CheckBox hidden = new() { Text = "Ẩn thiết bị và tắt cảnh báo", AutoSize = true };
    private readonly CheckBox customLow = new() { Text = "Ngưỡng pin yếu riêng", AutoSize = true };
    private readonly NumericUpDown low = new() { Minimum = 1, Maximum = 99, Width = 65, AccessibleName = "Phần trăm pin yếu" };
    private readonly CheckBox chargeAlert = new() { Text = "Báo khi sạc đạt mức", AutoSize = true };
    private readonly NumericUpDown chargeTarget = new() { Minimum = 1, Maximum = 100, Value = 100, Width = 65, AccessibleName = "Mức sạc mục tiêu" };
    private readonly ComboBox linkChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 254, AccessibleName = "Thiết bị cần liên kết" };
    private readonly ComboBox unlinkChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155, AccessibleName = "Kết nối cần tách" };
    private readonly ComboBox range = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, AccessibleName = "Khoảng thời gian" };
    private readonly CheckBox recordHistory = new() { Text = "Ghi lịch sử trên máy này", AutoSize = true };
    private readonly CheckBox start = new() { Text = "Khởi động cùng Windows", AutoSize = true };
    private readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label selectedModel = new() { AutoSize = true, MaximumSize = new Size(450, 0) };
    private readonly HistoryPlot plot = new() { Dock = DockStyle.Fill };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, AccessibleName = "Cài đặt BatteryDesk" };
    private readonly TabPage historyTab = new("Lịch sử");
    private readonly FlowLayoutPanel editor;
    private readonly Button diagnostics;
    private readonly Button link;
    private readonly Button unlink;
    private DeviceView[] views = [];
    private bool updating;

    public DevicePage(DeviceDirectory directory, BatteryHistory history, int defaultLow, Func<bool> save, Action changed)
    {
        this.directory = directory;
        this.history = history;
        this.defaultLow = defaultLow;
        this.save = save;
        this.changed = changed;
        Text = "BatteryDesk · Thiết bị";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(815, 560);
        MinimumSize = new Size(765, 505);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 238));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.Controls.Add(list, 0, 0);
        layout.Controls.Add(tabs, 1, 0);
        layout.Controls.Add(status, 0, 1);
        layout.SetColumnSpan(status, 2);
        Controls.Add(layout);

        var deviceTab = new TabPage("Thiết bị");
        editor = Stack();
        editor.Controls.Add(selectedModel);
        editor.Controls.Add(new Label { Text = "Tên hiển thị", AutoSize = true });
        editor.Controls.Add(alias);
        editor.Controls.Add(Row(pinned, hidden));
        editor.Controls.Add(Row(customLow, low, new Label { Text = "%", AutoSize = true, Margin = new Padding(2, 7, 1, 0) }));
        editor.Controls.Add(Row(chargeAlert, chargeTarget, new Label { Text = "%", AutoSize = true, Margin = new Padding(2, 7, 1, 0) }));
        editor.Controls.Add(Row(Button("Lưu thiết bị", SaveEditor), Button("Lên", () => MoveSelected(-1)), Button("Xuống", () => MoveSelected(1))));
        editor.Controls.Add(new Label { Text = "Liên kết hai bản ghi của cùng một thiết bị vật lý", AutoSize = true, Margin = new Padding(3, 16, 3, 3) });
        link = Button("Liên kết…", Link);
        unlink = Button("Tách", Unlink);
        editor.Controls.Add(Row(linkChoice, link));
        editor.Controls.Add(Row(unlinkChoice, unlink));
        deviceTab.Controls.Add(editor);
        tabs.TabPages.Add(deviceTab);

        var historyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        range.Items.AddRange(["24 giờ", "7 ngày"]);
        range.SelectedIndex = 0;
        historyLayout.Controls.Add(Row(range, recordHistory, Button("Xóa lịch sử…", ClearHistory)), 0, 0);
        historyLayout.Controls.Add(plot, 0, 1);
        historyTab.Controls.Add(historyLayout);
        tabs.TabPages.Add(historyTab);
        var settings = new TabPage("Cài đặt");
        var settingsLayout = Stack();
        settingsLayout.Controls.Add(start);
        diagnostics = Button("Sao chép thông tin hỗ trợ…", ShowSupport);
        settingsLayout.Controls.Add(diagnostics);
        settings.Controls.Add(settingsLayout);
        tabs.TabPages.Add(settings);

        customLow.CheckedChanged += (_, _) => low.Enabled = customLow.Checked;
        chargeAlert.CheckedChanged += (_, _) => chargeTarget.Enabled = chargeAlert.Checked;
        list.SelectedIndexChanged += (_, _) => { if (!updating) LoadEditor(); };
        linkChoice.SelectedIndexChanged += (_, _) => link.Enabled = linkChoice.SelectedIndex >= 0;
        unlinkChoice.SelectedIndexChanged += (_, _) => unlink.Enabled = unlinkChoice.SelectedIndex >= 0;
        tabs.SelectedIndexChanged += (_, _) => RefreshChart();
        range.SelectedIndexChanged += (_, _) => RefreshChart();
        recordHistory.Checked = history.Enabled;
        recordHistory.CheckedChanged += (_, _) =>
        {
            history.SetEnabled(recordHistory.Checked);
            SaveAndRefresh(recordHistory.Checked ? "Đã bật lịch sử." : "Đã dừng ghi lịch sử.");
            RefreshChart();
        };
        start.Checked = StartupSetting.Enabled;
        start.CheckedChanged += (_, _) =>
        {
            if (updating) return;
            if (StartupSetting.Set(start.Checked))
                status.Text = "Đã cập nhật tùy chọn khởi động.";
            else
            {
                status.Text = "Windows chưa lưu được tùy chọn khởi động.";
                updating = true;
                start.Checked = StartupSetting.Enabled;
                updating = false;
            }
        };
        LoadEditor();
    }

    public void SetViews(DeviceView[] next, bool reload = false)
    {
        var previouslySelected = SelectedId;
        views = next;
        var choices = next.Select(view => new Choice(view.Profile.Id, ChoiceLabel(view))).ToArray();
        if (!choices.SequenceEqual(list.Items.Cast<Choice>()))
        {
            updating = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                list.Items.AddRange(choices);
                var index = Array.FindIndex(choices, item => item.Id == previouslySelected);
                list.SelectedIndex = index >= 0 ? index : choices.Length > 0 ? 0 : -1;
            }
            finally { list.EndUpdate(); updating = false; }
        }
        if (reload || SelectedId != previouslySelected)
            LoadEditor();
        else
        {
            SetModelLabel();
            UpdateLinks();
            RefreshChart();
        }
    }

    private string? SelectedId => (list.SelectedItem as Choice)?.Id;
    private DeviceView? Selected => views.FirstOrDefault(view => view.Profile.Id == SelectedId);

    private static string ChoiceLabel(DeviceView view)
    {
        var via = string.Join(" / ", view.Profile.Endpoints.Select(item => SupportSummary.ConnectionLabel(item.Connection)));
        var label = $"{view.Name} • {via}";
        return view.Profile.Preferences.Hidden ? $"{label} (đã ẩn)" : label;
    }

    private void SetModelLabel()
    {
        var chosen = Selected;
        selectedModel.Text = chosen is null ? "Chưa tìm thấy thiết bị." :
            $"{chosen.Display.Endpoint.Model} · {SupportSummary.ConnectionLabel(chosen.Display.Endpoint.Connection)} · {chosen.Profile.Id[..6]}";
    }

    private void UpdateLinks()
    {
        var chosen = Selected;
        SetChoices(linkChoice, chosen is null ? [] : views.Where(view => directory.CanLink(chosen.Profile.Id, view.Profile.Id))
            .Select(view => new Choice(view.Profile.Id, $"{ChoiceLabel(view)} • {view.Profile.Id[..6]}")).ToArray());
        SetChoices(unlinkChoice, chosen is null ? [] : chosen.Profile.Endpoints.Where(item => item.Key != chosen.Profile.Id)
            .Select(item => new Choice(item.Key, SupportSummary.ConnectionLabel(item.Connection))).ToArray());
        link.Enabled = linkChoice.SelectedIndex >= 0;
        unlink.Enabled = unlinkChoice.SelectedIndex >= 0;
    }

    private static void SetChoices(ComboBox box, Choice[] choices)
    {
        if (choices.SequenceEqual(box.Items.Cast<Choice>()))
            return;
        var selected = (box.SelectedItem as Choice)?.Id;
        box.Items.Clear();
        box.Items.AddRange(choices);
        box.SelectedIndex = Array.FindIndex(choices, item => item.Id == selected);
    }

    private void LoadEditor()
    {
        var chosen = Selected;
        var preferences = chosen?.Profile.Preferences;
        editor.Enabled = chosen is not null;
        diagnostics.Enabled = chosen is not null;
        alias.Text = preferences?.Alias ?? "";
        pinned.Checked = preferences?.Pinned == true;
        hidden.Checked = preferences?.Hidden == true;
        customLow.Checked = preferences?.Low is not null;
        low.Value = Math.Clamp(preferences?.Low ?? defaultLow, 1, 99);
        low.Enabled = customLow.Checked;
        chargeAlert.Checked = preferences?.ChargeTarget is not null;
        chargeTarget.Value = Math.Clamp(preferences?.ChargeTarget ?? 100, 1, 100);
        chargeTarget.Enabled = chargeAlert.Checked;
        SetModelLabel();
        UpdateLinks();
        RefreshChart();
    }

    private void SaveEditor()
    {
        if (Selected is not { } view) return;
        var prefs = view.Profile.Preferences;
        directory.Edit(view.Profile.Id, prefs with { Alias = alias.Text, Pinned = pinned.Checked, Hidden = hidden.Checked,
            Low = customLow.Checked ? (int)low.Value : null, ChargeTarget = chargeAlert.Checked ? (int)chargeTarget.Value : null });
        SaveAndRefresh("Đã lưu cài đặt thiết bị.");
    }

    private void MoveSelected(int direction)
    {
        if (SelectedId is { } id && directory.Move(id, direction))
            SaveAndRefresh("Đã đổi thứ tự thiết bị.", reload: false);
    }

    private void Link()
    {
        if (Selected is not { } selected || linkChoice.SelectedItem is not Choice other) return;
        var message = $"Xác nhận hai bản ghi thuộc cùng một thiết bị vật lý?\n\n{ChoiceLabel(selected)}\n{other}\n\n"
            + "BatteryDesk sẽ dùng cài đặt của thiết bị đang chọn. Bạn có thể tách lại từng kết nối.";
        if (MessageBox.Show(this, message, "Liên kết thiết bị", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes && directory.Link(selected.Profile.Id, other.Id))
            SaveAndRefresh("Đã liên kết các kết nối.");
    }

    private void Unlink()
    {
        if (SelectedId is { } id && unlinkChoice.SelectedItem is Choice other && directory.Unlink(id, other.Id))
            SaveAndRefresh("Đã tách kết nối thành thiết bị riêng.");
    }

    private void ClearHistory()
    {
        if (MessageBox.Show(this, "Xóa toàn bộ lịch sử pin đã ghi trên máy này?", "Xóa lịch sử",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        history.Clear();
        SaveAndRefresh("Đã xóa lịch sử pin.");
        RefreshChart();
    }

    private void SaveAndRefresh(string text, bool reload = true)
    {
        status.Text = save() ? text : "Chưa lưu được dữ liệu trên máy.";
        changed();
        SetViews(directory.Views(), reload);
    }

    private void RefreshChart()
    {
        if (tabs.SelectedTab != historyTab) return;
        var now = DateTimeOffset.UtcNow;
        var since = range.SelectedIndex == 1 ? now.AddDays(-7) : now.AddHours(-24);
        var selected = Selected;
        plot.SetData(selected is null ? [] : history.Read(selected.Profile.Endpoints.Select(item => item.Key), since), since, now);
    }

    private void ShowSupport()
    {
        if (Selected is not { } view) return;
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "không rõ";
        var content = SupportSummary.Create(view, version, Environment.OSVersion.Version.ToString(),
            Environment.UserName, Environment.MachineName);
        using var dialog = new Form { Text = "Xem trước thông tin hỗ trợ", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(540, 280), FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var preview = new TextBox { Text = content, Dock = DockStyle.Fill, Multiline = true,
            ReadOnly = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Nội dung hỗ trợ" };
        var copy = Button("Sao chép", () =>
        {
            try { Clipboard.SetText(content); dialog.Close(); status.Text = "Đã sao chép thông tin hỗ trợ."; }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = "Clipboard đang bận. Hãy thử lại."; }
        });
        var close = Button("Đóng", dialog.Close);
        var footer = Row(copy, close);
        footer.Dock = DockStyle.Bottom;
        dialog.Controls.Add(preview);
        dialog.Controls.Add(footer);
        dialog.AcceptButton = copy;
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private static FlowLayoutPanel Stack() => new()
        { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            Margin = new Padding(0, 3, 0, 3) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Button Button(string label, Action action)
    {
        var result = new Button { Text = label, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        result.Click += (_, _) => action();
        return result;
    }

    private sealed record Choice(string Id, string Text)
    {
        public override string ToString() => Text;
    }
}
