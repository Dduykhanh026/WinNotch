using Velopack;

namespace WinNotch;

public static class Program
{
    private const string SingleInstanceMutexName = @"Local\WinNotch_SingleInstance_Mutex_v1";
    internal const string ActivateInstanceEventName = @"Local\WinNotch_ActivateInstance_Event_v1";

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Velopack lifecycle fast-path (handles install/uninstall/shortcut creation in <10ms)
        VelopackApp.Build().Run();

        // 2. Single-instance guard: if already running, signal existing Notch to pop open so user sees immediate feedback!
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateInstanceEventName);
                evt.Set();
            }
            catch { }
            return;
        }

        // 3. Boot WPF Application
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
