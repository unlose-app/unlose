using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Unlose.UI.Views.Pages;

namespace Unlose.UI
{
    public partial class MainWindow : Window
    {
        private string _currentPageKey = "Dashboard";

        // ===== System tray (P/Invoke implementation, avoids a WinForms dependency) =====
        private const int WM_TRAYICON = 0x0400 + 1;
        private const uint NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2, NIM_SETVERSION = 0x4;
        private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
        private const uint NIIF_INFO = 0x1;
        private const int WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
        // NOTIFYICON_VERSION_4 balloon events (low word of lParam)
        private const int NIN_BALLOONUSERCLICK = 0x0405;
        private const int SM_CXSMICON = 49, SM_CYSMICON = 50;

        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _trayIconHandle = IntPtr.Zero;
        private bool _allowRealExit;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

        public MainWindow()
        {
            InitializeComponent();

            // Clamp the default size to the work area: 1200x800 pushes the title bar off-screen on a 1280x720 display
            var wa = SystemParameters.WorkArea;
            Width = Math.Min(1200, wa.Width * 0.94);
            Height = Math.Min(800, wa.Height * 0.92);

            MainFrame.Navigate(new DashboardPage());
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Note: do not re-enable DwmEnableBlurBehindWindow — glass blur is a leftover from the
            // AllowsTransparency era; on a non-transparent window it renders the entire client area as
            // fully transparent glass (the window "disappears" but its handle remains visible)
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            SetupTrayIcon(hwnd);
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            // WPF borderless windows occasionally hit "first frame not composed": the window is visible
            // and the visual tree is intact, but the DWM has no content until a manual resize triggers
            // a repaint. After the first frame renders, proactively send FRAMECHANGED to make the DWM fetch the frame again.
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        // ===== Tray icon and context menu =====
        private void SetupTrayIcon(IntPtr hwnd)
        {
            _hwnd = hwnd;
            HwndSource.FromHwnd(hwnd)?.AddHook(TrayWndProc);

            // Tray icon size follows the system small-icon metrics (hardcoding 16 blurs under high DPI)
            int cx = GetSystemMetrics(SM_CXSMICON), cy = GetSystemMetrics(SM_CYSMICON);

            // Prefer unlose.ico from the output directory (MSI installs it at the root; dev builds put it in the Resources subdirectory);
            // if missing, fall back to extracting the icon embedded in our own exe (ApplicationIcon resource), and only then use the system default icon
            string icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "unlose.ico");
            if (!System.IO.File.Exists(icoPath))
                icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "unlose.ico");
            if (System.IO.File.Exists(icoPath))
                _trayIconHandle = LoadImage(IntPtr.Zero, icoPath, 1 /*IMAGE_ICON*/, cx, cy, 0x0010 /*LR_LOADFROMFILE*/);
            if (_trayIconHandle == IntPtr.Zero)
            {
                var small = new IntPtr[1];
                string exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "unlose.UI.exe");
                if (ExtractIconEx(exePath, 0, null, small, 1) > 0)
                    _trayIconHandle = small[0];
            }
            if (_trayIconHandle == IntPtr.Zero)
                _trayIconHandle = LoadImage(IntPtr.Zero, "#32512", 1, 0, 0, 0x0040 | 0x8000 /*LR_SHARED*/);

            var nid = NewTrayData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            nid.hIcon = _trayIconHandle;
            nid.szTip = "unlose — 本地文件安全防护";
            Shell_NotifyIconW(NIM_ADD, ref nid);

            // Declare VERSION_4 behavior to receive balloon click callbacks (NIN_BALLOONUSERCLICK)
            var ver = NewTrayData(0);
            ver.uTimeoutOrVersion = 4;
            Shell_NotifyIconW(NIM_SETVERSION, ref ver);

