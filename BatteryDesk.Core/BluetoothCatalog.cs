using Windows.Devices.Enumeration;

namespace BatteryDesk.Core;

public sealed class BluetoothCatalog : IDisposable
{
    private readonly Subscription[] subscriptions;

    public BluetoothCatalog(Action changed)
    {
        subscriptions = Enum.GetValues<Transport>().Select(transport => new Subscription(transport, changed)).ToArray();
    }

    public void Start()
    {
        foreach (var subscription in subscriptions)
            subscription.Start();
    }

    public Peripheral[]? Snapshot()
    {
        if (subscriptions.Any(subscription => !subscription.Ready))
            return null;
        return WindowsBluetooth.Group(subscriptions.SelectMany(subscription => subscription.Copy()));
    }

    public void Dispose()
    {
        foreach (var subscription in subscriptions)
            subscription.Dispose();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Transport transport;
        private readonly DeviceWatcher watcher;
        private readonly Action changed;
        private readonly object gate = new();
        private readonly Dictionary<string, (DeviceInformation Device, Endpoint Endpoint)> devices = [];
        private bool ready;

        public Subscription(Transport transport, Action changed)
        {
            this.transport = transport;
            this.changed = changed;
            watcher = WindowsBluetooth.Watch(transport);
            watcher.Added += OnAdded;
            watcher.Updated += OnUpdated;
            watcher.Removed += OnRemoved;
            watcher.EnumerationCompleted += OnCompleted;
            watcher.Stopped += OnStopped;
        }

        public bool Ready { get { lock (gate) return ready; } }
        public void Start() => watcher.Start();

        public Endpoint[] Copy()
        {
            lock (gate)
                return devices.Values.Select(entry => entry.Endpoint).ToArray();
        }

        private void OnAdded(DeviceWatcher sender, DeviceInformation device)
        {
            lock (gate)
                devices[device.Id] = (device, WindowsBluetooth.Describe(device, transport));
            if (Ready)
                changed();
        }

        private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(update.Id, out var old))
                    return;
                old.Device.Update(update);
                var next = WindowsBluetooth.Describe(old.Device, transport);
                if (next == old.Endpoint)
                    return;
                devices[update.Id] = (old.Device, next);
            }
            if (Ready)
                changed();
        }

        private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            lock (gate)
                devices.Remove(update.Id);
            if (Ready)
                changed();
        }

        private void OnCompleted(DeviceWatcher sender, object args)
        {
            lock (gate)
                ready = true;
            changed();
        }

        private void OnStopped(DeviceWatcher sender, object args)
        {
            lock (gate)
                ready = false;
            changed();
        }

        public void Dispose()
        {
            watcher.Added -= OnAdded;
            watcher.Updated -= OnUpdated;
            watcher.Removed -= OnRemoved;
            watcher.EnumerationCompleted -= OnCompleted;
            watcher.Stopped -= OnStopped;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
    }
}
