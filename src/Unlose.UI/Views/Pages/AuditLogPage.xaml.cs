using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Unlose.Core.Models;
using Unlose.UI.Converters;
using Microsoft.Win32;

namespace Unlose.UI.Views.Pages;

public partial class AuditLogPage : Page, ILocalizable
{
    private List<AuditRowViewModel> _allRows = new();
    private List<AuditRowViewModel> _filteredRows = new();
    private int _currentPage = 1;
    private int _pageSize = 50;
    private bool _hasLoaded;
    private bool _loadFailed;

    // Debounce for live refresh: multiple snapshot events within a short window trigger only one fetch
    private int _refreshPending;

    public AuditLogPage()
    {
        InitializeComponent();
        ApplyLanguage();
        Loaded += async (_, _) => await LoadEntriesAsync();
        Loaded += AuditLogPage_Loaded;
        Unloaded += AuditLogPage_Unloaded;
    }

    public void ApplyLanguage()
    {
        if (AuditLogTitle == null) return;

        var zh = LocalizationService.IsChinese;

        AuditLogTitle.Text = zh ? "监控与审计日志" : "Monitoring & Audit Log";

        LblSearch.Text = zh ? "🔍 关键词搜索" : "🔍 Keyword Search";
        LblSeverity.Text = zh ? "级别" : "Severity";
        LblCategory.Text = zh ? "事件类型" : "Event Type";
        LblTimeRange.Text = zh ? "时间范围" : "Time Range";
        LblPageSize.Text = zh ? "每页显示" : "Per page";
        LblTotalCount.Text = zh ? "共 0 条" : "0 total";

        RefreshButton.Content = zh ? "刷新" : "Refresh";
        ExportButton.Content = zh ? "📥 导出 CSV" : "📥 Export CSV";

        CbiSeverityAll.Content = zh ? "全部级别" : "All Levels";
        CbiSeverityHigh.Content = zh ? "警示 (High/Critical)" : "Warning (High/Critical)";
        CbiSeverityInfo.Content = zh ? "正常 (Info/OK)" : "Normal (Info/OK)";
        CbiSeverityFail.Content = zh ? "失败 (FAIL)" : "Failed (FAIL)";

        CbiCategoryAll.Content = zh ? "全部事件" : "All Events";
        CbiCategoryAgentSession.Content = zh ? "Agent 进程" : "Agent Processes";
        CbiCategorySystemRestore.Content = zh ? "系统还原" : "System Restore";
        CbiCategorySnapshot.Content = zh ? "快照事件" : "Snapshot Events";
        CbiCategoryStorage.Content = zh ? "存储告警" : "Storage Alerts";
        CbiCategoryProtection.Content = zh ? "保护状态变更" : "Protection State Changed";

        ColTime.Text = zh ? "时间" : "Time";
        ColLevel.Text = zh ? "级别" : "Level";
        ColActor.Text = zh ? "来源进程" : "Source Process";
        ColAction.Text = zh ? "事件摘要" : "Summary";

        RunMatchPrefix.Text = zh ? "应用当前筛选项，匹配 " : "Matches ";
        RunMatchSuffix.Text = zh ? " 条审计日志" : " entries";

        BtnFirstPage.Content = zh ? "⏮ 首页" : "⏮ First";
        BtnPrevPage.Content = zh ? "◀ 上一页" : "◀ Prev";
        BtnNextPage.Content = zh ? "下一页 ▶" : "Next ▶";
        BtnLastPage.Content = zh ? "末页 ⏭" : "Last ⏭";

        Last24HoursRadio.Content = zh ? "近24h" : "24h";
        Last7DaysRadio.Content = zh ? "近7天" : "7d";
        Last30DaysRadio.Content = zh ? "近30天" : "30d";

        UpdatePageInfoText();
        UpdateTotalCountText();
        UpdateEmptyState();

        // Row-level buttons and expander section titles live in the DataTemplate and are localized via Loaded; refresh the container to re-trigger them
        AuditItemsControl?.Items.Refresh();
    }

