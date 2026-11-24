using System.Runtime.InteropServices;
using BatteryDesk.Core;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Timer = System.Windows.Forms.Timer;

namespace BatteryDesk;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly BatteryPanel panel;
    private readonly BatteryBar bar;
    private readonly DeviceDirectory directory;
    private readonly BatteryHistory history;
    private readonly Dictionary<string, DeviceAlerts> alerts = new(StringComparer.Ordinal);
    private DevicePage? devicesPage;
    private readonly int lowThreshold;
    private const int LiveInterval = 5_000;
    private const int ReadDue = 4_500;
    private readonly Timer debounce = new() { Interval = 300 };
    private readonly Timer periodic = new() { Interval = LiveInterval };
    private readonly Timer persist = new() { Interval = 30_000 };
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReceiverEntry> receivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CableEntry> keyboards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Icon icon = TrayIcon.Create();
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly BluetoothCatalog catalog;
    private readonly DeviceWatcher receiverWatcher;
    private readonly DeviceWatcher keyboardWatcher;
    private readonly EventWaitHandle showEvent = new(false, EventResetMode.AutoReset, @"LocalBatteryDesk.Show");
    private readonly RegisteredWaitHandle showRegistration;
    private CancellationTokenSource? scanCancellation;
    private bool pending;
    private bool force;
    private bool reset;
    private bool closing;
    private bool disposed;
    private string? errorMessage;
    private string? receiverError;
    private string? keyboardError;
    private bool receiverListDirty = true;
    private bool keyboardListDirty = true;
    private bool bluetoothNeedsPolling = true;

    public TrayApplication(bool show)
    {
        lowThreshold = BatterySettings.ReadLowThreshold();
        var saved = DeviceStorage.ReadDevices();
        directory = new DeviceDirectory(saved.Profiles);
        history = new BatteryHistory(saved.RecordHistory, DeviceStorage.ReadHistory(), DateTimeOffset.UtcNow);
        panel = new BatteryPanel(lowThreshold);
        bar = new BatteryBar(lowThreshold, BatterySettings.ReadTaskbarStyle());
        tray = new NotifyIcon { Icon = icon, Text = "BatteryDesk", Visible = true, ContextMenuStrip = menu };
        _ = dispatcher.Handle;
        panel.RefreshRequested += () => RequestRefresh(true);
        panel.DevicesRequested += ShowDevices;
        bar.DetailsRequested += ShowPanel;
        bar.ContextMenuStrip = menu;
        menu.Items.Add("Mở bảng pin", null, (_, _) => ShowPanel());
        menu.Items.Add("Thiết bị và lịch sử", null, (_, _) => ShowDevices());
        menu.Items.Add("Làm mới", null, (_, _) => RequestRefresh(true));
        var styles = new ToolStripMenuItem("Kiểu hiển thị");
        foreach (var (style, label) in new[] { (TaskbarStyle.Percentage, "Số phần trăm"),
            (TaskbarStyle.Meter, "Vạch pin"), (TaskbarStyle.Hub, "Một biểu tượng") })
        {
            var option = new ToolStripMenuItem(label) { Checked = bar.Style == style };
            option.Click += (_, _) =>
            {
                if (bar.Style == style)
                    return;
                bar.SetDisplayStyle(style);
                foreach (ToolStripMenuItem item in styles.DropDownItems)
                    item.Checked = item == option;
                if (!BatterySettings.SaveTaskbarStyle(style))
                    tray.ShowBalloonTip(4000, "BatteryDesk", "Đã đổi kiểu hiển thị. Windows chưa lưu được lựa chọn cho lần mở sau.", ToolTipIcon.Warning);
            };
            styles.DropDownItems.Add(option);
        }
        menu.Items.Add(styles);
        menu.Items.Add("Thoát", null, (_, _) => Quit());
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                if (panel.Visible) panel.Hide();
                else ShowPanel();
            }
        };
        catalog = new BluetoothCatalog(() => Post(QueueCatalogRefresh));
        receiverWatcher = LightspeedReceiver.Watch();
        receiverWatcher.Added += ReceiverAdded;
        receiverWatcher.Removed += ReceiverRemoved;
        receiverWatcher.Stopped += ReceiverWatcherStopped;
        keyboardWatcher = DareuKeyboard.Watch();
        keyboardWatcher.Added += KeyboardAdded;
        keyboardWatcher.Removed += KeyboardRemoved;
        keyboardWatcher.Stopped += KeyboardWatcherStopped;
        debounce.Tick += (_, _) => { debounce.Stop(); RequestRefresh(); };
        periodic.Tick += (_, _) => { bar.PinToTaskbar(); RequestRefresh(); };
        persist.Tick += (_, _) => PersistData();
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Post(ShowPanel),
            null, Timeout.Infinite, false);
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        catalog.Start();
        receiverWatcher.Start();
        keyboardWatcher.Start();
        periodic.Start();
        persist.Start();
        Post(() =>
        {
            bar.Show();
            bar.PinToTaskbar();
            if (show) ShowPanel();
            // Give the watchers a chance to populate before using a standalone query.
            debounce.Interval = 1_200;
            debounce.Start();
        });
    }

    private void DisplayChanged(object? sender, EventArgs args) => Post(bar.PinToTaskbar);
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (args.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General
            or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
            Post(() => { bar.ApplyTheme(); bar.PinToTaskbar(); });
    }

    private void ReceiverAdded(DeviceWatcher sender, DeviceInformation device) => ReceiverChanged(device.Id);
    private void ReceiverRemoved(DeviceWatcher sender, DeviceInformationUpdate device)
    {
        if (!LightspeedReceiver.IsReceiverPath(device.Id))
            return;
        Post(() =>
        {
            if (receivers.TryGetValue(device.Id, out var receiver))
                receiver.Invalidated = true;
            InvalidateReceiverList();
        });
    }
    private void ReceiverWatcherStopped(DeviceWatcher sender, object args) => Post(InvalidateReceiverList);

    private void KeyboardAdded(DeviceWatcher sender, DeviceInformation device)
    {
        if (DareuKeyboard.IsKeyboardPath(device.Id))
            Post(InvalidateKeyboardList);
    }

    private void KeyboardRemoved(DeviceWatcher sender, DeviceInformationUpdate device)
    {
        if (DareuKeyboard.IsKeyboardPath(device.Id))
            Post(InvalidateKeyboardList);
    }

    private void KeyboardWatcherStopped(DeviceWatcher sender, object args) => Post(InvalidateKeyboardList);

    private void InvalidateKeyboardList()
    {
        keyboardListDirty = true;
        foreach (var keyboard in keyboards.Values)
        {
            keyboard.State.SetConnection(null);
            keyboard.NextRead = 0;
        }
        Render();
        QueueCatalogRefresh();
    }

    private void ReceiverChanged(string path)
    {
        if (LightspeedReceiver.IsReceiverPath(path))
            Post(InvalidateReceiverList);
    }

    private void InvalidateReceiverList()
    {
        receiverListDirty = true;
        QueueCatalogRefresh();
    }

    private void QueueCatalogRefresh()
    {
        debounce.Stop();
        debounce.Interval = 300;
        debounce.Start();
    }

    private void Post(Action action)
    {
        try
        {
            dispatcher.BeginInvoke(() => { if (!closing) action(); });
        }
        catch (InvalidOperationException) { } // Window already destroyed during shutdown.
    }

    private void ShowPanel()
    {
        if (!bar.Visible)
            bar.Show();
        bar.PinToTaskbar();
        if (!panel.Visible)
            panel.ShowNearTray(bar.Bounds);
        panel.Activate();
        Post(bar.KeepVisible);
    }

    private void ShowDevices()
    {
        if (devicesPage is null || devicesPage.IsDisposed)
            devicesPage = new DevicePage(directory, history, lowThreshold, () => PersistData(true), Render);
        devicesPage.SetViews(directory.Views());
        if (!devicesPage.Visible)
            devicesPage.Show();
        if (devicesPage.WindowState == FormWindowState.Minimized)
            devicesPage.WindowState = FormWindowState.Normal;
        devicesPage.Activate();
    }

    private bool PersistData(bool savePreferences = false)
    {
        var success = true;
        if (directory.Dirty || savePreferences)
        {
            success = DeviceStorage.SaveDevices(new SavedDevices(directory.Profiles, history.Enabled));
            if (success) directory.MarkSaved();
        }
        if (history.Dirty)
        {
            var saved = DeviceStorage.SaveHistory(history.Snapshot(DateTimeOffset.UtcNow));
            if (saved) history.MarkSaved();
            success &= saved;
        }
        return success;
    }

    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume)
            Post(() =>
            {
                reset = true;
                foreach (var entry in entries.Values)
                    entry.State.SetConnection(null);
                foreach (var receiver in receivers.Values)
                    receiver.State.SetConnection(null);
                foreach (var keyboard in keyboards.Values)
                    keyboard.State.SetConnection(null);
                scanCancellation?.Cancel();
                Render();
                RequestRefresh(true);
            });
    }

    private async void RequestRefresh(bool manual = false)
    {
        if (closing)
            return;
        force |= manual;
        if (scanCancellation is not null)
        {
            pending = true;
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        scanCancellation = cancellation;
        var refreshAll = force;
        force = false;
        panel.SetBusy(true);
        try
        {
            if (reset)
            {
                foreach (var entry in entries.Values)
                    entry.ResetGatt();
                reset = false;
            }
            using var slots = new SemaphoreSlim(2);
            await Task.WhenAll(RefreshKeyboards(refreshAll, slots, cancellation.Token),
                RefreshReceivers(refreshAll, slots, cancellation.Token),
                RefreshBluetooth(refreshAll, slots, cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            scanCancellation = null;
            if (closing)
                ExitThread();
            else
            {
                periodic.Enabled = bluetoothNeedsPolling || receivers.Count > 0 || receiverError is not null
                    || keyboards.Count > 0 || keyboardError is not null
                    || keyboardWatcher.Status is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted
                    || receiverWatcher.Status is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted;
                panel.SetBusy(false);
                Render();
                if (pending)
                {
                    pending = false;
                    Post(() => RequestRefresh());
                }
            }
        }
    }

    private async Task RefreshKeyboards(bool manual, SemaphoreSlim slots, CancellationToken token)
    {
        try
        {
            if (keyboardListDirty || manual || keyboardWatcher.Status is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted)
            {
                var found = DareuKeyboard.Discover().ToHashSet(keyboards.Comparer);
                keyboardListDirty = false;
                keyboardError = null;
                foreach (var path in keyboards.Keys.Where(path => !found.Contains(path)).ToArray())
                    keyboards.Remove(path);
                foreach (var path in found)
                {
                    if (!keyboards.ContainsKey(path))
                        keyboards.Add(path, new CableEntry(path, lowThreshold));
                }
                Render();
            }

            foreach (var (path, keyboard) in keyboards)
            {
                var started = Environment.TickCount64;
                if (!manual && started < keyboard.NextRead)
                    continue;
                await slots.WaitAsync(token);
                try
                {
                    var reading = await Task.Run(() => DareuKeyboard.ReadAsync(path, token), token);
                    token.ThrowIfCancellationRequested();
                    if (keyboardListDirty)
                        return;
                    keyboard.State.SetConnection(true);
                    keyboard.State.Apply(reading, true);
                    keyboard.NextRead = started + (reading.Status == ReadStatus.Success ? ReadDue : 29_000);
                    Render();
                }
                finally { slots.Release(); }
            }
        }
        catch (IOException error)
        {
            keyboardListDirty = true;
            keyboardError = "Không đọc được kết nối USB của bàn phím.";
            System.Diagnostics.Trace.TraceWarning("Wired keyboard scan failed: {0}", error);
        }
    }

    private async Task RefreshBluetooth(bool refreshAll, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        errorMessage = null;
        try
        {
            using var discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            discovery.CancelAfter(TimeSpan.FromSeconds(12));
            var snapshot = catalog.Snapshot();
            var devices = snapshot ?? await WindowsBluetooth.DiscoverAsync(discovery.Token);
            cancellationToken.ThrowIfCancellationRequested();
            bluetoothNeedsPolling = snapshot is null || devices.Any(device => device.Connected == true);
            var present = devices.Select(device => device.Id).ToHashSet(entries.Comparer);
            foreach (var id in entries.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                entries[id].ResetGatt();
                entries.Remove(id);
            }
            foreach (var device in devices)
            {
                if (!entries.TryGetValue(device.Id, out var entry))
                    entries.Add(device.Id, entry = new Entry(device, lowThreshold));
                entry.Update(device);
            }
            Render();

            var native = WindowsBluetooth.ReadWindowsLevels(devices);
            foreach (var entry in entries.Values)
                ApplyWindows(entry, BatterySamples.FromWindows(entry.Device, native));
            Render();

            await Task.WhenAll(entries.Values.Where(entry => entry.BleId is not null)
                .Select(async entry =>
                {
                    if (!refreshAll && Environment.TickCount64 < entry.NextGatt)
                        return;
                    await slots.WaitAsync(cancellationToken);
                    try { await ReadGatt(entry, cancellationToken); }
                    finally { slots.Release(); }
                }));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            bluetoothNeedsPolling = true;
            errorMessage = "Windows Bluetooth phản hồi chậm. Hãy thử làm mới.";
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or IOException)
        {
            bluetoothNeedsPolling = true;
            System.Diagnostics.Trace.TraceWarning("Bluetooth scan failed: {0}", error);
            errorMessage = "Không truy cập được Windows Bluetooth.";
        }
    }

    private async Task RefreshReceivers(bool manual, SemaphoreSlim slots, CancellationToken token)
    {
        try
        {
            var watcherStopped = receiverWatcher.Status is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted;
            if (receiverListDirty || manual || watcherStopped)
            {
                receiverListDirty = false;
                var found = LightspeedReceiver.Discover().ToHashSet(receivers.Comparer);
                foreach (var path in receivers.Keys.Where(path => !found.Contains(path)).ToArray())
                    receivers.Remove(path);
                foreach (var path in found)
                {
                    if (!receivers.ContainsKey(path))
                        receivers.Add(path, new ReceiverEntry(path, lowThreshold));
                    if (receivers[path] is { Invalidated: true } receiver)
                    {
                        receiver.State.SetConnection(null);
                        receiver.NextRead = 0;
                        receiver.Invalidated = false;
                    }
                }
                receiverError = null;
                Render();
            }

            foreach (var (path, receiver) in receivers)
            {
                if (!manual && Environment.TickCount64 < receiver.NextRead)
                    continue;
                await slots.WaitAsync(token);
                try
                {
                    var started = Environment.TickCount64;
                    var readName = manual || receiver.Name is null || started >= receiver.NextName;
                    var preferred = receiver.Feature;
                    var retry = manual || receiver.NextRead == 0 || (receiver.State.Sample is not null && !receiver.State.IsLast);
                    var reading = await Task.Run(
                        () => LightspeedReceiver.ReadAsync(path, readName, token, preferred, retry), token);
                    token.ThrowIfCancellationRequested();
                    if (receiver.Invalidated)
                        continue;
                    if (reading.DeviceName is { } name)
                    {
                        receiver.UpdateName(name);
                        receiver.NextName = started + 600_000;
                    }
                    receiver.State.SetConnection(reading.Connected);
                    receiver.State.Apply(reading.Battery, true);
                    if (reading.Feature is { } feature)
                        receiver.Feature = feature;
                    if (reading.Battery.Status == ReadStatus.Unavailable)
                    {
                        receiver.Failures = Math.Min(receiver.Failures + 1, 4);
                        receiver.NextName = 0;
                    }
                    else
                        receiver.Failures = 0;

                    receiver.NextRead = started + (reading.Battery.Status == ReadStatus.Unsupported ? 295_000
                        : ReadDue << receiver.Failures);
                    Render();
                }
                finally { slots.Release(); }
            }
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or IOException)
        {
            receiverListDirty = true;
            receiverError = "Không đọc được USB receiver.";
            foreach (var receiver in receivers.Values)
                receiver.State.SetConnection(null);
            System.Diagnostics.Trace.TraceWarning("USB receiver scan failed: {0}", error);
        }
    }

    private void ApplyWindows(Entry entry, Reading reading)
    {
        entry.State.Apply(reading, entry.BleId is null || entry.State.Sample?.ReceivedAt is null);
    }

    private async Task ReadGatt(Entry entry, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var previous = entry.State.Sample;
        if (entry.Gatt is null)
        {
            var session = new GattBattery();
            session.Changed += sample => Post(() =>
            {
                if (reset || entry.Gatt != session || entry.State.Connected != true)
                    return;
                entry.State.Apply(Reading.Success(sample), true);
                Render();
            });
            entry.Gatt = session;
        }

        var keepSession = false;
        try
        {
            var reading = await entry.Gatt.ReadAsync(entry.BleId!, true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            keepSession = entry.Gatt.Subscribed && reading.Status != ReadStatus.Unsupported
                && !reading.Detail.StartsWith("BLE endpoint", StringComparison.Ordinal);
            if (reading.Sample is not null)
            {
                entry.State.Apply(reading, true);
                entry.Failures = 0;
                entry.NextGatt = started + (keepSession ? 290_000 : ReadDue);
            }
            else
            {
                if (entry.State.Sample is null || (entry.State.Sample.ReceivedAt is not null && ReferenceEquals(previous, entry.State.Sample)))
                    entry.State.Apply(reading, true);
                entry.Failures = Math.Min(entry.Failures + 1, 4);
                entry.NextGatt = reading.Status == ReadStatus.Unsupported ? long.MaxValue
                    : started + (ReadDue << entry.Failures);
            }
        }
        finally
        {
            if (!keepSession)
                entry.ReleaseGatt();
        }
        Render();
    }

    private void CheckAlerts(DeviceView device, int low)
    {
        if (!alerts.TryGetValue(device.Profile.Id, out var state))
            alerts[device.Profile.Id] = state = new DeviceAlerts();
        var flags = state.Observe(device.Display, low, device.Profile.Preferences.ChargeTarget);
        var level = device.Display.Sample?.Percent;
        if (flags.HasFlag(BatteryAlert.Low))
            tray.ShowBalloonTip(5_000, "Pin yếu", $"{device.Name} còn {level}%.", ToolTipIcon.Warning);
        if (flags.HasFlag(BatteryAlert.ChargeTarget))
            tray.ShowBalloonTip(5_000, "Đã đạt mức sạc", $"{device.Name} đã đạt {level}%.", ToolTipIcon.Info);
    }

    private void Render()
    {
        var snapshot = entries.Values.Select(entry => DeviceObservation.From(
                new DeviceEndpoint(entry.Key, entry.Device.Name, DeviceConnection.Bluetooth), entry.State))
            .Concat(receivers.Values.Select(receiver => DeviceObservation.From(new DeviceEndpoint(receiver.Key,
                receiver.DisplayName, DeviceConnection.Receiver, LightspeedReceiver.UsbId), receiver.State)))
            .Concat(keyboards.Values.Select(keyboard => DeviceObservation.From(new DeviceEndpoint(keyboard.Key,
                DareuKeyboard.Name, DeviceConnection.Cable, DareuKeyboard.UsbId), keyboard.State)))
            .ToArray();
        directory.Observe(snapshot);
        history.Observe(snapshot, DateTimeOffset.UtcNow);
        var devices = directory.Views();
        devicesPage?.SetViews(devices);
        foreach (var view in devices)
            if (!view.Profile.Preferences.Hidden)
                CheckAlerts(view, view.Profile.Preferences.Low ?? lowThreshold);

        var rows = DeviceDirectory.Overview(devices).Select(view =>
        {
            var device = view.Display;
            var threshold = view.Profile.Preferences.Low ?? lowThreshold;
            var kind = device.Endpoint.Connection switch
            {
                DeviceConnection.Cable => ConnectionKind.Cable,
                DeviceConnection.Receiver => ConnectionKind.Receiver,
                _ => ConnectionKind.Bluetooth
            };
            return new BatteryRow(view.Name, device.Connected, device.Sample, device.IsLast,
                kind, device.Endpoint.Model, threshold, view.Profile.Id);
        }).ToArray();
        panel.SetRows(rows, errorMessage ?? receiverError ?? keyboardError);
        bar.SetRows(rows);
        var count = rows.Count(row => row.Connected == true);
        var lowest = rows.Where(row => row.Connected == true && !row.IsLast).Min(row => row.Sample?.Percent);
        var tooltip = lowest is int level ? $"BatteryDesk · thấp nhất {level}%"
            : $"BatteryDesk · {count} thiết bị kết nối";
        if (tray.Text != tooltip)
            tray.Text = tooltip;
    }

    private void Quit()
    {
        closing = true;
        tray.Visible = false;
        debounce.Stop();
        periodic.Stop();
        persist.Stop();
        shutdown.Cancel();
        if (scanCancellation is null)
            ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            PersistData();
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.UserPreferenceChanged -= PreferenceChanged;
            showRegistration.Unregister(null);
            showEvent.Dispose();
            catalog.Dispose();
            receiverWatcher.Added -= ReceiverAdded;
            receiverWatcher.Removed -= ReceiverRemoved;
            receiverWatcher.Stopped -= ReceiverWatcherStopped;
            if (receiverWatcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                receiverWatcher.Stop();
            keyboardWatcher.Added -= KeyboardAdded;
            keyboardWatcher.Removed -= KeyboardRemoved;
            keyboardWatcher.Stopped -= KeyboardWatcherStopped;
            if (keyboardWatcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                keyboardWatcher.Stop();
            foreach (var entry in entries.Values)
                entry.ResetGatt();
            tray.Dispose();
            icon.Dispose();
            menu.Dispose();
            panel.Dispose();
            devicesPage?.Dispose();
            bar.Dispose();
            dispatcher.Dispose();
            debounce.Dispose();
            periodic.Dispose();
            persist.Dispose();
            shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class CableEntry(string path, int lowThreshold)
    {
        public string Key { get; } = DeviceEndpoint.KeyFor(DeviceConnection.Cable, path);
        public BatteryState State { get; } = new(lowThreshold);
        public long NextRead { get; set; }
    }

    private sealed class ReceiverEntry(string path, int lowThreshold)
    {
        public string Key { get; } = DeviceEndpoint.KeyFor(DeviceConnection.Receiver, path);
        public BatteryState State { get; private set; } = new(lowThreshold);
        public string? Name { get; private set; }
        public string DisplayName => Name ?? LightspeedReceiver.DisplayName;
        public long NextName { get; set; }
        public long NextRead { get; set; }
        public int Failures { get; set; }
        public LogitechBatteryFeature? Feature { get; set; }
        public bool Invalidated { get; set; }

        public void UpdateName(string name)
        {
            if (Name is not null && Name != name)
                State = new BatteryState(lowThreshold);
            Name = name;
        }
    }

    private sealed class Entry(Peripheral device, int lowThreshold)
    {
        public string Key { get; } = DeviceEndpoint.KeyFor(DeviceConnection.Bluetooth, device.Id);
        public Peripheral Device { get; private set; } = device;
        public BatteryState State { get; } = new(lowThreshold);
        public string? BleId { get; private set; }
        public GattBattery? Gatt { get; set; }
        public long NextGatt { get; set; }
        public int Failures { get; set; }

        public void Update(Peripheral device)
        {
            Device = device;
            State.SetConnection(device.Connected);
            var next = device.Endpoints.FirstOrDefault(endpoint =>
                endpoint.Transport == Transport.LowEnergy && endpoint.Connected == true)?.Id;
            if (next != BleId)
            {
                State.SetConnection(null);
                State.SetConnection(device.Connected);
                ResetGatt();
                BleId = next;
            }
        }

        public void ReleaseGatt()
        {
            Gatt?.Dispose();
            Gatt = null;
        }

        public void ResetGatt()
        {
            ReleaseGatt();
            NextGatt = 0;
            Failures = 0;
        }
    }
}
