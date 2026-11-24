namespace BatteryDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, @"LocalBatteryDesk.Tray", out var first);
        if (!first)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(@"LocalBatteryDesk.Show");
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }

        ApplicationConfiguration.Initialize();
        using var application = new TrayApplication(args.Contains("--show"));
        Application.Run(application);
    }
}
