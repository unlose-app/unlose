using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Unlose.Core.Config;

namespace Unlose.UI.Views.Pages;

public partial class SettingsPage : Page, ILocalizable
{
    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "unlose", "config.json");

    /// <summary>Fixed-time list (chips data source), mirrors config.Snapshot.ScheduleTimes</summary>
    private List<TimeSpan> _scheduleTimes = new() { new(8, 0, 0), new(13, 0, 0), new(18, 0, 0) };

    public SettingsPage()
    {
        InitializeComponent();

        // Time picker: hours 00-23, minutes in 5-minute steps; defaults to 08:00
        for (var h = 0; h < 24; h++) HourCombo.Items.Add(h.ToString("D2"));
        for (var m = 0; m < 60; m += 5) MinuteCombo.Items.Add(m.ToString("D2"));
        HourCombo.SelectedIndex = 8;
        MinuteCombo.SelectedIndex = 0;

        ApplyLanguage();
        RenderTimeChips();
        Loaded += async (_, _) => await LoadCurrentSettingsAsync();
    }

    // ── Fixed-time chips ────────────────────────────────────────────────────────
    private void RenderTimeChips()
    {
        TimesChipPanel.Children.Clear();
        foreach (var t in _scheduleTimes.OrderBy(x => x))
        {
            var chipText = new TextBlock
            {
                Text = t.ToString(@"hh\:mm"),
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1")),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var close = new TextBlock
            {
                Text = " ×",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7BA7C4")),
                FontSize = 12,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = t
            };
            close.MouseLeftButtonUp += RemoveTime_Click;
            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(chipText);
            inner.Children.Add(close);
            TimesChipPanel.Children.Add(new Border
            {
                Child = inner,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0F9FF")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 6)
            });
        }
    }

    private void AddTime_Click(object sender, MouseButtonEventArgs e)
    {
        if (HourCombo.SelectedIndex < 0 || MinuteCombo.SelectedIndex < 0) return;
        var t = new TimeSpan(HourCombo.SelectedIndex, MinuteCombo.SelectedIndex * 5, 0);
        if (!_scheduleTimes.Contains(t))
        {
            _scheduleTimes.Add(t);
            RenderTimeChips();
        }
    }

    private void RemoveTime_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Tag: TimeSpan t })
        {
            _scheduleTimes.Remove(t);
            RenderTimeChips();
        }
    }

    private void ScheduleMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FixedTimesPanel == null || IntervalPanel == null) return;
        var fixedMode = ScheduleModeCombo.SelectedIndex == 0;
        FixedTimesPanel.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        IntervalPanel.Visibility = fixedMode ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ApplyLanguage()
    {
        if (LblSettingsTitle == null) return;

        var zh = LocalizationService.IsChinese;

        LblSettingsTitle.Text = zh ? "⚙️  设置中心" : "⚙️  Settings";
        LblSettingsNavTitle.Text = zh ? "控制台策略" : "Console Policy";

        LblNavStorage.Text = zh ? "储存与保留控制" : "Storage & Retention";
        LblNavAgent.Text = zh ? "Agent 告警与响应" : "Agent Alert & Response";
        LblNavMcp.Text = zh ? "MCP 集成向导" : "MCP Integration";
        LblNavSystem.Text = zh ? "系统底层与通知" : "System & Notifications";
        LblNavDiag.Text = zh ? "诊断与空闲释放" : "Diagnostics & Idle Release";

        LblSectionStorage.Text = zh ? "💽  储存与保留控制" : "💽  Storage & Retention Control";
        LblSectionAgent.Text = zh ? "🤖  Agent 进程监控" : "🤖  Agent Process Monitoring";
        LblSectionSystem.Text = zh ? "🪟  系统底层与通知" : "🪟  System & Notification";

        // Section 1: Storage
        LblWatchPaths.Text = zh ? "监控保护目标 (保护卷)" : "Protected Volumes";
        LblWatchPathsDesc.Text = zh ? "快照保护以整卷为单位（VSS 卷影复制）。" : "Snapshots protect entire volumes via VSS (Volume Shadow Copy).";
        BtnChangePaths.Content = zh ? "变更保护卷" : "Change Volumes";
        LblSnapshotInterval.Text = zh ? "定时快照" : "Scheduled Snapshots";
        LblSnapshotIntervalDesc.Text = zh
            ? "每天在定点时刻自动创建快照（默认，1~N 个均可）；也可切换为按间隔周期。"
            : "Snapshots are created at fixed times each day (default, 1-N entries); or switch to interval-based mode.";
        CbiModeFixed.Content = zh ? "定点时刻 (默认)" : "Fixed times (default)";
        CbiModeInterval.Content = zh ? "按间隔周期" : "Interval-based";
        LblAddTime.Text = zh ? "+ 添加时间点" : "+ Add time";
        LblIntervalDesc.Text = zh
            ? "服务按此周期自动为保护卷打快照。"
            : "The service snapshots protected volumes at this interval.";
        CbiInterval6.Content = zh ? "每 6 小时" : "Every 6 hours";
        CbiInterval12.Content = zh ? "每 12 小时" : "Every 12 hours";
        CbiInterval24.Content = zh ? "每 24 小时 (默认)" : "Every 24 hours (default)";
        CbiInterval48.Content = zh ? "每 48 小时" : "Every 48 hours";
        LblStorageThreshold.Text = zh ? "低空间挂起阈值" : "Low-Space Suspend Threshold";
        LblStorageThresholdDesc.Text = zh
            ? "磁盘可用空间低于该阈值时，自动暂停快照保护。"
            : "Snapshot protection pauses automatically when free space drops below this threshold.";
        CbiThreshold1.Content = "1 GB";
        CbiThreshold2.Content = zh ? "2 GB (默认)" : "2 GB (default)";
        CbiThreshold5.Content = "5 GB";
        CbiThreshold10.Content = "10 GB";
        // 24h snapshot retention count
        LblRetention24h.Text = zh ? "24 小时快照保留数" : "24h Snapshot Retention";
        LblRetention24hDesc.Text = zh
            ? "最近 24 小时内最多保留的非固定快照数。Agent 高频会话场景建议 ≥30；其余时段每天/每周仍自动各保留 1 个。"
            : "Max non-pinned snapshots kept within the last 24h. ≥30 recommended for high-frequency Agent sessions; older periods still keep 1/day and 1/week.";
        CbiRetention10.Content = zh ? "10 个" : "10";
        CbiRetention20.Content = zh ? "20 个" : "20";
        CbiRetention30.Content = zh ? "30 个 (默认)" : "30 (default)";
        CbiRetention50.Content = zh ? "50 个" : "50";
        CbiRetention100.Content = zh ? "100 个" : "100";

        // Full-volume in-place restore master switch
        LblInPlaceRestore.Text = zh ? "整卷原位还原（高风险）" : "Full-Volume In-Place Restore (High Risk)";
        LblInPlaceRestoreDesc.Text = zh
            ? "开启后显示「整卷原位还原」入口：把快照整卷覆盖回原位置，卷上现有文件将被替换、快照之后新建的文件将被删除。仅限非系统盘；系统盘请使用 Windows 系统还原。"
            : "When enabled, the \"Full Volume Restore\" entries appear: the snapshot overwrites the entire volume in place — existing files are replaced and files created after the snapshot are deleted. Non-system volumes only; for the system drive use Windows System Restore.";
        LblInPlaceRestoreSwitch.Text = zh ? "启用（仅限非系统盘）" : "Enable (non-system volumes only)";

        // Section 2: Agent
        LblAgentMonitor.Text = zh ? "附加监控进程清单" : "Additional Monitored Processes";
        LblAgentMonitorDesc.Text = zh
            ? "除内置清单外额外监控的进程。检测到这些进程启动时会自动创建保护快照；如需修改请编辑 config.json 的 Agent.MonitoredProcesses。"
            : "Extra processes to monitor beyond the built-in list. A protection snapshot is created when any of them starts. Edit Agent.MonitoredProcesses in config.json to change.";
        LblAgentBuiltinNote.Text = zh
            ? "另内置 25+ 款主流 AI Agent 进程监控。"
            : "Plus 25+ built-in mainstream AI Agent processes monitored.";

        // Section 5: MCP
        LblSectionMcp.Text = zh ? "🔌  MCP 集成向导" : "🔌  MCP Integration";
        LblNavMcp.Text = zh ? "MCP 集成向导" : "MCP Integration";
        LblMcpAddress.Text = zh ? "MCP 服务程序（stdio 模式）" : "MCP Server Executable (stdio)";
        TxtMcpExePath.Text = zh
            ? "unlose.McpServer.exe（位于安装目录根目录，如 C:\\Program Files\\unlose\\）"
            : "unlose.McpServer.exe (in the root of the install directory, e.g. C:\\Program Files\\unlose\\)";
        LblMcpConfigTitle.Text = zh
            ? "配置示例（填入 AI 工具的 MCP 配置文件）："
            : "Config example (paste into your AI tool's MCP config file):";
        // Example JSON is identical in both languages
        TxtMcpConfigExample.Text =
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"unlose\": {\n" +
            "      \"command\": \"C:\\\\Program Files\\\\unlose\\\\unlose.McpServer.exe\"\n" +
            "    }\n" +
            "  }\n" +
            "}";
        LblMcpHint.Text = zh
            ? "command 路径请按实际安装目录调整；服务提供 7 个快照管理工具。"
            : "Adjust the command path to your install directory; the server exposes 7 snapshot tools.";

        // Section 6: Diagnostics
        LblSectionDiag.Text = zh ? "🔬  诊断与空闲释放" : "🔬  Diagnostics & Idle Release";
        LblNavDiag.Text = zh ? "诊断与空闲释放" : "Diagnostics & Idle Release";
        LblDiagDesc.Text = zh
            ? "查看服务运行诊断信息，清理过期临时数据，释放磁盘空间。"
            : "View service diagnostics, clean expired temp data, and free disk space.";
        BtnOpenLogFolder.Content = zh ? "📂  打开日志目录" : "📂  Open Log Folder";
        BtnClearTempData.Content = zh ? "🧹  清理过期日志 (30 天前)" : "🧹  Clear Expired Logs (30d+)";

        // Section 4: System
        LblGeneral.Text = zh ? "常规配置" : "General";
        LblAutoStart.Text = zh
            ? "unlose 服务已注册为开机自启（安装时设定）。"
            : "The unlose service is registered to start with Windows (set during installation).";
        LblUiLang.Text = zh ? "界面语言：" : "Language: ";
        CbiLangZh.Content = zh ? "简体中文" : "Chinese";
        CbiLangEn.Content = "English";
        LblUiLangNa.Text = zh ? "请使用标题栏语言切换" : "Please use the language toggle in the title bar";

        // Snapshot notification level
        LblNotifyLevel.Text = zh ? "快照通知：" : "Snapshot notifications: ";
        CbiNotifyAll.Content = zh ? "全部提示 (默认)" : "All (default)";
        CbiNotifyFailures.Content = zh ? "仅失败时提示" : "Failures only";
        CbiNotifySilent.Content = zh ? "完全静默" : "Silent";
        LblNotifyLevelDesc.Text = zh
            ? "托盘气泡通知的显示档位；快照列表的实时刷新不受影响。也可在托盘右键菜单暂停通知 24 小时。"
            : "Tray balloon level for snapshot events; live refresh of the snapshot list is unaffected. You can also snooze notifications for 24 hours from the tray menu.";

        BtnResetConfig.Content = zh ? "重置配置文件 (需重启服务)" : "Reset Config File (Restart Required)";
        BtnSaveConfig.Content = zh ? "💾  保存配置" : "💾  Save Settings";
        LblSaveNote.Text = zh
            ? "定时快照规则、低空间阈值与保护卷将保存到服务配置"
            : "Snapshot schedule, low-space threshold and protected volumes are saved to the service config";
    }

    private async Task LoadCurrentSettingsAsync()
    {
        try
        {
            var config = await ServiceClient.LoadConfigAsync();

            // Scheduled snapshots: non-empty ScheduleTimes → fixed-times mode (default); empty → interval mode (legacy rule)
            if (config.Snapshot.ScheduleTimes is { Length: > 0 } times)
            {
                ScheduleModeCombo.SelectedIndex = 0;
                _scheduleTimes = times
                    .Select(s => TimeSpan.TryParse(s, out var t) ? t : (TimeSpan?)null)
                    .Where(t => t.HasValue).Select(t => t!.Value).ToList();
                if (_scheduleTimes.Count == 0)
                    _scheduleTimes = new List<TimeSpan> { new(8, 0, 0), new(13, 0, 0), new(18, 0, 0) };
            }
            else
            {
                ScheduleModeCombo.SelectedIndex = 1;
            }
            RenderTimeChips();

            // Interval (hours): matches dropdown items 6/12/24/48, default 24 (applies in interval mode)
            RetentionPolicyCombo.SelectedIndex = config.Snapshot.IntervalHours switch
            {
                6 => 0, 12 => 1, 48 => 3, _ => 2
            };

            // Low-space suspend threshold (GB): matches dropdown items 1/2/5/10, default 2
            StorageThresholdCombo.SelectedIndex = config.Snapshot.StorageThresholdGb switch
            {
                <= 1 => 0, >= 10 => 3, >= 5 => 2, _ => 1
            };

            // 24h snapshot retention count: matches dropdown items 10/20/30/50/100, default 30
            Retention24hCombo.SelectedIndex = config.Snapshot.Retention24hCount switch
            {
                <= 10 => 0, >= 100 => 4, >= 50 => 3, >= 20 => 1, _ => 2
            };

            // Full-volume in-place restore master switch (default off)
            ChkInPlaceRestore.IsChecked = config.Snapshot.EnableInPlaceVolumeRestore;

            // Snapshot notification level: all / failures-only / silent, default all (unknown values fall back to "all")
            NotifyLevelComboBox.SelectedIndex = (config.Snapshot.NotificationLevel ?? "all").Trim().ToLowerInvariant() switch
            {
                "failures-only" => 1,
                "silent" => 2,
                _ => 0
            };

            // Show the service's actual protected volumes; fall back to local fixed drives when unconfigured
            WatchPathsText.Text = config.Snapshot.Volumes.Length > 0
                ? string.Join(", ", config.Snapshot.Volumes)
                : GetDefaultDrivePaths();

            // Custom monitored process list (the 25+ built-in ones ship with the service and are not listed here)
            AgentProcessesText.Text = config.Agent.MonitoredProcesses.Length > 0
                ? string.Join("  ", config.Agent.MonitoredProcesses)
                : "—";
        }
        catch
        {
            // Fail silently; keep the control defaults
            WatchPathsText.Text = GetDefaultDrivePaths();
            AgentProcessesText.Text = "—";
        }
    }

    private void Nav_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock tb || tb.Tag is not string tag) return;
        FrameworkElement? target = tag switch
        {
            "SectionStorage" => SectionStorage,
            "SectionAgent" => SectionAgent,
            "SectionMcp" => SectionMcp,
            "SectionSystem" => SectionSystem,
            "SectionDiag" => SectionDiag,
            _ => null
        };
        if (target == null) return;

        // Scroll the section to the top of the ScrollViewer
        target.UpdateLayout();
        var transform = target.TransformToAncestor(ContentScrollViewer);
        var topOffset = transform.Transform(new Point(0, 0)).Y;
        ContentScrollViewer.ScrollToVerticalOffset(ContentScrollViewer.VerticalOffset + topOffset);

        // Reset all nav item styles
        var activeColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1"));
        var inactiveColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A7A9B"));
        foreach (var child in NavStackPanel.Children)
        {
            if (child is TextBlock nav && nav.Tag is string)
            {
                nav.Foreground = inactiveColor;
                nav.FontWeight = FontWeights.Normal;
            }
        }
        tb.Foreground = activeColor;
        tb.FontWeight = FontWeights.SemiBold;
    }

    private void ResetConfig_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;
        var confirm = MessageBox.Show(
            zh ? "确认将所有配置重置为默认值？此操作不可撤销。"
               : "Reset all configuration to defaults? This cannot be undone.",
            "unlose", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            if (File.Exists(ConfigFilePath))
                File.Delete(ConfigFilePath);
            MessageBox.Show(
                zh ? "配置已重置，请重启服务生效。" : "Config reset. Please restart the service.",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(zh ? $"重置失败：{ex.Message}" : $"Reset failed: {ex.Message}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "unlose", "logs");
        if (Directory.Exists(logDir))
            System.Diagnostics.Process.Start("explorer.exe", logDir);
        else
            MessageBox.Show(
                LocalizationService.IsChinese ? "日志目录尚未创建。" : "Log directory does not exist yet.",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ClearTempData_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;
        var confirm = MessageBox.Show(
            zh ? "确认清理自动生成的临时缓存和过期日志文件？"
               : "Clear auto-generated temporary cache and expired log files?",
            "unlose", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var cleaned = 0;
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "unlose");
        var logDir = Path.Combine(dataDir, "logs");
        if (Directory.Exists(logDir))
        {
            foreach (var f in Directory.GetFiles(logDir, "*.log")
                         .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)))
            {
                try { File.Delete(f); cleaned++; } catch { /* ignore */ }
            }
        }
        MessageBox.Show(
            zh ? $"共清理 {cleaned} 个过期文件。" : $"Cleaned {cleaned} expired file(s).",
            "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ChangePaths_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PathListDialog(WatchPathsText.Text)
        { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (!dlg.Saved) return;
        WatchPathsText.Text = string.Join(", ", dlg.Paths);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;
        try
        {
            var config = await ServiceClient.LoadConfigAsync();

            // Scheduled snapshots: fixed-times mode writes ScheduleTimes (at least 1 time point); interval mode clears ScheduleTimes and writes IntervalHours
            if (ScheduleModeCombo.SelectedIndex == 0)
            {
                if (_scheduleTimes.Count == 0)
                {
                    MessageBox.Show(zh
                        ? "定点模式至少需要 1 个时间点，请添加或切换为按间隔周期。"
                        : "Fixed-times mode needs at least 1 time point; add one or switch to interval-based mode.",
                        "unlose", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                config.Snapshot.ScheduleTimes = _scheduleTimes
                    .Select(t => t.ToString(@"hh\:mm")).OrderBy(x => x).ToArray();
            }
            else
            {
                config.Snapshot.ScheduleTimes = [];
                config.Snapshot.IntervalHours = RetentionPolicyCombo.SelectedIndex switch
                {
                    0 => 6, 1 => 12, 3 => 48, _ => 24
                };
            }

            // Low-space suspend threshold (GB)
            config.Snapshot.StorageThresholdGb = StorageThresholdCombo.SelectedIndex switch
            {
                0 => 1, 2 => 5, 3 => 10, _ => 2
            };

            // 24h snapshot retention count
            config.Snapshot.Retention24hCount = Retention24hCombo.SelectedIndex switch
            {
                0 => 10, 1 => 20, 3 => 50, 4 => 100, _ => 30
            };

            // Full-volume in-place restore master switch
            config.Snapshot.EnableInPlaceVolumeRestore = ChkInPlaceRestore.IsChecked == true;

            // Snapshot notification level (consumed by the UI; service hot-reload unaffected)
            config.Snapshot.NotificationLevel = NotifyLevelComboBox.SelectedIndex switch
            {
                1 => "failures-only",
                2 => "silent",
                _ => "all"
            };

            // Protected volumes: parse the edit result from the "Change Volumes" dialog (staged in WatchPathsText)
            var volumes = WatchPathsText.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();
            if (volumes.Length > 0)
                config.Snapshot.Volumes = volumes;

            // Write the config file
            var dir = Path.GetDirectoryName(ConfigFilePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(ConfigFilePath, json);

            // Ask the service to hot-reload the config; on failure fall back to an "applies after restart" message
            var resp = await ServiceClient.SendAsync("RELOAD_CONFIG");
            if (resp.Success)
            {
                MessageBox.Show(zh
                    ? "设置已保存并即时生效。"
                    : "Settings saved and applied.",
                    "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(zh
                    ? $"设置已保存，将在服务重启后生效。（{resp.ErrorMessage}）"
                    : $"Settings saved; they will take effect after the service restarts. ({resp.ErrorMessage})",
                    "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(zh
                ? $"保存设置时出错：{ex.Message}"
                : $"Error saving settings: {ex.Message}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Normalize a path: drive roots keep a trailing backslash, other paths have it removed</summary>
    private static string NormalizePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return string.Empty;
        p = p.Trim();
        // Bare drive letters like "C:" or "C:\" → normalize to "C:\"
        if (p.Length == 2 && p[1] == ':')
            return p + "\\";
        if (p.Length == 3 && p[1] == ':' && p[2] == '\\')
            return p;
        // Other paths are kept as-is (trailing backslash optional)
        return p;
    }

    private static string GetDefaultDrivePaths()
        => string.Join(", ", DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => d.Name));
}
