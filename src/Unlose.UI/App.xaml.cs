using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Unlose.UI;

public partial class App : Application
{
    // Single instance: relaunching only brings up the existing instance's window, avoiding multiple tray icons
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showWindowEvent;
    // Only the first instance owns the mutex; a second instance must not ReleaseMutex on exit
    // (it never acquired it — releasing throws ApplicationException, crashing the exit path)
    private static bool _ownsInstanceMutex;
    private const string MutexName = @"Local\unlose.UI.SingleInstance";
    private const string ShowEventName = @"Local\unlose.UI.ShowWindow";

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsInstanceMutex = createdNew;
        if (!createdNew)
        {
            // An instance is already running: signal it to show the main window, then exit this process
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                evt.Set();
            }
            catch { /* Silently exit if the first instance is not ready yet */ }
            Shutdown();
            return;
        }

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        Task.Run(async () =>
        {
            while (true)
            {
                try { _showWindowEvent.WaitOne(); }
                catch { break; }
                await Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.RestoreFromTray());
            }
        });

        // Must be registered before the main window is created: exceptions thrown earlier would otherwise go uncaught (window flash-crashes)
        DispatcherUnhandledException += (_, ex) =>
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unlose-ui-crash.log"),
                $"=== {System.DateTime.Now:HH:mm:ss} ===\n{ex.Exception}\n\n"); } catch { }
            MessageBox.Show($"未处理错误：{ex.Exception.Message}\n\n{ex.Exception}", "unlose",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                MessageBox.Show(
                    $"后台任务异常：{args.Exception?.Message}\n\n{args.Exception}",
                    "unlose",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                args.SetObserved();
            }
        };

        base.OnStartup(e);

        // --tray / --minimized (used by auto-start on boot): stay in the tray without showing the main window.
        // A WPF window has no HWND before Show, and the tray icon is attached in OnSourceInitialized,
        // so tray mode must call EnsureHandle to force-create the window handle (the window stays hidden).
        bool trayOnly = e.Args.Any(a => a is "--tray" or "--minimized");
        var window = new MainWindow();
        MainWindow = window;
        if (trayOnly)
            new WindowInteropHelper(window).EnsureHandle();
        else
            window.Show();

        StartPeriodicUpdateCheck();
    }

    // Daily auto update check: first check 30 seconds after startup, then every 24 hours
    // (throttled by ShouldCheckUpdatesNow; failures also record the timestamp to avoid hammering
    // the site while offline). Results are cached in UiSettings so the About page can always show
    // the "Update" button or "You are up to date". Only GETs the static version.json from the
    // official site, reports no data, and fails silently.
    private static DispatcherTimer? _updateCheckTimer;

    private static void StartPeriodicUpdateCheck()
    {
        _ = RunUpdateCheckOnceAsync(TimeSpan.FromSeconds(30));
        _updateCheckTimer = new DispatcherTimer { Interval = UiSettings.UpdateCheckInterval };
        _updateCheckTimer.Tick += async (_, _) => await RunUpdateCheckOnceAsync(TimeSpan.Zero);
        _updateCheckTimer.Start();
    }

    private static async Task RunUpdateCheckOnceAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            if (!UiSettings.ShouldCheckUpdatesNow) return;
            var result = await Unlose.Core.Updates.UpdateChecker.CheckAsync();
            UiSettings.RecordUpdateCheck(result);
        }
        catch { /* A failed update check does not affect any local functionality */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowEvent?.Dispose();
        if (_ownsInstanceMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { /* not owned anymore */ }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

