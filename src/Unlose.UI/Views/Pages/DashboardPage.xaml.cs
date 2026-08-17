using Unlose.Core.Models;
using Unlose.UI.Converters;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Unlose.UI.Views.Pages;

internal sealed class AlertItemViewModel
{
    public string TimeText { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCritical { get; set; }
}

public partial class DashboardPage : Page, ILocalizable
{
    public DashboardPage()
    {
        InitializeComponent();
        ApplyLanguage();
        Loaded += DashboardPage_Loaded;
        Unloaded += DashboardPage_Unloaded;
    }

    public void ApplyLanguage()
    {
        if (StatusText == null) return;

        var zh = LocalizationService.IsChinese;

        // Brand slogan (official website hero line)
        SloganText.Text = zh ? "AI 删掉的东西，用 unlose 找回来" : "Unlose what your AI agent deleted";

        RunSubtitlePrefix.Text = zh ? "保护服务运行正常 • 本地共有 " : "Service running • ";
        RunSubtitleSuffix.Text = zh ? " 个快照可供恢复" : " snapshots available for recovery";

        BtnRestoreFromHistory.Content = zh ? "⟲  从历史快照中恢复文件" : "⟲  Restore from Snapshots";
        BtnQuickSnapshot.Content = zh ? "立即补充一拍" : "Create Snapshot";

        LblStorageCardTitle.Text = zh ? "💽  系统级存储保护态势" : "💽  System Storage Status";
        LblStorageUsage.Text = zh ? "存储用量" : "Storage Usage";
        LblAutoSnapshot.Text = zh ? "自动快照" : "Auto Snapshot";

        LblAgentCardTitle.Text = zh ? "🤖  Agent 集成状态" : "🤖  Agent Integration";
        LblAlertsTitle.Text = zh ? "📋  近期事件 (Top 5)" : "📋  Recent Events (Top 5)";
        AuditLinkText.Text = zh ? "查看完整审计日志 →" : "View Full Audit Log →";

        CreateSnapshotBtn.Content = zh ? "立即手动创建快照" : "Create Snapshot Now";
        RefreshUpdateButton();

        // Do not trigger a load during construction (before Loaded), to avoid double-loading with the Loaded event
        if (IsLoaded)
            _ = LoadStatusAsync();
    }