            // Balloon notifications rely on service event push; the tray (including --tray windowless mode) must keep its own subscription
            ServiceClient.NotificationReceived += ServiceClient_NotificationReceived;
            ServiceClient.EnsureEventSubscriptionStarted();
        }

        private NOTIFYICONDATAW NewTrayData(uint flags) => new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_TRAYICON,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };

        private IntPtr TrayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouseMsg == WM_LBUTTONDBLCLK)
                {
                    RestoreFromTray();
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowTrayMenu();
                    handled = true;
                }
                else if (mouseMsg == NIN_BALLOONUSERCLICK)
                {
                    // Balloon click: open the main window and jump to the snapshot management page for details
                    RestoreFromTray();
                    _currentPageKey = "Snapshots";
                    MainFrame.Navigate(new SnapshotLibraryPage());
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        // ===== Snapshot creation balloon notifications (scheduled / pre-Agent-session / Agent-initiated; manual and pre-restore snapshots do not interrupt the user) =====
        private void ServiceClient_NotificationReceived(object? sender, ServiceClient.ServiceNotificationEventArgs e)
        {
            // Snapshot created/failed events: refresh the current page (snapshot library / audit log) in real time,
            // so the user sees new snapshots without clicking refresh. RequestRefresh is debounced internally — a burst of events triggers only one fetch.
            var isSnapshotEvent = string.Equals(e.Type, "SnapshotCreatedNotification", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(e.Type, "SnapshotFailedNotification", StringComparison.OrdinalIgnoreCase);
            if (isSnapshotEvent)
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (MainFrame?.Content is SnapshotLibraryPage snapPage)
                        snapPage.RequestRefresh();
                    else if (MainFrame?.Content is AuditLogPage auditPage)
                        auditPage.RequestRefresh();
                });
            }

            // Notification filtering (affects only tray balloons; the page real-time refresh above is unaffected)
            var notifyLevel = GetNotificationLevel();
            var snoozed = UiSettings.IsNotificationSnoozed;

            // Failure events: shown in both all / failures-only modes; not shown in silent mode or during snooze
            if (string.Equals(e.Type, "SnapshotFailedNotification", StringComparison.OrdinalIgnoreCase))
            {
                if (notifyLevel == "silent" || snoozed || e.Payload is null) return;
                try
                {
                    var failed = e.Payload.Value.Deserialize<Unlose.Core.Ipc.SnapshotFailedNotification>();
                    if (failed is null) return;
                    var zhF = LocalizationService.IsChinese;
                    var textF = zhF
                        ? $"快照创建失败：{failed.Reason}（已重试 {failed.RetryCount} 次）"
                        : $"Snapshot creation failed: {failed.Reason} (retried {failed.RetryCount} time(s))";
                    Dispatcher.InvokeAsync(() => ShowTrayBalloon(zhF ? "unlose 快照保护" : "unlose Snapshot Guard", textF));
                }
                catch { /* Silently ignore malformed payloads */ }
                return;
            }

            if (!string.Equals(e.Type, "SnapshotCreatedNotification", StringComparison.OrdinalIgnoreCase) || e.Payload is null)
                return;

            // In silent / failures-only mode or during snooze, successful snapshots do not pop a balloon
            if (notifyLevel is "silent" or "failures-only" || snoozed)
                return;

            // Payload is a SnapshotCreatedNotification envelope ({ "Snapshot": {...} }); unwrap the inner object first
            Unlose.Core.Models.SnapshotRecord? snapshot;
            try
            {
                var el = e.Payload.Value;
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("Snapshot", out var inner))
                    el = inner;
                snapshot = el.Deserialize<Unlose.Core.Models.SnapshotRecord>();
            }
            catch { return; }
            if (snapshot is null) return;

            var zh = LocalizationService.IsChinese;
            string? text = snapshot.TriggerType switch
            {
                Unlose.Core.Enums.TriggerType.Scheduled =>
                    zh ? "定时快照已创建，文件受保护中。" : "Scheduled snapshot created. Your files are protected.",
                Unlose.Core.Enums.TriggerType.AgentPreSession =>
                    zh ? "Agent 启动前快照已创建，文件受保护中。" : "Pre-session Agent snapshot created. Your files are protected.",
                Unlose.Core.Enums.TriggerType.AgentInitiated =>
                    zh ? "Agent 主动快照已创建，文件受保护中。" : "Agent-initiated snapshot created. Your files are protected.",
                _ => null
            };
            if (text is null) return;

            if (!string.IsNullOrWhiteSpace(snapshot.TriggerDetail))
                text += "\n" + snapshot.TriggerDetail;

            Dispatcher.InvokeAsync(() => ShowTrayBalloon(zh ? "unlose 快照保护" : "unlose Snapshot Guard", text));
        }

        private void ShowTrayBalloon(string title, string info)
        {
            if (_hwnd == IntPtr.Zero) return;
            var nid = NewTrayData(NIF_INFO);
            nid.szInfoTitle = title;
            nid.szInfo = info;
            nid.dwInfoFlags = NIIF_INFO;
            nid.uTimeoutOrVersion = 10000; // Display duration (ms) for legacy OS compat; on Win10+ the system decides
            Shell_NotifyIconW(NIM_MODIFY, ref nid);
        }

        /// <summary>
        /// Read the notification level (all / failures-only / silent, default all).
        /// Reads config.json directly instead of going through the pipe: takes effect immediately after a
        /// service hot-reload or manual edit, with no cache synchronization needed on the UI side;
        /// falls back to all when the file is missing/corrupt (current behavior).
        /// </summary>
        private static string GetNotificationLevel()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "unlose", "config.json");
                if (!File.Exists(path)) return "all";
                var cfg = JsonSerializer.Deserialize<Unlose.Core.Config.UnloseConfig>(File.ReadAllText(path));
                return string.IsNullOrWhiteSpace(cfg?.Snapshot?.NotificationLevel)
                    ? "all"
                    : cfg!.Snapshot.NotificationLevel.Trim().ToLowerInvariant();
            }
            catch { return "all"; }
        }

        // ===== Snooze notifications (snooze 24 hours; state persisted to ui-settings.json, survives UI restarts, auto-resumes on expiry) =====
        private static string GetSnoozeMenuHeader()
        {
            var zh = LocalizationService.IsChinese;
            if (UiSettings.IsNotificationSnoozed && UiSettings.NotificationSnoozedUntilUtc is { } until)
            {
                var hoursLeft = (int)Math.Ceiling((until - DateTime.UtcNow).TotalHours);
                return zh ? $"恢复通知（剩余约 {hoursLeft} 小时）" : $"Resume notifications (~{hoursLeft}h left)";
            }
            return zh ? "暂停通知 24 小时" : "Snooze notifications for 24 hours";
        }

        private void ToggleNotificationSnooze()
        {
            if (UiSettings.IsNotificationSnoozed)
                UiSettings.ResumeNotifications();
            else
                UiSettings.SnoozeNotifications(TimeSpan.FromHours(24));
        }

        private void ShowTrayMenu()
        {
            var menu = new ContextMenu();

            var showItem = new MenuItem { Header = "显示主界面" };
            showItem.Click += (_, _) => RestoreFromTray();
            var snoozeItem = new MenuItem { Header = GetSnoozeMenuHeader() };
            snoozeItem.Click += (_, _) => ToggleNotificationSnooze();
            var exitItem = new MenuItem { Header = "退出 unlose" };
            exitItem.Click += (_, _) => { _allowRealExit = true; Close(); };

            menu.Items.Add(showItem);
            menu.Items.Add(snoozeItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);

            // Bring this window to the foreground before popping the tray menu, so the menu auto-closes when it loses focus
            SetForegroundWindow(_hwnd);
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        internal void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        // Close = hide to tray; only the tray menu "Exit" actually exits
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowRealExit)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            ServiceClient.NotificationReceived -= ServiceClient_NotificationReceived;
            if (_hwnd != IntPtr.Zero)
            {
                var nid = NewTrayData(0);
                Shell_NotifyIconW(NIM_DELETE, ref nid);
                HwndSource.FromHwnd(_hwnd)?.RemoveHook(TrayWndProc);
            }
            if (_trayIconHandle != IntPtr.Zero)
                DestroyIcon(_trayIconHandle);
            base.OnClosed(e);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                // Double-click toggles maximize/restore; single-click drags (dragging while maximized auto-restores and follows the mouse)
                if (e.ClickCount == 2)
                    ToggleMaximize();
                else
                    this.DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (MaximizeButton != null)
                MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "⬜";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // DEF2-010: language switch (zh/EN); the choice is persisted and no longer follows the OS on next start
        private void LangSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            LocalizationService.SetLanguage(!LocalizationService.IsChinese);
            ApplyNavTexts();

            if (MainFrame.Content is ILocalizable localizable)
                localizable.ApplyLanguage();
        }

        private void ApplyNavTexts()
        {
            if (LocalizationService.IsChinese)
            {
                NavDashboard.Content = "🏠  概览";
                NavSnapshots.Content = "📂  快照管理";
                NavAudit.Content = "📋  监控日志";
                NavSettings.Content = "⚙️  控制中心";
                NavRestorePoints.Content = "🛡️  系统还原点";
                NavRestoreWizard.Content = "⟲  回滚向导";
                NavAbout.Content = "ℹ️  关于";
            }
            else
            {
                NavDashboard.Content = "🏠  Overview";
                NavSnapshots.Content = "📂  Snapshots";
                NavAudit.Content = "📋  Audit Log";
                NavSettings.Content = "⚙️  Settings";
                NavRestorePoints.Content = "🛡️  Restore Points";
                NavRestoreWizard.Content = "⟲  Restore Wizard";
                NavAbout.Content = "ℹ️  About";
            }
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag?.ToString() is { } tag)
            {
                _currentPageKey = tag;
                UpdateNavActiveState(btn);

                MainFrame.Navigate(tag switch
                {
                    "Dashboard" => (object)new DashboardPage(),
                    "Snapshots" => new SnapshotLibraryPage(),
                    "Audit" => new AuditLogPage(),
                    "Settings" => new SettingsPage(),
                    "RestorePoints" => new RestorePoints(),
                    "RestoreWizard" => new RestoreWizard(),
                    "About" => new AboutPage(),
                    _ => new DashboardPage()
                });
            }
        }

        private void UpdateNavActiveState(Button activeBtn)
        {
            var navButtons = new[] { NavDashboard, NavSnapshots, NavAudit, NavSettings, NavRestorePoints, NavRestoreWizard, NavAbout };
            var navStyle = (Style)FindResource("NavButton");
            var activeStyle = (Style)FindResource("NavButtonActive");
            foreach (var b in navButtons)
            {
                if (b != null)
                    b.Style = (b == activeBtn) ? activeStyle : navStyle;
            }
        }
    }
}