    /// <summary>
    /// Entry point for live refresh: called by MainWindow when a snapshot created/failed notification arrives.
    /// A 500ms debounce coalesces consecutive events. Safe to call from any thread.
    /// </summary>
    public void RequestRefresh()
    {
        Interlocked.Exchange(ref _refreshPending, 1);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(500);
            if (Interlocked.CompareExchange(ref _refreshPending, 0, 1) != 1) return;
            await LoadEntriesAsync();
        });
    }

    public async Task LoadEntriesAsync()
    {
        if (RefreshButton == null) return;

        var rows = new List<AuditRowViewModel>();

        try
        {
            RefreshButton.IsEnabled = false;

            var days = GetSelectedDays();
            // Category filtering is now done client-side, so pull the full data from both sources here
            var auditEntries = await ServiceClient.ListAuditLogAsync(days: days);
            rows.AddRange(auditEntries.Select(entry => new AuditRowViewModel
            {
                TimestampValue = entry.Timestamp.ToLocalTime(),
                Timestamp = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Category = entry.Action,
                Action = entry.Action,
                Actor = entry.Actor,
                Details = entry.Details ?? string.Empty,
                Level = entry.Success ? "OK" : "FAIL"
            }));

            var monitorEvents = await ServiceClient.ListMonitorEventsAsync(days: days, max: 2000);
            rows.AddRange(monitorEvents.Select(evt => new AuditRowViewModel
            {
                TimestampValue = evt.OccurredAt.ToLocalTime(),
                Timestamp = evt.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Category = evt.EventType,
                Action = evt.EventType,
                Actor = $"{evt.ProcessName} (PID={evt.Pid})",
                // Legacy databases may leak English enum names into Description; replace them with localized display names before showing
                Details = UiText.ReplaceTriggerTypeNames(evt.Description, LocalizationService.IsChinese),
                Level = evt.Severity?.ToString() ?? "INFO"
            }));

            _allRows = rows
                .OrderByDescending(row => row.TimestampValue)
                .ToList();

            _loadFailed = false;
            _hasLoaded = true;
            _currentPage = 1;
            ApplyFilters();
        }
        catch
        {
            _allRows = rows;
            _loadFailed = true;
            _hasLoaded = true;
            _currentPage = 1;
            ApplyFilters();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilters()
    {
        if (AuditItemsControl == null) return;

        IEnumerable<AuditRowViewModel> query = _allRows;

        var searchText = SearchTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(row =>
                row.Actor.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                row.Action.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                row.Details.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        // Category filter: client-side local filtering, grouped by real event types (audit source + monitor source)
        var categoryTag = GetSelectedCategory();
        query = categoryTag switch
        {
            "AgentSession" => query.Where(row => row.Category is "AgentSessionStarted" or "AgentSessionEnded"),
            "Snapshot" => query.Where(row => row.Category is "SnapshotCreated" or "SnapshotFailed" or "SnapshotPurged"),
            "SystemRestoreApplied" or "StorageLow" or "ProtectionStateChanged" =>
                query.Where(row => string.Equals(row.Category, categoryTag, StringComparison.OrdinalIgnoreCase)),
            _ => query
        };

        // Actual severity semantics: OK/FAIL for the audit source, Info/High(/Critical) for the monitor source
        var severityTag = (SeverityFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
        query = severityTag switch
        {
            "HIGH_OR_CRITICAL" => query.Where(row => row.Level is "High" or "Critical"),
            "INFO_OR_OK" => query.Where(row => row.Level is "OK" or "INFO" or "Info"),
            "FAIL" => query.Where(row => row.Level == "FAIL"),
            _ => query
        };

        _filteredRows = query.OrderByDescending(row => row.TimestampValue).ToList();
        _currentPage = Math.Min(_currentPage, Math.Max(1, TotalPages));
        RenderPage();
    }

    private int TotalPages => Math.Max(1, (int)Math.Ceiling((double)_filteredRows.Count / _pageSize));

    private void RenderPage()
    {
        var page = _filteredRows
            .Skip((_currentPage - 1) * _pageSize)
            .Take(_pageSize)
            .ToList();

        AuditItemsControl.ItemsSource = page;
        if (ResultCountText != null) ResultCountText.Text = _filteredRows.Count.ToString();
        UpdatePageInfoText();
        UpdateTotalCountText();
        UpdateEmptyState();
    }

    private void UpdatePageInfoText()
    {
        if (TxtPageInfo == null) return;
        var zh = LocalizationService.IsChinese;
        TxtPageInfo.Text = zh
            ? $"第 {_currentPage} / {TotalPages} 页"
            : $"Page {_currentPage} / {TotalPages}";
    }

    private void UpdateTotalCountText()
    {
        if (LblTotalCount == null) return;
        var zh = LocalizationService.IsChinese;
        LblTotalCount.Text = zh ? $"共 {_filteredRows.Count} 条" : $"{_filteredRows.Count} total";
    }

    private void UpdateEmptyState()
    {
        if (EmptyStatePanel == null) return;

        if (!_hasLoaded)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var zh = LocalizationService.IsChinese;

        if (_loadFailed)
        {
            EmptyStateTitle.Text = zh ? "无法加载日志" : "Could Not Load Logs";
            EmptyStateDesc.Text = zh
                ? "服务未连接或发生错误。请确认服务正在运行后重试。"
                : "The service is not connected or an error occurred. Make sure the service is running, then retry.";
            BtnRetryLoad.Content = zh ? "重试" : "Retry";
            BtnRetryLoad.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
        else if (_filteredRows.Count == 0)
        {
            EmptyStateTitle.Text = zh ? "暂无匹配日志" : "No Matching Log Entries";
            EmptyStateDesc.Text = zh
                ? "当前筛选条件下没有日志记录。"
                : "No entries match the current filters.";
            BtnRetryLoad.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void RetryLoad_Click(object sender, RoutedEventArgs e)
        => await LoadEntriesAsync();

    private int GetSelectedDays()
    {
        if (Last24HoursRadio.IsChecked == true) return 1;
        if (Last7DaysRadio.IsChecked == true) return 7;
        return 30;
    }

    private string GetSelectedCategory()
        => (CategoryFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentPage = 1;
        ApplyFilters();
    }

    private void SeverityFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentPage = 1;
        ApplyFilters();
    }

    private void CategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentPage = 1;
        ApplyFilters();
    }

    private async void TimeRangeRadio_Checked(object sender, RoutedEventArgs e)
        => await LoadEntriesAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await LoadEntriesAsync();

    private void PageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeComboBox?.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out var size))
        {
            _pageSize = size;
            _currentPage = 1;
            ApplyFilters();
        }
    }

    private void BtnFirstPage_Click(object sender, RoutedEventArgs e) { _currentPage = 1; RenderPage(); }
    private void BtnPrevPage_Click(object sender, RoutedEventArgs e) { if (_currentPage > 1) { _currentPage--; RenderPage(); } }
    private void BtnNextPage_Click(object sender, RoutedEventArgs e) { if (_currentPage < TotalPages) { _currentPage++; RenderPage(); } }
    private void BtnLastPage_Click(object sender, RoutedEventArgs e) { _currentPage = TotalPages; RenderPage(); }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = $"unlose-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };

        if (dialog.ShowDialog() != true) return;

        var rows = _filteredRows;
        var lines = new List<string> { "Timestamp,Category,Action,Actor,Level,Details" };
        lines.AddRange(rows.Select(row => string.Join(",",
            EscapeCsv(row.Timestamp),
            EscapeCsv(row.Category),
            EscapeCsv(row.Action),
            EscapeCsv(row.Actor),
            EscapeCsv(row.Level),
            EscapeCsv(row.Details))));
        File.WriteAllLines(dialog.FileName, lines);
    }

    private void AuditLogPage_Loaded(object sender, RoutedEventArgs e)
    {
        ServiceClient.NotificationReceived += ServiceClient_NotificationReceived;
        ServiceClient.EnsureEventSubscriptionStarted();
    }

    private void AuditLogPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceClient.NotificationReceived -= ServiceClient_NotificationReceived;
    }

    private void ServiceClient_NotificationReceived(object? sender, ServiceClient.ServiceNotificationEventArgs e)
    {
        if (e.Type == "ServiceHeartbeatNotification") return;
        Dispatcher.InvokeAsync(async () => await LoadEntriesAsync());
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // Elements inside the DataTemplate can't be handled by ApplyLanguage; set their text per current language via the Loaded event
    private void BtnExtractSnapshot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
            btn.Content = LocalizationService.IsChinese ? "创建快照" : "Create Snapshot";
    }

    private void LblDetailsTitle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb)
            tb.Text = LocalizationService.IsChinese ? "事件详情" : "Event Details";
    }

    private async void ExtractSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as AuditRowViewModel;
        if (row == null) return;

        var zh = LocalizationService.IsChinese;
        var resp = await ServiceClient.SendAsync("CREATE_SNAPSHOT",
            new Dictionary<string, string>
            {
                ["label"] = zh
                    ? $"兜底快照-{row.Actor}-{DateTime.Now:HHmmss}"
                    : $"fallback-snapshot-{row.Actor}-{DateTime.Now:HHmmss}",
                ["triggerDetail"] = $"AuditLog manual fallback for actor: {row.Actor}"
            });

        if (resp.Success)
            MessageBox.Show(
                zh ? "快照已创建，可在快照管理中查阅。" : "Snapshot created. View it in Snapshot Management.",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(
                zh ? $"创建快照失败：{resp.ErrorMessage}" : $"Failed to create snapshot: {resp.ErrorMessage}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

public class AuditRowViewModel
{
    // Keep only the event types backed by a real data source (three from audit_log + four backfilled from monitor_events)
    private static readonly Dictionary<string, (string zh, string en)> EventTypeMap = new()
    {
        ["AgentSessionStarted"] = ("Agent 进程启动", "Agent Process Started"),
        ["AgentSessionEnded"] = ("Agent 进程结束", "Agent Process Ended"),
        ["SystemRestoreApplied"] = ("系统还原已执行", "System Restore Applied"),
        ["SnapshotCreated"] = ("快照创建", "Snapshot Created"),
        ["SnapshotFailed"] = ("快照创建失败", "Snapshot Creation Failed"),
        ["SnapshotPurged"] = ("快照已按保留策略清理", "Snapshot Purged by Retention Policy"),
        ["StorageLow"] = ("存储空间不足", "Storage Low"),
        ["ProtectionStateChanged"] = ("保护状态变更", "Protection State Changed"),
    };

    public DateTime TimestampValue { get; set; }
    public string Timestamp { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;

    public string LocalizedAction => LocalizeEventType(Action);
    public string LocalizedLevel => LocalizeLevel(Level);

    private static string LocalizeEventType(string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return "—";
        var zh = LocalizationService.IsChinese;
        return EventTypeMap.TryGetValue(eventType, out var map)
            ? (zh ? map.zh : map.en)
            : eventType;
    }

    private static string LocalizeLevel(string level)
    {
        if (string.IsNullOrEmpty(level)) return "—";
        var zh = LocalizationService.IsChinese;
        // Actual level semantics: OK/FAIL for the audit source, Info/High(/Critical) for the monitor source
        return level switch
        {
            "OK" => zh ? "成功" : "OK",
            "FAIL" => zh ? "失败" : "FAIL",
            "INFO" or "Info" => zh ? "信息" : "Info",
            "High" => zh ? "高" : "High",
            "Critical" => zh ? "严重" : "Critical",
            _ => level
        };
    }
}