    // Home page "Update" button: shown only when the daily auto-check cache reports a newer version; clicking goes straight to the download page
    private void RefreshUpdateButton()
    {
        try
        {
            var zh = LocalizationService.IsChinese;
            var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            var available = UiSettings.IsUpdateAvailable(current);
            BtnUpdate.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            BtnUpdate.Content = available && !string.IsNullOrEmpty(UiSettings.LatestKnownVersion)
                ? (zh ? $"⬆  更新到 {UiSettings.LatestKnownVersion}" : $"⬆  Update to {UiSettings.LatestKnownVersion}")
                : (zh ? "⬆  更新" : "⬆  Update");
        }
        catch
        {
            BtnUpdate.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                string.IsNullOrWhiteSpace(UiSettings.LatestDownloadUrl) ? "https://unlose.app/" : UiSettings.LatestDownloadUrl!)
            { UseShellExecute = true });
        }
        catch { /* Browser launch failure is non-blocking */ }
    }

    private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        ServiceClient.NotificationReceived += ServiceClient_NotificationReceived;
        ServiceClient.EnsureEventSubscriptionStarted();
        _ = LoadStatusAsync();
    }

    private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceClient.NotificationReceived -= ServiceClient_NotificationReceived;
    }

    // Fetch Service status when the page loads (must be called after Loaded so Application resources can be resolved)
    private async Task LoadStatusAsync()
    {
        var zh = LocalizationService.IsChinese;
        try
        {
            var resp = await ServiceClient.SendAsync("STATUS");
            var isPaused = resp.Success && resp.Data?.Contains("IsPaused=True") == true;
            var isSuspended = resp.Success && resp.Data?.Contains("IsSuspended=True") == true;

            // Four hero states: service offline > protection paused > suspended (low disk) > normal.
            // Emoji prefixes replaced by StatusDot color (green/amber/red) set alongside the text.
            if (!resp.Success)
            {
                StatusText.Text = zh ? "服务离线，正在尝试重连…" : "Service Offline, Reconnecting…";
                StatusText.Foreground = HexBrush("#B91C1C");
                StatusDot.Fill = HexBrush("#EF4444");
            }
            else if (isPaused)
            {
                StatusText.Text = zh ? "保护已暂停" : "Protection Paused";
                StatusText.Foreground = HexBrush("#B45309");
                StatusDot.Fill = HexBrush("#F59E0B");
            }
            else if (isSuspended)
            {
                StatusText.Text = zh ? "自动快照已挂起：存储空间不足" : "Auto-Snapshot Suspended: Low Disk Space";
                StatusText.Foreground = HexBrush("#B45309");
                StatusDot.Fill = HexBrush("#F59E0B");
            }
            else
            {
                StatusText.Text = zh ? "保护中" : "Protected";
                StatusText.Foreground = HexBrush("#0C4A6E");
                StatusDot.Fill = HexBrush("#22C55E");
            }

            // Subtitle prefix follows the state (the snapshot count is filled in by SnapshotCountText below)
            RunSubtitlePrefix.Text = !resp.Success
                ? (zh ? "服务离线 • 本地共有 " : "Service offline • ")
                : isPaused
                    ? (zh ? "保护已暂停 • 本地共有 " : "Paused • ")
                    : isSuspended
                        ? (zh ? "存储不足，自动快照已挂起 • 本地共有 " : "Auto-snapshot suspended (low disk) • ")
                        : (zh ? "保护服务运行正常 • 本地共有 " : "Service running • ");

            UpdateStorageUsage(resp.Success, isSuspended);

            var snapshots = await ServiceClient.ListSnapshotsAsync();
            SnapshotCountText.Text = snapshots.Count.ToString();

            var todayAlerts = await ServiceClient.ListMonitorEventsAsync(days: 1, max: 500);
            // Count shows all of today's events; use the yellow warning badge only when High/Critical events exist, otherwise green
            var hasSevereAlerts = todayAlerts.Any(evt =>
                evt.Severity?.ToString()?.Contains("Critical", StringComparison.OrdinalIgnoreCase) == true
                || evt.Severity?.ToString()?.Contains("High", StringComparison.OrdinalIgnoreCase) == true);
            AlertCountText.Text = zh ? $"今日 {todayAlerts.Count} 条" : $"{todayAlerts.Count} today";
            AlertCountText.Foreground = ResolveBrush(hasSevereAlerts ? "WarningBrush" : "SuccessBrush");
            AlertCountBadge.Background = HexBrush(hasSevereAlerts ? "#FEF3C7" : "#DCFCE7");
            AlertCountBadge.BorderBrush = HexBrush(hasSevereAlerts ? "#FDE68A" : "#BBF7D0");

            var activeSessions = await ServiceClient.ListAgentSessionsAsync(activeOnly: true);

            // Protection criterion: any snapshot within the cooldown window counts as protected
            // (a session whose snapshot was skipped by the same-process cooldown is not unprotected)
            var cooldownMin = 60;
            try
            {
                var agentCfg = await ServiceClient.LoadConfigAsync();
                cooldownMin = agentCfg.Agent.AgentSnapshotCooldownMinutes;
            }
            catch { /* Fall back to the default 60-minute cooldown when config reading fails */ }

            var latestSnapshotAt = snapshots.Count > 0 ? snapshots.Max(s => s.CreatedAt) : DateTime.MinValue;
            var hasRecentSnapshot = latestSnapshotAt >= DateTime.UtcNow.AddMinutes(-cooldownMin);
            var hasUnprotected = activeSessions.Count > 0 && !hasRecentSnapshot;

            UpdateAgentStatus(activeSessions, hasRecentSnapshot);
            UpdateWarningBanner(resp.Success, hasUnprotected);
            RefreshUpdateButton();

            // Dynamically load the Top 5 events
            await LoadTop5AlertsAsync(todayAlerts);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                zh ? $"加载仪表板状态时出错：{ex.Message}" : $"Failed to load dashboard status: {ex.Message}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Brush ResolveBrush(string key)
    {
        return TryFindResource(key) as Brush
            ?? Application.Current?.TryFindResource(key) as Brush
            ?? Brushes.Gray;
    }

    // Build a brush from a #RRGGBB literal (badge/status row colors, kept in sync with the static color values in XAML)
    private static Brush HexBrush(string hex)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    // Create snapshot now (shared by the hero "Create Snapshot" button and the warning banner button): busy state prevents double-clicks
    private async void CreateSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;
        var dlg = new CreateSnapshotDialog { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (!dlg.Confirmed) return;

        var btn = sender as Button;
        var originalContent = btn?.Content;
        if (btn is not null)
        {
            btn.IsEnabled = false;
            btn.Content = zh ? "创建中..." : "Creating...";
        }
        try
        {
            // No explicit volume: the service snapshots every configured volume (config.snapshot.volumes).
            // Previously this hard-coded C:\ and silently skipped other protected volumes.
            var resp = await ServiceClient.SendAsync("CREATE_SNAPSHOT",
                new Dictionary<string, string> { ["description"] = dlg.Description });
            if (resp.Success)
                MessageBox.Show(zh ? "快照已创建。" : "Snapshot created.", "unlose",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(zh ? $"创建失败：{resp.ErrorMessage}" : $"Failed: {resp.ErrorMessage}", "unlose",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(zh ? $"快照创建失败：{ex.Message}" : $"Snapshot failed: {ex.Message}", "unlose",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (btn is not null)
            {
                btn.IsEnabled = true;
                btn.Content = originalContent;
            }
            _ = LoadStatusAsync();
        }
    }

    // Storage usage row (read C:\ directly via local DriveInfo) + auto-snapshot protection status row (refreshed from STATUS's IsSuspended)
    private void UpdateStorageUsage(bool serviceOnline, bool isSuspended)
    {
        var zh = LocalizationService.IsChinese;
        try
        {
            var drive = new DriveInfo("C");
            if (!drive.IsReady) throw new IOException("Drive C: not ready");
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var totalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
            StorageUsageBar.Value = totalGb > 0 ? (totalGb - freeGb) / totalGb * 100 : 0;
            StorageUsageText.Text = zh
                ? $"可用 {freeGb:F1} GB / 共 {totalGb:F1} GB"
                : $"{freeGb:F1} GB free of {totalGb:F1} GB";
        }
        catch (Exception)
        {
            StorageUsageBar.Value = 0;
            StorageUsageText.Text = "—";
        }

        if (!serviceOnline)
        {
            StorageGuardStatusText.Text = "—";
            StorageGuardStatusText.Foreground = HexBrush("#4A7A9B");
        }
        else if (isSuspended)
        {
            StorageGuardStatusText.Text = zh ? "已挂起（存储空间不足）" : "Suspended (Low Disk)";
            StorageGuardStatusText.Foreground = HexBrush("#B91C1C");
        }
        else
        {
            StorageGuardStatusText.Text = zh ? "正常" : "Normal";
            StorageGuardStatusText.Foreground = HexBrush("#15803D");
        }
    }

    // Warning banner has two mutually exclusive states (service offline red > unprotected-snapshot yellow), plus hidden;
    // only the "unprotected snapshot" state shows the "Create Snapshot Now" button
    private void UpdateWarningBanner(bool serviceOnline, bool hasUnprotected)
    {
        var zh = LocalizationService.IsChinese;
        if (!serviceOnline)
        {
            ServiceWarningBanner.Visibility = Visibility.Visible;
            ServiceWarningBanner.Background = ResolveBrush("DangerBrush");
            WarningBannerText.Text = zh ? "⚠️ 无法连接 unlose 服务，请检查服务是否正在运行。" : "⚠️ Cannot connect to unlose service. Check if it's running.";
            CreateSnapshotBtn.Visibility = Visibility.Collapsed;
            return;
        }

        if (hasUnprotected)
        {
            ServiceWarningBanner.Visibility = Visibility.Visible;
            ServiceWarningBanner.Background = HexBrush("#FEF9C3");
            ServiceWarningBanner.BorderBrush = HexBrush("#FDE68A");
            WarningBannerText.Text = zh ? "检测到 Agent 正在运行，但近期没有保护快照。若 Agent 误改文件将无法回退，建议立即创建快照。"
                                        : "An Agent is running but no recent protection snapshot exists. Agent changes cannot be rolled back — create a snapshot now.";
            CreateSnapshotBtn.Visibility = Visibility.Visible;
            return;
        }

        ServiceWarningBanner.Visibility = Visibility.Collapsed;
    }

    private void UpdateAgentStatus(IReadOnlyCollection<AgentSessionRecord> sessions, bool hasRecentSnapshot)
    {
        var zh = LocalizationService.IsChinese;

        // Group by process name: merge multiple process sessions of the same Agent app (e.g. ZCode's multi-process architecture) into one Agent,
        // so "24 ZCode processes" is not misread as "24 Agents running"
        var groups = sessions
            .GroupBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();
        var agentCount = groups.Count;
        var processCount = sessions.Count;

        // Top-right badge: number of Agent apps (gray when there are no sessions)
        var hasSessions = agentCount > 0;
        AgentSessionBadgeText.Text = zh ? $"{agentCount} 个 Agent 运行中" : $"{agentCount} agent(s) running";
        AgentSessionBadgeText.Foreground = HexBrush(hasSessions ? "#15803D" : "#6B7280");
        AgentSessionBadge.Background = HexBrush(hasSessions ? "#DCFCE7" : "#F3F4F6");
        AgentSessionBadge.BorderBrush = HexBrush(hasSessions ? "#BBF7D0" : "#E5E7EB");

        if (agentCount == 0)
        {
            AgentStatusText.Text = zh ? "⚪ 当前无运行中的受监控 Agent" : "⚪ No monitored Agents running";
            AgentStatusText.Foreground = ResolveBrush("TextSecondaryBrush");
            AgentMetaText.Text = zh ? "未检测到受监控的 Agent 进程。" : "No monitored Agent processes detected.";
            return;
        }

        // Details: ZCode × 24 · Kimi × 5 · codex × 1 (process names with the .exe suffix stripped)
        var meta = string.Join(" · ", groups.Select(g =>
            $"{g.Key.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)} × {g.Count()}"));
        var countText = processCount > agentCount ? $"{agentCount} 个 Agent（{processCount} 个进程）" : $"{agentCount} 个 Agent";
        var countTextEn = processCount > agentCount ? $"{agentCount} agent(s) ({processCount} processes)" : $"{agentCount} agent(s)";

        if (hasRecentSnapshot)
        {
            AgentStatusText.Text = zh
                ? $"🟢 {countText} 运行中，保护快照已就绪"
                : $"🟢 {countTextEn} running, protection snapshot ready";
            AgentStatusText.Foreground = ResolveBrush("SuccessBrush");
            AgentMetaText.Text = meta;
        }
        else
        {
            AgentStatusText.Text = zh
                ? $"⚠️ {countText} 运行中，暂无保护快照"
                : $"⚠️ {countTextEn} running, no protection snapshot yet";
            AgentStatusText.Foreground = ResolveBrush("WarningBrush");
            AgentMetaText.Text = zh ? $"{meta} — 建议立即创建快照。" : $"{meta} — Consider creating a snapshot now.";
        }
    }

    private void ServiceClient_NotificationReceived(object? sender, ServiceClient.ServiceNotificationEventArgs e)
    {
        Dispatcher.InvokeAsync(async () => await LoadStatusAsync());
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.MainFrame.Navigate(new RestoreWizard());
    }

    private void NavigateToAuditLog_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.MainFrame.Navigate(new AuditLogPage());
    }

    private Task LoadTop5AlertsAsync(List<Unlose.Core.Models.MonitorEventRecord> events)
    {
        if (AlertsItemsControl == null) return Task.CompletedTask;
        var zh = LocalizationService.IsChinese;
        var top5 = events
            .OrderByDescending(evt => evt.OccurredAt)
            .Take(5)
            .Select(evt => new AlertItemViewModel
            {
                TimeText = evt.OccurredAt.ToLocalTime().ToString("HH:mm:ss"),
                Actor = evt.ProcessName ?? "system",
                // Tolerate English enum-name leakage in legacy database Descriptions; replace with localized display names before showing
                Description = UiText.ReplaceTriggerTypeNames(evt.Description, zh),
                IsCritical = evt.Severity?.ToString()?.Contains("Critical", StringComparison.OrdinalIgnoreCase) == true
                           || evt.Severity?.ToString()?.Contains("High", StringComparison.OrdinalIgnoreCase) == true
            })
            .ToList();
        AlertsItemsControl.ItemsSource = top5;
        return Task.CompletedTask;
    }
}
