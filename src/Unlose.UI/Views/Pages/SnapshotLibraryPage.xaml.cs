using Unlose.Core.Enums;
using Unlose.Core.Models;
using Unlose.UI.Converters;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Unlose.UI.Views.Pages;

public partial class SnapshotLibraryPage : Page, ILocalizable
{
    private List<SnapshotRecord> _allSnapshots = new();
    private List<SnapshotRecord> _filteredSnapshots = new();
    private readonly HashSet<Guid> _selectedIds = new();
    private int _filterDays = 1;
    private string _filterTriggerType = string.Empty;
    private int _currentPage = 1;
    private int _pageSize = 50;

    // Debounce for live refresh: multiple SnapshotCreatedNotification events within a short window
    // trigger only one fetch, so back-to-back Agent sessions (several pre-session snapshots within a minute) don't flood the LIST_SNAPSHOTS IPC.
    private readonly CancellationTokenSource _refreshDebounceCts = new();
    private int _refreshPending;

    public SnapshotLibraryPage()
    {
        InitializeComponent();
        ApplyLanguage();
        ResetDrawer();
        _ = UpdateFullRestoreVisibilityAsync();
        _ = RefreshAsync();
    }

    // In-place full-volume restore is an advanced, destructive feature gated behind a settings
    // switch (default off): the button stays hidden until the user enables it in Settings.
    // The service enforces the same gate, so hiding here is UX, not security.
    private async Task UpdateFullRestoreVisibilityAsync()
    {
        try
        {
            var config = await ServiceClient.LoadConfigAsync();
            BtnFullRestore.Visibility = config.Snapshot.EnableInPlaceVolumeRestore ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { BtnFullRestore.Visibility = Visibility.Collapsed; }
    }

    // Gray out the in-place restore button when the selected snapshot targets the system volume
    // (the service refuses system volumes; disabling here explains why upfront instead of letting
    // the user walk into a rejection). TooltipService.ShowOnDisabled keeps the tooltip visible.
    private void UpdateFullRestoreEnabled(SnapshotRecord? s)
    {
        if (BtnFullRestore == null) return;
        var sysVol = s != null && VolumeSafety.IsSystemVolume(s.VolumePath);
        BtnFullRestore.IsEnabled = !sysVol;
        ToolTipService.SetShowOnDisabled(BtnFullRestore, true);
        BtnFullRestore.ToolTip = sysVol
            ? (LocalizationService.IsChinese
                ? "整卷原位还原不支持系统卷，请使用 Windows 系统还原。"
                : "In-place full-volume restore is not supported on the system volume. Use Windows System Restore instead.")
            : null;
    }

    public void ApplyLanguage()
    {
        if (PageTitle == null) return;

        var zh = LocalizationService.IsChinese;

        PageTitle.Text = zh ? "快照列表" : "Snapshot List";
        LblRetentionNote.Text = zh
            ? "保留策略：最近 24 小时完整保留（默认最多 30 个）；更早的每天保留最早和最新 2 个，7~30 天每周保留 1 个，超过 30 天自动清理；锁定 🔒 的快照永不清理。"
            : "Retention: last 24h fully kept (max 30 by default); older snapshots keep the earliest + latest of each day, one per week for 7–30d, auto-purged after 30d; pinned 🔒 snapshots are kept forever.";
        RefreshSnapshotBtn.Content = zh ? "刷新" : "Refresh";
        BtnBatchDelete.Content = zh ? "批量删除" : "Batch Delete";

        ColTime.Text = zh ? "创建时间" : "Created";
        ColTrigger.Text = zh ? "触发类型" : "Trigger";
        ColDesc.Text = zh ? "描述" : "Description";
        ColStatus.Text = zh ? "状态" : "Status";

        CbiTriggerAll.Content = zh ? "全部触发类型" : "All Trigger Types";
        CbiTriggerScheduled.Content = zh ? "定时快照" : "Scheduled";
        CbiTriggerAgentPre.Content = zh ? "Agent 启动前" : "Agent Startup";
        CbiTriggerAgentInit.Content = zh ? "Agent 主动快照" : "Agent Initiated";
        CbiTriggerManual.Content = zh ? "手动" : "Manual";
        CbiTriggerPreRestore.Content = zh ? "还原前备份" : "Pre-Restore";

        Filter24h.Content = zh ? "近24小时" : "24h";
        Filter7d.Content = zh ? "近7天" : "7d";
        FilterAll.Content = zh ? "全部历史" : "All";

        LblContextMeta.Text = zh ? "触发详情" : "Trigger Detail";
        LblVolumes.Text = zh ? "保护卷" : "Volumes";
        LblSession.Text = zh ? "会话标识" : "Session ID";
        LblPinCheck.Text = zh ? "🔒  置为永久保留快照点" : "🔒  Pin as Permanent";
        LblPinDesc.Text = zh
            ? "打上标记后，系统在磁盘耗尽时也绝不会淘汰清理此节点的数据。"
            : "Pinned snapshots are never pruned, even when disk space is low.";

        BtnImmersiveRestore.Content = zh ? "👁  沉浸式选定文件挑拣恢复" : "👁  Immersive File Restore";
        BtnFullRestore.Content = zh ? "📦  整卷原位还原" : "📦  Full Volume Restore";
        BtnDelete.Content = zh ? "删除" : "Delete";

        LblPageSize.Text = zh ? "每页显示" : "Per page";
        LblTotalCount.Text = zh ? "共 0 条" : "0 total";
        BtnFirstPage.Content = zh ? "⏮ 首页" : "⏮ First";
        BtnPrevPage.Content = zh ? "◀ 上一页" : "◀ Prev";
        BtnNextPage.Content = zh ? "下一页 ▶" : "Next ▶";
        BtnLastPage.Content = zh ? "末页 ⏭" : "Last ⏭";

        UpdatePageInfoText();
        UpdateTotalCountText();
        UpdateEmptyState();

        // Sync the drawer placeholder / trigger-type text when the language changes
        if (SnapshotList?.SelectedItem is SnapshotRecord sel)
        {
            DrawerTrigger.Text = GetTriggerDisplayText(sel, zh);
            UpdateFullRestoreEnabled(sel); // re-localize the system-volume tooltip
        }
        else
            ResetDrawer();

        // Chips / status column inside the DataTemplate go through converters; refresh the items to re-evaluate them
        SnapshotList?.Items.Refresh();
    }

    /// <summary>Display text for the trigger type, sharing the same mapping as the list chip (Converters/UiTextConverters.cs);
    /// the chip shows only the type name, while the B-side source/notes are carried by the description column and the drawer's "Trigger Detail"</summary>
    private static string GetTriggerDisplayText(SnapshotRecord s, bool zh) => UiText.TriggerChip(s.TriggerType, zh);

    /// <summary>Shows placeholder text in the drawer when no snapshot is selected</summary>
    private void ResetDrawer()
    {
        if (DrawerTimestamp == null) return;
        var zh = LocalizationService.IsChinese;
        DrawerTimestamp.Text = zh ? "未选择快照" : "No snapshot selected";
        DrawerSnapshotId.Text = "Object ID: —";
        DrawerTrigger.Text = "—";
        DrawerContextMeta.Text = "—";
        DrawerVolumes.Text = "—";
        DrawerSessionId.Text = "—";
        PinnedCheckBox.IsChecked = false;
        UpdateFullRestoreEnabled(null);
    }

    /// <summary>Shows the empty-state panel when the list is empty (no snapshots, or no filter matches)</summary>
    private void UpdateEmptyState()
    {
        if (EmptyStatePanel == null) return;
        var zh = LocalizationService.IsChinese;
        var hasData = _filteredSnapshots.Count > 0;
        EmptyStatePanel.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        if (hasData) return;

        if (_allSnapshots.Count == 0)
        {
            EmptyStateTitle.Text = zh ? "暂无快照" : "No snapshots yet";
            EmptyStateHint.Text = zh ? "可在概览页立即补充一拍" : "Take one now from the Overview page.";
        }
        else
        {
            EmptyStateTitle.Text = zh ? "没有符合筛选条件的快照" : "No snapshots match the filters";
            EmptyStateHint.Text = zh ? "试试调整时间或触发类型筛选" : "Try adjusting the time or trigger filters.";
        }
    }

    private static void ShowSelectSnapshotPrompt()
    {
        var zh = LocalizationService.IsChinese;
        MessageBox.Show(zh ? "请先选择一个快照。" : "Please select a snapshot first.",
            "unlose", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private int TotalPages => Math.Max(1, (int)Math.Ceiling((double)_filteredSnapshots.Count / _pageSize));

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
        LblTotalCount.Text = zh ? $"共 {_filteredSnapshots.Count} 条" : $"{_filteredSnapshots.Count} total";
    }

    private void UpdateBatchDeleteVisibility()
    {
        if (BtnBatchDelete != null)
            BtnBatchDelete.Visibility = _selectedIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async void TimeFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (Filter24h == null) return;
        _filterDays = Filter24h.IsChecked == true ? 1 :
                      Filter7d.IsChecked == true ? 7 : 0;
        _currentPage = 1;
        _selectedIds.Clear();
        await RefreshAsync();
    }

    private async void TriggerTypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TriggerTypeFilterCombo == null) return;
        _filterTriggerType = (TriggerTypeFilterCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        _currentPage = 1;
        _selectedIds.Clear();
        await RefreshAsync();
    }

    /// <summary>
    /// Entry point for live refresh: called by MainWindow when a snapshot created/failed notification arrives.
    /// A 500ms debounce coalesces consecutive events to avoid an IPC storm; safe to call from any thread.
    /// </summary>
    public void RequestRefresh()
    {
        // Mark a refresh as pending; Interlocked ensures visibility across threads
        Interlocked.Exchange(ref _refreshPending, 1);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(500);
            if (Interlocked.CompareExchange(ref _refreshPending, 0, 1) != 1) return;
            await RefreshAsync();
        });
    }

    public async Task RefreshAsync()
    {
        var zh = LocalizationService.IsChinese;
        var resp = await ServiceClient.SendAsync("LIST_SNAPSHOTS");
        if (!resp.Success)
        {
            MessageBox.Show(
                zh ? $"获取快照列表失败：{resp.ErrorMessage}" : $"Failed to load snapshots: {resp.ErrorMessage}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            _allSnapshots = JsonSerializer.Deserialize<List<SnapshotRecord>>(resp.Data ?? "[]")
                         ?? new List<SnapshotRecord>();

            ApplyFilterAndRender();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                zh ? $"解析快照数据失败：{ex.Message}" : $"Failed to parse snapshot data: {ex.Message}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyFilterAndRender()
    {
        IEnumerable<SnapshotRecord> filtered = _allSnapshots;

        if (_filterDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_filterDays);
            filtered = filtered.Where(s => s.CreatedAt >= cutoff);
        }

        if (!string.IsNullOrEmpty(_filterTriggerType) &&
            Enum.TryParse<TriggerType>(_filterTriggerType, out var tt))
        {
            filtered = filtered.Where(s => s.TriggerType == tt);
        }

        _filteredSnapshots = filtered.OrderByDescending(s => s.CreatedAt).ToList();
        _currentPage = Math.Min(_currentPage, Math.Max(1, TotalPages));
        RenderPage();
    }

    private void RenderPage()
    {
        var page = _filteredSnapshots
            .Skip((_currentPage - 1) * _pageSize)
            .Take(_pageSize)
            .ToList();

        SnapshotList.ItemsSource = page;
        if (BtnFirstPage != null)
        {
            BtnFirstPage.IsEnabled = BtnPrevPage.IsEnabled = _currentPage > 1;
            BtnNextPage.IsEnabled = BtnLastPage.IsEnabled = _currentPage < TotalPages;
        }
        UpdatePageInfoText();
        UpdateTotalCountText();
        UpdateEmptyState();
        UpdateBatchDeleteVisibility();
    }

    private void PageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeComboBox?.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out var size))
        {
            _pageSize = size;
            _currentPage = 1;
            RenderPage();
        }
    }

    private void BtnFirstPage_Click(object sender, RoutedEventArgs e) { _currentPage = 1; RenderPage(); }
    private void BtnPrevPage_Click(object sender, RoutedEventArgs e) { if (_currentPage > 1) { _currentPage--; RenderPage(); } }
    private void BtnNextPage_Click(object sender, RoutedEventArgs e) { if (_currentPage < TotalPages) { _currentPage++; RenderPage(); } }
    private void BtnLastPage_Click(object sender, RoutedEventArgs e) { _currentPage = TotalPages; RenderPage(); }

    // ── Batch selection ──

    private void BatchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not SnapshotRecord rec) return;

        if (cb.IsChecked == true)
        {
            _selectedIds.Add(rec.Id);
            // Checking a batch box also selects the row: this makes the detail drawer and row-level actions
            // (immersive restore / full restore / delete) immediately available, avoiding the confusing
            // "checked ✓ but still prompted to select a snapshot first" double-selection concept
            SnapshotList.SelectedItem = rec;
        }
        else
        {
            _selectedIds.Remove(rec.Id);
        }

        UpdateBatchDeleteVisibility();
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (SelectAllCheckBox == null) return;
        var page = SnapshotList.ItemsSource as List<SnapshotRecord>;
        if (page == null) return;

        if (SelectAllCheckBox.IsChecked == true)
        {
            foreach (var s in page)
                _selectedIds.Add(s.Id);
        }
        else
        {
            foreach (var s in page)
                _selectedIds.Remove(s.Id);
        }

        // Rebind to refresh the CheckBox states
        SnapshotList.ItemsSource = null;
        SnapshotList.ItemsSource = page;
        UpdateBatchDeleteVisibility();
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIds.Count == 0) return;

        var zh = LocalizationService.IsChinese;
        var count = _selectedIds.Count;
        var confirm = MessageBox.Show(
            zh ? $"确认批量删除 {count} 个快照？此操作不可撤销！" : $"Confirm batch delete of {count} snapshots? This cannot be undone!",
            zh ? "确认批量删除" : "Confirm Batch Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var idsToDelete = _selectedIds.ToList();
        _selectedIds.Clear();
        int failed = 0;

        foreach (var id in idsToDelete)
        {
            var resp = await ServiceClient.SendAsync("DELETE_SNAPSHOT",
                new Dictionary<string, string> { ["id"] = id.ToString() });
            if (!resp.Success) failed++;
        }

        if (failed > 0)
            MessageBox.Show(zh
                ? $"批量删除完成，{failed} 个失败。"
                : $"Batch delete completed, {failed} failed.",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Warning);

        await RefreshAsync();
    }

    // ── Single-item actions ──

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotRecord selected)
        {
            ShowSelectSnapshotPrompt();
            return;
        }

        var zh = LocalizationService.IsChinese;
        // Type-to-confirm gate (GitHub-delete-repo style): the user must type the target volume
        // letter (e.g. "D:"), acknowledging WHICH volume is rolled back without IME friction.
        // The service additionally creates the PreRestore safety snapshot itself and refuses
        // system volumes, so no UI-side pre-snapshot is needed here.
        var token = VolumeSafety.VolumeToken(selected.VolumePath);
        var dlg = new InputDialog(
            zh ? "确认整卷原位还原" : "Confirm Full-Volume In-Place Restore",
            zh
                ? $"将把 {selected.VolumePath} 整卷回滚到该快照时点：{selected.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n\n此操作不可撤销：卷上现有文件将被快照版本替换，快照时点之后新建的文件将被删除。\n执行前服务会自动创建一个 PreRestore 保护快照，可用于事后回退。\n\n如确认执行，请输入卷号：{token}"
                : $"This rolls back the ENTIRE volume {selected.VolumePath} to the snapshot taken at {selected.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}.\n\nThis cannot be undone: existing files will be overwritten by the snapshot versions, and files created after the snapshot will be deleted.\nA PreRestore safety snapshot is created automatically first.\n\nTo proceed, type the volume letter: {token}",
            string.Empty)
        { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (!dlg.Confirmed) return;
        if (!VolumeSafety.TokenMatches(dlg.InputText, selected.VolumePath))
        {
            MessageBox.Show(zh ? "输入的卷号不匹配，已取消操作。" : "Volume letter did not match; operation aborted.",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var resp = await ServiceClient.SendAsync("RESTORE_SNAPSHOT",
            new Dictionary<string, string> { ["id"] = selected.Id.ToString() });

        if (resp.Success)
            MessageBox.Show(zh ? "还原请求已发送，请等待服务完成还原。" : "Restore request sent. Please wait for completion.",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(zh ? $"还原失败：{resp.ErrorMessage}" : $"Restore failed: {resp.ErrorMessage}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);

        _ = RefreshAsync();
    }

    private void SnapshotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotRecord s)
        {
            ResetDrawer();
            return;
        }
        var zh = LocalizationService.IsChinese;
        DrawerTimestamp.Text = s.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        DrawerSnapshotId.Text = $"Object ID: {s.Id.ToString()[..8].ToUpper()}";
        DrawerTrigger.Text = GetTriggerDisplayText(s, zh);
        DrawerContextMeta.Text = s.TriggerDetail ?? s.Label ?? "—";
        DrawerVolumes.Text = s.Volumes.Length > 0 ? string.Join(", ", s.Volumes) : "—";
        DrawerSessionId.Text = string.IsNullOrWhiteSpace(s.SessionId) ? "—" : s.SessionId;
        PinnedCheckBox.IsChecked = s.IsPinned;
        UpdateFullRestoreEnabled(s);
    }

    private void ImmersiveRestore_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotRecord selected)
        {
            ShowSelectSnapshotPrompt();
            return;
        }
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.MainFrame.Navigate(new ImmersiveRestorePage(selected.Id.ToString()));
    }

    private async void PinnedCheckBox_Checked(object sender, RoutedEventArgs e)
        => await SetPinnedAsync(true);

    private async void PinnedCheckBox_Unchecked(object sender, RoutedEventArgs e)
        => await SetPinnedAsync(false);

    private async Task SetPinnedAsync(bool pinned)
    {
        if (SnapshotList.SelectedItem is not SnapshotRecord selected) return;

        var resp = await ServiceClient.SendAsync("PIN_SNAPSHOT",
            new Dictionary<string, string>
            {
                ["id"] = selected.Id.ToString(),
                ["pinned"] = pinned.ToString().ToLower()
            });

        if (!resp.Success)
        {
            var zh = LocalizationService.IsChinese;
            MessageBox.Show(
                zh ? $"操作失败：{resp.ErrorMessage}" : $"Operation failed: {resp.ErrorMessage}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
            PinnedCheckBox.IsChecked = !pinned;
        }
        else
        {
            selected.IsPinned = pinned;
        }
    }

    private async void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotRecord selected)
        {
            ShowSelectSnapshotPrompt();
            return;
        }
        var zh = LocalizationService.IsChinese;
        var shortId = selected.Id.ToString("N")[..8].ToUpper();
        var confirm = MessageBox.Show(
            zh ? $"确认删除快照 {shortId}？此操作不可撤销！" : $"Delete snapshot {shortId}? This cannot be undone!",
            zh ? "确认删除" : "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var resp = await ServiceClient.SendAsync("DELETE_SNAPSHOT",
            new Dictionary<string, string> { ["id"] = selected.Id.ToString() });
        if (resp.Success)
            await RefreshAsync();
        else
            MessageBox.Show(
                zh ? $"删除失败：{resp.ErrorMessage}" : $"Delete failed: {resp.ErrorMessage}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
