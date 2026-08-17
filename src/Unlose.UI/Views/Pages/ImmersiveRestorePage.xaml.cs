using Unlose.Core.Models;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Unlose.UI.Views.Pages;

public partial class ImmersiveRestorePage : Page, ILocalizable
{
    private List<SnapshotRecord> _timelineSnapshots = [];
    private int _currentTimelineIndex;
    private string? _initialSnapshotId;

    // Empty-state/error text (both languages, re-shown on language switch)
    private string? _emptyStateZh;
    private string? _emptyStateEn;
    // Diff placeholder/fallback text (both languages)
    private string? _diffPlaceholderZh;
    private string? _diffPlaceholderEn;
    // Last successfully shown diff result (stats line re-shown on language switch)
    private string? _lastDiffFileName;
    private int _lastDiffAdds;
    private int _lastDiffDels;
    private bool _diffResultShown;

    private const int MaxCompareLines = 2000;    // max lines per side included in the comparison
    private const int MaxDiffOutputLines = 500;  // max diff output lines

    public ImmersiveRestorePage() : this(null) { }

    public ImmersiveRestorePage(string? snapshotId)
    {
        InitializeComponent();
        ApplyLanguage();
        _initialSnapshotId = snapshotId;
        Loaded += async (_, _) => await LoadTimelineAsync();
        // In-place full-volume restore is gated behind a settings switch (default off): hide the
        // button unless the user enabled it (the service enforces the same gate).
        Loaded += async (_, _) =>
        {
            try
            {
                var cfg = await ServiceClient.LoadConfigAsync();
                BtnForceRestore.Visibility = cfg.Snapshot.EnableInPlaceVolumeRestore ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { BtnForceRestore.Visibility = Visibility.Collapsed; }
        };
        // Lazy loading: read the next level on demand when a directory node expands (only two levels load initially; deep files must stay reachable)
        LeftTreeView.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnLeftNodeExpanded));
        RightTreeView.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnRightNodeExpanded));
        // Dual-tree sync: also sync on collapse (only when paths match)
        LeftTreeView.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(OnLeftNodeCollapsed));
        RightTreeView.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(OnRightNodeCollapsed));
        Loaded += (_, _) => HookScrollSync();
    }

    public void ApplyLanguage()
    {
        if (LblTitle == null) return;
        var zh = LocalizationService.IsChinese;
        LblTitle.Text = zh ? "沉浸式对比还原" : "Immersive Compare & Restore";
        BtnBack.Content = zh ? "← 返回" : "← Back";
        BtnRestoreToDir.Content = zh ? "🗂️ 整卷内容恢复到新目录" : "🗂️ Restore Entire Volume to Directory";
        BtnRestoreSelected.Content = zh ? "✅ 恢复选中项到新目录" : "✅ Restore Selected to Directory";
        BtnForceRestore.Content = zh ? "⚠️ 原位覆盖还原（高风险）" : "⚠️ In-Place Overwrite Restore (High Risk)";
        BtnEmptyStateBack.Content = zh ? "← 返回" : "← Back";

        // Timeline and tree headers: empty state > mounted > initial loading
        if (EmptyStatePanel.Visibility == Visibility.Visible)
        {
            LblTimelineLabel.Text = zh ? "时间轴不可用" : "Timeline unavailable";
            if (_emptyStateZh is not null)
                EmptyStateText.Text = zh ? _emptyStateZh : (_emptyStateEn ?? _emptyStateZh);
        }
        else if (_timelineSnapshots.Count > 0 && _currentTimelineIndex < _timelineSnapshots.Count)
        {
            var snap = _timelineSnapshots[_currentTimelineIndex];
            var chip = Converters.UiText.TriggerChip(snap.TriggerType, zh);
            LblTimelineLabel.Text = zh
                ? $"[ 当前挂载: {snap.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({chip}) ]"
                : $"[ Mounted: {snap.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({chip}) ]";
            LblLeftHeader.Text = $"🕒 {snap.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {chip}";
            LblRightHeader.Text = zh
                ? $"📂 当前状态 ({DateTime.Now:yyyy-MM-dd HH:mm})"
                : $"📂 Current ({DateTime.Now:yyyy-MM-dd HH:mm})";
        }
        else
        {
            LblTimelineLabel.Text = zh ? "← 更早  [ 加载中... ]  更晚 →" : "← Older  [ Loading... ]  Newer →";
            LblLeftHeader.Text = zh ? "🕒 历史快照" : "🕒 History Snapshot";
            LblRightHeader.Text = zh ? "📂 当前状态" : "📂 Current State";
        }

        // Diff panel: placeholder/fallback text and stats line
        if (DiffPreviewText.Visibility == Visibility.Visible)
            DiffPreviewText.Text = zh
                ? (_diffPlaceholderZh ?? "点击左侧历史快照中的文件以预览差异。")
                : (_diffPlaceholderEn ?? "Select a file in the left history tree to preview its diff.");
        if (_diffResultShown && _lastDiffFileName is not null)
        {
            LblDiffHeader.Text = (zh ? "</> 文件差异预览 — " : "</> File Diff Preview — ") + _lastDiffFileName;
            DiffStatsText.Text = zh
                ? $"本文件差异：+{_lastDiffAdds} 行 / -{_lastDiffDels} 行"
                : $"Diff: +{_lastDiffAdds} / -{_lastDiffDels} lines";
        }
        else
        {
            LblDiffHeader.Text = zh ? "</> 文件差异预览" : "</> File Diff Preview";
        }

        UpdateDiffSummary();
        UpdateLeftTreeCheckBoxToolTips();
        RefreshDiffSuffixes();
    }

    // ── Load failure / empty state ─────────────────────────────────

    private void ShowEmptyState(string zhMessage, string enMessage)
    {
        _emptyStateZh = zhMessage;
        _emptyStateEn = enMessage;
        EmptyStateText.Text = LocalizationService.IsChinese ? zhMessage : enMessage;
        EmptyStatePanel.Visibility = Visibility.Visible;
        LblTimelineLabel.Text = LocalizationService.IsChinese ? "时间轴不可用" : "Timeline unavailable";
    }

    private void HideEmptyState() => EmptyStatePanel.Visibility = Visibility.Collapsed;

    // ── Timeline loading ───────────────────────────────────────────

    private async Task LoadTimelineAsync()
    {
        try
        {
            var resp = await ServiceClient.SendAsync("LIST_SNAPSHOTS");
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Data))
            {
                ShowEmptyState(
                    "无法获取快照列表" + (string.IsNullOrWhiteSpace(resp.ErrorMessage) ? "：服务未响应" : $"：{resp.ErrorMessage}"),
                    "Failed to list snapshots" + (string.IsNullOrWhiteSpace(resp.ErrorMessage) ? ": no response from service" : $": {resp.ErrorMessage}"));
                return;
            }

            _timelineSnapshots = JsonSerializer.Deserialize<List<SnapshotRecord>>(resp.Data) ?? [];
            _timelineSnapshots = _timelineSnapshots
                .OrderByDescending(s => s.CreatedAt)
                .ToList();

            if (_timelineSnapshots.Count == 0)
            {
                ShowEmptyState(
                    "暂无快照。请先在快照管理页创建快照，再回到本页进行对比还原。",
                    "No snapshots yet. Create a snapshot in Snapshot Library first, then return here.");
                return;
            }

            TimelineSlider.Minimum = 0;
            TimelineSlider.Maximum = _timelineSnapshots.Count - 1;
            TimelineSlider.SmallChange = 1;
            TimelineSlider.LargeChange = 1;
            TimelineSlider.TickFrequency = 1;

            // If a specific snapshot ID was passed, jump to the matching entry
            if (_initialSnapshotId != null)
            {
                var idx = _timelineSnapshots.FindIndex(s => s.Id.ToString() == _initialSnapshotId);
                if (idx >= 0)
                {
                    // Set _currentTimelineIndex BEFORE assigning the slider value, so Timeline_ValueChanged
                    // sees index == current and skips — otherwise the event handler and the direct call below
                    // both load the view, firing two concurrent MOUNT_SNAPSHOT requests (mklink race).
                    _currentTimelineIndex = idx;
                    TimelineSlider.Value = idx;
                    await LoadSnapshotViewAsync(idx);
                    return;
                }
            }

            _currentTimelineIndex = 0;
            TimelineSlider.Value = 0;
            await LoadSnapshotViewAsync(0);
        }
        catch (Exception ex)
        {
            ShowEmptyState("加载快照时间轴失败：" + ex.Message, "Failed to load snapshot timeline: " + ex.Message);
        }
    }

    // ── Snapshot view switching ────────────────────────────────────

    private async void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TimelineSlider == null || _timelineSnapshots.Count == 0) return;
        var index = (int)e.NewValue;
        if (index != _currentTimelineIndex)
            await LoadSnapshotViewAsync(index);
    }

    private async Task LoadSnapshotViewAsync(int index)
    {
        if (index < 0 || index >= _timelineSnapshots.Count) return;
        _currentTimelineIndex = index;
        var snap = _timelineSnapshots[index];
        var zh = LocalizationService.IsChinese;

        var chip = Converters.UiText.TriggerChip(snap.TriggerType, zh);
        LblTimelineLabel.Text = zh
            ? $"[ 当前挂载: {snap.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({chip}) ]"
            : $"[ Mounted: {snap.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({chip}) ]";
        LblLeftHeader.Text = $"🕒 {snap.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {chip}";
        LblRightHeader.Text = zh
            ? $"📂 当前状态 ({DateTime.Now:yyyy-MM-dd HH:mm})"
            : $"📂 Current ({DateTime.Now:yyyy-MM-dd HH:mm})";

        // Gray out in-place overwrite when the mounted snapshot targets the system volume
        // (the service refuses it; disabling here explains why upfront).
        UpdateForceRestoreGate(snap);

        await LoadFileTreesAsync(snap);
    }

    private void UpdateForceRestoreGate(SnapshotRecord snap)
    {
        if (BtnForceRestore == null) return;
        var sysVol = VolumeSafety.IsSystemVolume(snap.VolumePath);
        BtnForceRestore.IsEnabled = !sysVol;
        ToolTipService.SetShowOnDisabled(BtnForceRestore, true);
        BtnForceRestore.ToolTip = sysVol
            ? (LocalizationService.IsChinese
                ? "原位覆盖还原不支持系统卷，请使用 Windows 系统还原。"
                : "In-place overwrite restore is not supported on the system volume. Use Windows System Restore instead.")
            : null;
    }

    // ── File tree loading ──────────────────────────────────────────

    private string? _currentMountRoot;
    private string? _currentVolume;

    private async Task LoadFileTreesAsync(SnapshotRecord snapshot)
    {
        var zh = LocalizationService.IsChinese;
        HideEmptyState();
        // Reset the diff preview to placeholder after switching snapshots (a stale diff must not linger)
        _diffResultShown = false;
        LblDiffHeader.Text = zh ? "</> 文件差异预览" : "</> File Diff Preview";
        ShowDiffPlaceholder(
            "点击左侧历史快照中的文件以预览差异。",
            "Select a file in the left history tree to preview its diff.");
        try
        {
            var mountResp = await ServiceClient.SendAsync("MOUNT_SNAPSHOT",
                new Dictionary<string, string> { ["snapshotId"] = snapshot.Id.ToString() });
            if (!mountResp.Success)
            {
                // No more silent empty tree: show a clear error + back button (previously users saw a blank page when the service lacked this command)
                var err = mountResp.ErrorMessage;
                if (err?.Contains("shadow copy no longer exists") == true)
                {
                    // The DB record outlived its VSS shadow: Windows evicts old shadow copies when shadow
                    // storage fills up, so very old snapshots can become unrestorable. Say so plainly.
                    ShowEmptyState(
                        "该快照的底层卷影副本已被 Windows 自动清理（卷影存储空间有限，旧副本会被淘汰），无法再挂载还原。",
                        "This snapshot's underlying shadow copy was reclaimed by Windows (shadow storage is limited and old copies are evicted), so it can no longer be mounted or restored.");
                    return;
                }
                ShowEmptyState(
                    "挂载快照失败：" + (string.IsNullOrWhiteSpace(err) ? "服务未响应" : err),
                    "Failed to mount snapshot: " + (string.IsNullOrWhiteSpace(err) ? "no response from service" : err));
                return;
            }

            var mountData = JsonSerializer.Deserialize<JsonElement>(mountResp.Data);
            var rootPath = mountData.TryGetProperty("rootPath", out var rp) ? rp.GetString() : null;
            if (rootPath == null)
            {
                ShowEmptyState("挂载响应缺少 rootPath", "Mount response missing rootPath");
                return;
            }

            _currentMountRoot = rootPath.TrimEnd('\\');
            _currentVolume = snapshot.Volumes.FirstOrDefault();

            // Load only the first two directory levels to bound the data size; deeper levels load on node expansion
            var historicalDirs = await BuildFileTreeAsync(rootPath, maxDepth: 2);

            // Resolve the corresponding current system directory
            var currentPath = GetCorrespondingCurrentPath(rootPath, _currentVolume);
            var currentDirs = Directory.Exists(currentPath)
                ? await BuildFileTreeAsync(currentPath, maxDepth: 2)
                : new List<DiffTreeItem>();

            // Highlight differences
            HighlightDifferences(historicalDirs, currentDirs, zh);

            LeftTreeView.ItemsSource = historicalDirs;
            RightTreeView.ItemsSource = currentDirs;
            UpdateDiffSummary();

            if (historicalDirs.Count == 0)
            {
                RestoreStatusText.Text = zh
                    ? "⚠️ 快照内容为空或不可访问（卷影副本可能已被保留策略清理）"
                    : "⚠️ Snapshot empty or inaccessible (shadow copy may have been purged)";
            }
        }
        catch (Exception ex)
        {
            ShowEmptyState("加载文件树失败：" + ex.Message, "Failed to load file tree: " + ex.Message);
        }
    }

    private static string GetCorrespondingCurrentPath(string mountPath, string? volume)
    {
        // The mount path is a symlink (%ProgramData%\unlose\mounts\...) representing the volume root as of snapshot time;
        // the current-state tree maps directly back to the real volume path (the old logic checked for a "ShadowCopy" substring, which does not hold for symlinks)
        if (!string.IsNullOrWhiteSpace(volume))
            return volume;
        return mountPath;
    }

    private static async Task<List<DiffTreeItem>> BuildFileTreeAsync(string root, int maxDepth, int currentDepth = 0)
    {
        var items = new List<DiffTreeItem>();
        if (currentDepth >= maxDepth || !Directory.Exists(root)) return items;

        try
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                var item = new DiffTreeItem
                {
                    Name = name,
                    Icon = "📁",
                    FullPath = dir,
                    ModifiedText = FormatModified(SafeLastWriteTime(dir, isDir: true)),
                    Children = new ObservableCollection<DiffTreeItem>(
                        await BuildFileTreeAsync(dir, maxDepth, currentDepth + 1))
                };
                // At the depth boundary with a non-empty directory: mark lazy (real children load on expansion)
                if (currentDepth + 1 >= maxDepth && DirectoryHasContent(dir))
                    item.MarkLazy();
                items.Add(item);
            }

            foreach (var file in Directory.GetFiles(root).Take(50)) // cap file count for performance
            {
                items.Add(new DiffTreeItem
                {
                    Name = Path.GetFileName(file),
                    Icon = "📄",
                    FullPath = file,
                    ModifiedText = FormatModified(SafeLastWriteTime(file, isDir: false))
                });
            }
        }
        catch { /* skip on access errors */ }

        return items;
    }

    private static bool DirectoryHasContent(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    // ── Lazy loading: read the next level on node expansion ─────────

    private async void OnLeftNodeExpanded(object sender, RoutedEventArgs e)
        => await ExpandLazyNodeAsync(e, isHistorical: true);

    private async void OnRightNodeExpanded(object sender, RoutedEventArgs e)
        => await ExpandLazyNodeAsync(e, isHistorical: false);

    private async Task ExpandLazyNodeAsync(RoutedEventArgs e, bool isHistorical)
    {
        if ((e.OriginalSource as TreeViewItem)?.DataContext is not DiffTreeItem item)
            return;
        await LoadLazyChildrenAsync(item, isHistorical); // returns immediately for non-lazy nodes

        // After expanding, drive the opposite tree to sync (only when paths match)
        if (!_syncingExpansion)
            _ = SyncExpansionAsync(item, isHistoricalSource: isHistorical, expand: true);
    }

    private void OnLeftNodeCollapsed(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as TreeViewItem)?.DataContext is DiffTreeItem item && !_syncingExpansion)
            _ = SyncExpansionAsync(item, isHistoricalSource: true, expand: false);
    }

    private void OnRightNodeCollapsed(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as TreeViewItem)?.DataContext is DiffTreeItem item && !_syncingExpansion)
            _ = SyncExpansionAsync(item, isHistoricalSource: false, expand: false);
    }

    private async Task LoadLazyChildrenAsync(DiffTreeItem item, bool isHistoricalNode)
    {
        if (!item.IsLazy) return;
        item.IsLazy = false;
        item.Children.Clear();

        var children = await Task.Run(() => BuildChildrenLevel(item.FullPath!));
        // Compute diff annotations for lazy children on the fly (same coloring rules as the initial HighlightDifferences)
        foreach (var child in children)
        {
            ComputeDiffStatus(child, isHistoricalNode);
            item.Children.Add(child);
        }
        UpdateDiffSummary();
    }

    // ── Dual-tree sync: expand/collapse/scroll (sync only when paths match; otherwise each tree goes its own way) ────

    private bool _syncingExpansion;
    private bool _syncingScroll;
    private ScrollViewer? _leftScroller;
    private ScrollViewer? _rightScroller;

    /// <summary>Walk the opposite tree's data chain segment by segment along the relative path, applying the same expanded/collapsed state.</summary>
    private async Task SyncExpansionAsync(DiffTreeItem sourceItem, bool isHistoricalSource, bool expand)
    {
        if (_syncingExpansion || _currentMountRoot is null || _currentVolume is null || sourceItem.FullPath is null)
            return;

        // Relative path segments of the source node (left tree base = mount root, right tree base = current volume)
        var basePath = isHistoricalSource ? _currentMountRoot : _currentVolume;
        var rel = System.IO.Path.GetRelativePath(basePath, sourceItem.FullPath);
        if (rel.StartsWith("..")) return;
        var segments = rel.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        var otherRoots = (isHistoricalSource ? RightTreeView.ItemsSource : LeftTreeView.ItemsSource)
            as System.Collections.IEnumerable;
        if (otherRoots is null) return;

        _syncingExpansion = true;
        try
        {
            var level = otherRoots.Cast<DiffTreeItem>();
            foreach (var seg in segments)
            {
                var node = level.FirstOrDefault(n => string.Equals(n.Name, seg, StringComparison.OrdinalIgnoreCase));
                if (node is null) return; // no match → no sync

                if (node.IsLazy)
                    await LoadLazyChildrenAsync(node, isHistoricalNode: !isHistoricalSource);

                // Drive UI expand/collapse via the ItemContainerStyle TwoWay binding
                if (node.IsExpanded != expand)
                    node.IsExpanded = expand;

                level = node.Children;
            }
        }
        finally { _syncingExpansion = false; }
    }

    private void HookScrollSync()
    {
        _leftScroller = FindDescendantScrollViewer(LeftTreeView);
        _rightScroller = FindDescendantScrollViewer(RightTreeView);
        if (_leftScroller is not null)
            _leftScroller.ScrollChanged += (_, _) => SyncScroll(_leftScroller, _rightScroller);
        if (_rightScroller is not null)
            _rightScroller.ScrollChanged += (_, _) => SyncScroll(_rightScroller, _leftScroller);
    }

    /// <summary>Sync by scroll ratio (keeps the trees roughly aligned even when their content heights differ).</summary>
    private void SyncScroll(ScrollViewer from, ScrollViewer? to)
    {
        if (_syncingScroll || to is null) return;
        _syncingScroll = true;
        try
        {
            var ratio = from.ScrollableHeight > 0 ? from.VerticalOffset / from.ScrollableHeight : 0;
            to.ScrollToVerticalOffset(ratio * to.ScrollableHeight);
        }
        finally { _syncingScroll = false; }
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindDescendantScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static List<DiffTreeItem> BuildChildrenLevel(string dir)
    {
        var items = new List<DiffTreeItem>();
        try
        {
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var child = new DiffTreeItem
                {
                    Name = Path.GetFileName(subDir),
                    Icon = "📁",
                    FullPath = subDir,
                    ModifiedText = FormatModified(SafeLastWriteTime(subDir, isDir: true))
                };
                if (DirectoryHasContent(subDir)) child.MarkLazy();
                items.Add(child);
            }
            foreach (var file in Directory.GetFiles(dir).Take(50))
                items.Add(new DiffTreeItem
                {
                    Name = Path.GetFileName(file),
                    Icon = "📄",
                    FullPath = file,
                    ModifiedText = FormatModified(SafeLastWriteTime(file, isDir: false))
                });
        }
        catch { /* skip on access errors */ }
        return items;
    }

    private static DateTime SafeLastWriteTime(string path, bool isDir)
    {
        try { return isDir ? new DirectoryInfo(path).LastWriteTime : new FileInfo(path).LastWriteTime; }
        catch { return DateTime.MinValue; }
    }

    private static string FormatModified(DateTime t)
        => t == DateTime.MinValue ? string.Empty : t.ToString("yyyy-MM-dd HH:mm");

    /// <summary>Compute the diff annotation for a lazily loaded node on the fly.</summary>
    private void ComputeDiffStatus(DiffTreeItem child, bool isHistoricalNode)
    {
        if (_currentMountRoot is null || _currentVolume is null || child.FullPath is null) return;

        try
        {
            if (isHistoricalNode)
            {
                // Left tree (history): map to the current system path for comparison
                var rel = Path.GetRelativePath(_currentMountRoot, child.FullPath);
                var currentPath = Path.Combine(_currentVolume, rel);
                if (!PathExists(currentPath))
                {
                    child.Status = DiffStatus.Deleted;
                    child.Color = "#EF4444";
                    child.Suffix = DeletedSuffix();
                }
                else if (child.Icon == "📄" && File.Exists(currentPath))
                {
                    var histInfo = new FileInfo(child.FullPath);
                    var currInfo = new FileInfo(currentPath);
                    if (histInfo.Length != currInfo.Length || histInfo.LastWriteTime != currInfo.LastWriteTime)
                    {
                        child.Status = DiffStatus.Modified;
                        child.Color = "#F97316";
                    }
                }
            }
            else
            {
                // Right tree (current): missing from the snapshot → added
                var rel = Path.GetRelativePath(_currentVolume, child.FullPath);
                var histPath = Path.Combine(_currentMountRoot, rel);
                if (!PathExists(histPath))
                {
                    child.Status = DiffStatus.Added;
                    child.Color = "#22C55E";
                    child.Suffix = AddedSuffix();
                }
            }
        }
        catch { /* a single failed comparison does not block browsing */ }
    }

    private static string DeletedSuffix() => LocalizationService.IsChinese ? "（已删除）" : " (deleted)";
    private static string AddedSuffix() => LocalizationService.IsChinese ? "（新文件）" : " (new)";

    private static bool PathExists(string path)
        => File.Exists(path) || Directory.Exists(path);

    private static void HighlightDifferences(IList<DiffTreeItem> historical, IList<DiffTreeItem> current, bool zh)
    {
        var currentNames = new HashSet<string>(current.Select(c => c.Name!));

        foreach (var item in historical)
        {
            if (!currentNames.Contains(item.Name!))
            {
                item.Status = DiffStatus.Deleted;
                item.Color = "#EF4444";
                item.Suffix = zh ? "（已删除）" : " (deleted)";
            }
            else
            {
                var match = current.First(c => c.Name == item.Name);
                if (item.Icon == "📄" && match.Icon == "📄")
                {
                    try
                    {
                        var histInfo = new FileInfo(item.FullPath!);
                        var currInfo = new FileInfo(match.FullPath!);
                        if (histInfo.Length != currInfo.Length || histInfo.LastWriteTime != currInfo.LastWriteTime)
                        {
                            item.Status = DiffStatus.Modified;
                            item.Color = "#F97316";
                        }
                    }
                    catch { }
                }
                HighlightDifferences(item.Children, match.Children, zh);
            }
        }

        foreach (var item in current)
        {
            var histNames = new HashSet<string>(historical.Select(h => h.Name!));
            if (!histNames.Contains(item.Name!))
            {
                item.Status = DiffStatus.Added;
                item.Color = "#22C55E";
                item.Suffix = zh ? "（新文件）" : " (new)";
            }
        }
    }

    /// <summary>Aggregate the computed diff states of both trees and update the page-level stats chip (covers loaded levels only; recomputed after lazy expansion).</summary>
    private void UpdateDiffSummary()
    {
        if (LeftTreeView.ItemsSource is null && RightTreeView.ItemsSource is null)
        {
            DiffSummaryChip.Visibility = Visibility.Collapsed;
            return;
        }

        var modified = 0;
        var deleted = 0;
        var added = 0;
        void Walk(IEnumerable<DiffTreeItem> items)
        {
            foreach (var it in items)
            {
                switch (it.Status)
                {
                    case DiffStatus.Modified: modified++; break;
                    case DiffStatus.Deleted: deleted++; break;
                    case DiffStatus.Added: added++; break;
                }
                Walk(it.Children);
            }
        }
        if (LeftTreeView.ItemsSource is System.Collections.IEnumerable left)
            Walk(left.Cast<DiffTreeItem>());
        if (RightTreeView.ItemsSource is System.Collections.IEnumerable right)
            Walk(right.Cast<DiffTreeItem>());

        var zh = LocalizationService.IsChinese;
        DiffSummaryChip.Visibility = Visibility.Visible;
        DiffSummaryText.Text = (modified + deleted + added) == 0
            ? (zh ? "未发现差异" : "No differences found")
            : (zh ? $"发现 {modified} 处修改，{deleted} 处删除，{added} 处新增"
                  : $"{modified} modified, {deleted} deleted, {added} added");
    }

    /// <summary>Rebuild suffix labels from each node's existing Status after a language switch.</summary>
    private void RefreshDiffSuffixes()
    {
        void Walk(IEnumerable<DiffTreeItem> items)
        {
            foreach (var it in items)
            {
                it.Suffix = it.Status switch
                {
                    DiffStatus.Deleted => DeletedSuffix(),
                    DiffStatus.Added => AddedSuffix(),
                    _ => ""
                };
                Walk(it.Children);
            }
        }
        if (LeftTreeView.ItemsSource is System.Collections.IEnumerable left)
            Walk(left.Cast<DiffTreeItem>());
        if (RightTreeView.ItemsSource is System.Collections.IEnumerable right)
            Walk(right.Cast<DiffTreeItem>());
    }

    // Template elements are not accessible by name: set on Loaded in the current language; ApplyLanguage walks the visual tree to refresh generated containers
    private void LeftItemCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
            cb.ToolTip = LeftCheckBoxToolTip();
    }

    private static string LeftCheckBoxToolTip()
        => LocalizationService.IsChinese ? "勾选后可批量挑捡恢复" : "Check to include in selective restore";

    private void UpdateLeftTreeCheckBoxToolTips()
    {
        var tooltip = LeftCheckBoxToolTip();
        var pending = new Stack<DependencyObject>();
        pending.Push(LeftTreeView);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                if (child is CheckBox cb) cb.ToolTip = tooltip;
                pending.Push(child);
            }
        }
    }

    // ── File selection and line-level diff preview ───────────────

    private async void LeftTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not DiffTreeItem item || item.Icon != "📄") return;
        var zh = LocalizationService.IsChinese;

        // Comparison pair: file on the mounted snapshot side vs the file at the same relative path on the current disk
        var histPath = item.FullPath!;
        string? currPath = null;
        if (_currentMountRoot is not null && _currentVolume is not null)
        {
            var rel = Path.GetRelativePath(_currentMountRoot, histPath);
            if (!rel.StartsWith("..", StringComparison.Ordinal))
                currPath = Path.Combine(_currentVolume, rel);
        }

        try
        {
            var result = await Task.Run(() => BuildFileDiff(histPath, currPath, zh));
            // The user may have selected another file while computing in the background: discard stale results
            if (!ReferenceEquals(LeftTreeView.SelectedItem, item)) return;
            ApplyFileDiffResult(item.Name!, result, zh);
        }
        catch (Exception ex)
        {
            ShowDiffPlaceholder("无法读取文件内容：" + ex.Message, "Failed to read file: " + ex.Message);
        }
    }

    private sealed class FileDiffResult
    {
        public string? PlaceholderZh;              // non-null → show placeholder/fallback
        public string? PlaceholderEn;
        public List<DiffLine>? Lines;
        public int Adds;
        public int Dels;
        public bool HasPlaceholder => PlaceholderZh is not null;
    }

    private static FileDiffResult BuildFileDiff(string histPath, string? currPath, bool zh)
    {
        var histExists = File.Exists(histPath);
        var currExists = currPath is not null && File.Exists(currPath);

        if (!histExists && !currExists)
            return new FileDiffResult { PlaceholderZh = "两侧文件均不存在，无法对比。", PlaceholderEn = "File is missing on both sides." };

        // Skip line-level diff for binary files (detected via NUL byte)
        if ((histExists && IsBinaryFile(histPath)) || (currExists && IsBinaryFile(currPath!)))
            return new FileDiffResult { PlaceholderZh = "二进制文件不可预览。", PlaceholderEn = "Binary file cannot be previewed." };

        // When one side is missing: treat the other side as all deleted/added lines
        var oldLines = histExists ? ReadLinesLimited(histPath, MaxCompareLines) : new List<string>();
        var newLines = currExists ? ReadLinesLimited(currPath!, MaxCompareLines) : new List<string>();

        var lines = ComputeLineDiff(oldLines, newLines, out var adds, out var dels);
        if (adds == 0 && dels == 0)
            return new FileDiffResult { PlaceholderZh = "两侧文件内容一致，无差异。", PlaceholderEn = "Files are identical on both sides." };

        // Output exceeds the line cap: truncate and append a notice line (stats still reflect the full diff)
        if (lines.Count > MaxDiffOutputLines)
        {
            lines = lines.Take(MaxDiffOutputLines).ToList();
            lines.Add(DiffLine.Notice(zh ? "… 差异行过多，已截断" : "… diff output truncated"));
        }
        return new FileDiffResult { Lines = lines, Adds = adds, Dels = dels };
    }

    /// <summary>Simple LCS-based line diff: oldLines = snapshot side, newLines = current side.</summary>
    private static List<DiffLine> ComputeLineDiff(List<string> oldLines, List<string> newLines, out int adds, out int dels)
    {
        var n = oldLines.Count;
        var m = newLines.Count;
        // LCS length table (n,m ≤ 2000, ~16MB, acceptable)
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = oldLines[i] == newLines[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var lines = new List<DiffLine>();
        adds = 0;
        dels = 0;
        int oi = 0, ni = 0;
        while (oi < n && ni < m)
        {
            if (oldLines[oi] == newLines[ni])
            {
                lines.Add(DiffLine.Context(oldLines[oi], ni + 1));
                oi++; ni++;
            }
            else if (dp[oi + 1, ni] >= dp[oi, ni + 1])
            {
                lines.Add(DiffLine.Deleted(oldLines[oi], oi + 1));
                dels++; oi++;
            }
            else
            {
                lines.Add(DiffLine.Added(newLines[ni], ni + 1));
                adds++; ni++;
            }
        }
        while (oi < n) { lines.Add(DiffLine.Deleted(oldLines[oi], oi + 1)); dels++; oi++; }
        while (ni < m) { lines.Add(DiffLine.Added(newLines[ni], ni + 1)); adds++; ni++; }
        return lines;
    }

    private static bool IsBinaryFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[8192];
            var read = fs.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
                if (buffer[i] == 0) return true;
            return false;
        }
        catch { return false; } // read failures are handled by the ReadLines exception path
    }

    private static List<string> ReadLinesLimited(string path, int maxLines)
    {
        // File.ReadLines streams, avoiding a full read of large files
        var lines = new List<string>(Math.Min(maxLines, 1024));
        foreach (var line in File.ReadLines(path))
        {
            lines.Add(line);
            if (lines.Count >= maxLines) break;
        }
        return lines;
    }

    private void ApplyFileDiffResult(string fileName, FileDiffResult result, bool zh)
    {
        _lastDiffFileName = fileName;
        if (result.HasPlaceholder)
        {
            _diffResultShown = false;
            ShowDiffPlaceholder(result.PlaceholderZh!, result.PlaceholderEn ?? result.PlaceholderZh!);
            return;
        }
        _diffResultShown = true;
        _lastDiffAdds = result.Adds;
        _lastDiffDels = result.Dels;
        DiffPreviewText.Visibility = Visibility.Collapsed;
        DiffLineList.ItemsSource = result.Lines;
        LblDiffHeader.Text = (zh ? "</> 文件差异预览 — " : "</> File Diff Preview — ") + fileName;
        DiffStatsText.Text = zh
            ? $"本文件差异：+{result.Adds} 行 / -{result.Dels} 行"
            : $"Diff: +{result.Adds} / -{result.Dels} lines";
    }

    private void ShowDiffPlaceholder(string zhMessage, string enMessage)
    {
        _diffPlaceholderZh = zhMessage;
        _diffPlaceholderEn = enMessage;
        DiffLineList.ItemsSource = null;
        DiffStatsText.Text = "";
        DiffPreviewText.Text = LocalizationService.IsChinese ? zhMessage : enMessage;
        DiffPreviewText.Visibility = Visibility.Visible;
    }

    // ── Restore operations ───────────────────────────────────────

    private async void ConfirmRestore_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;
        var snap = _timelineSnapshots.ElementAtOrDefault(_currentTimelineIndex);
        if (snap == null) return;

        // In-place overwrite is high risk: the user must type the target volume letter (e.g. "D:")
        // — acknowledging WHICH volume is rolled back without IME friction. The service creates the
        // PreRestore safety snapshot itself and refuses system volumes, so no UI-side pre-snapshot
        // is needed here.
        var token = VolumeSafety.VolumeToken(snap.VolumePath);
        var dlg = new InputDialog(
            zh ? "确认原位覆盖还原" : "Confirm In-Place Overwrite",
            zh ? $"将把快照 {snap.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} 的整卷内容覆盖还原到原位置 {snap.VolumePath}。\n\n此操作不可撤销：原位置的现有文件将被替换或清除。\n执行前服务会自动创建一个 PreRestore 保护快照，可用于事后回退。\n\n如确认执行，请输入卷号：{token}"
               : $"This overwrites the ENTIRE volume at its original location {snap.VolumePath} with snapshot {snap.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}.\n\nThis action cannot be undone: existing files will be replaced or purged.\nA PreRestore safety snapshot is created automatically beforehand.\n\nTo proceed, type the volume letter: {token}",
            string.Empty)
        { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (!dlg.Confirmed) return;
        if (!VolumeSafety.TokenMatches(dlg.InputText, snap.VolumePath))
        {
            RestoreStatusText.Text = zh ? "输入的卷号不匹配，已取消操作。" : "Volume letter did not match; aborted.";
            return;
        }

        RestoreStatusText.Text = zh ? "还原中..." : "Restoring...";
        RestoreProgress.Value = 0;
        RestoreProgress.Opacity = 1;

        try
        {
            var resp = await ServiceClient.SendAsync("RESTORE_SNAPSHOT",
                new Dictionary<string, string> { ["snapshotId"] = snap.Id.ToString() });
            RestoreProgress.Value = resp.Success ? 100 : 0;
            RestoreStatusText.Text = resp.Success
                ? (zh ? "✅ 还原成功" : "✅ Restore Complete")
                : (zh ? "❌ 还原失败" : "❌ Restore Failed");
        }
        catch (Exception ex)
        {
            RestoreStatusText.Text = $"❌ {ex.Message}";
        }
    }

    // ── Selective restore: checked files/dirs → restore to a target directory ──

    private async void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;

        if (_currentMountRoot is null)
        {
            RestoreStatusText.Text = zh ? "⚠️ 快照尚未挂载完成，请稍候" : "⚠️ Snapshot not mounted yet";
            return;
        }

        var snap = _timelineSnapshots.ElementAtOrDefault(_currentTimelineIndex);
        if (snap == null) return;

        // Collect checked items (skip children of an already-checked ancestor — the service copies directories as whole trees, avoiding duplicates)
        var selected = new List<DiffTreeItem>();
        if (LeftTreeView.ItemsSource is System.Collections.IEnumerable roots)
            foreach (var root in roots.Cast<DiffTreeItem>())
                CollectSelected(root, ancestorSelected: false, selected);

        if (selected.Count == 0)
        {
            RestoreStatusText.Text = zh ? "⚠️ 请先在左侧历史树勾选文件/目录" : "⚠️ Check items in the left tree first";
            MessageBox.Show(
                zh ? "尚未勾选任何文件或目录。\n\n请先在左侧「历史版本」树中勾选要恢复的项目（勾选目录会包含其全部子项），然后再点击本按钮。"
                   : "Nothing selected.\n\nCheck files or folders in the left (history) tree first, then click this button again.",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog { Title = zh ? "选择恢复目标目录" : "Select Restore Destination" };
        if (dialog.ShowDialog() != true) return;

        var relPaths = selected
            .Select(s => System.IO.Path.GetRelativePath(_currentMountRoot, s.FullPath!))
            .ToList();

        RestoreStatusText.Text = zh ? $"挑捡还原中（{relPaths.Count} 项）..." : $"Restoring {relPaths.Count} item(s)...";

        try
        {
            var resp = await ServiceClient.SendAsync("RESTORE_FILES",
                new Dictionary<string, string>
                {
                    ["snapshotId"] = snap.Id.ToString(),
                    ["paths"] = JsonSerializer.Serialize(relPaths),
                    ["targetPath"] = dialog.FolderName
                });

            if (resp.Success)
            {
                RestoreStatusText.Text = zh ? $"✅ 已恢复 {relPaths.Count} 项到指定目录" : $"✅ Restored {relPaths.Count} item(s)";
                MessageBox.Show(
                    zh ? $"已恢复 {relPaths.Count} 个选中项到：\n{dialog.FolderName}" : $"Restored {relPaths.Count} item(s) to:\n{dialog.FolderName}",
                    "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                RestoreStatusText.Text = (zh ? "❌ 挑捡还原失败: " : "❌ Restore failed: ") + resp.ErrorMessage;
                MessageBox.Show((zh ? "部分或全部项目还原失败：\n" : "Some or all items failed:\n") + resp.ErrorMessage,
                    "unlose", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            RestoreStatusText.Text = $"❌ {ex.Message}";
        }
    }

    private static void CollectSelected(DiffTreeItem item, bool ancestorSelected, List<DiffTreeItem> acc)
    {
        if (item.IsSelected && !ancestorSelected && item.FullPath is not null)
        {
            acc.Add(item);
            return; // a checked directory is recursively copied by the service, so its children need not be collected
        }
        foreach (var child in item.Children)
            CollectSelected(child, ancestorSelected || item.IsSelected, acc);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        NavigationService?.GoBack();
    }

    private async void RestoreToNewDir_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;
        var dialog = new OpenFolderDialog { Title = zh ? "选择恢复目标目录" : "Select Restore Destination" };
        if (dialog.ShowDialog() != true) return;

        // Full-volume restore is heavy (potentially tens of GB); require a second confirmation before running
        var confirm = MessageBox.Show(
            zh ? $"将把该快照的整卷全部内容还原到：\n{dialog.FolderName}\n\n注意：这是整卷复制，数据量可能很大（数十 GB）、耗时较长。\n确定继续吗？"
               : $"This restores the ENTIRE volume content of the snapshot to:\n{dialog.FolderName}\n\nPotentially tens of GB and may take a long time. Continue?",
            "unlose", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var snap = _timelineSnapshots.ElementAtOrDefault(_currentTimelineIndex);
        if (snap == null) return;

        try
        {
            var resp = await ServiceClient.SendAsync("RESTORE_SNAPSHOT",
                new Dictionary<string, string> { ["snapshotId"] = snap.Id.ToString(), ["targetPath"] = dialog.FolderName });
            MessageBox.Show(resp.Success
                ? (zh ? "文件已还原到指定目录。" : "Files restored to selected directory.")
                : (zh ? "还原失败，请查看日志。" : "Restore failed. Check logs."),
                "unlose", MessageBoxButton.OK,
                resp.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{(zh ? "还原出错：" : "Restore error: ")}{ex.Message}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

// ── Diff file tree node model ────────────────────────────────────

public enum DiffStatus { Unchanged, Added, Deleted, Modified }

public class DiffTreeItem : System.ComponentModel.INotifyPropertyChanged
{
    public string? Name { get; set; }
    public string Icon { get; set; } = "📄";
    public string Color { get; set; } = "#E2E8F0";
    public DiffStatus Status { get; set; } = DiffStatus.Unchanged;
    public ObservableCollection<DiffTreeItem> Children { get; set; } = new();
    public string? FullPath { get; set; }

    /// <summary>Lazy-loading flag: when true, Children holds a placeholder and real children load on expansion</summary>
    public bool IsLazy { get; set; }

    /// <summary>Selective-restore checked state (bound to the left tree checkbox)</summary>
    public bool IsSelected { get; set; }

    /// <summary>Last-modified display text (yyyy-MM-dd HH:mm) for side-by-side comparison</summary>
    public string? ModifiedText { get; set; }

    private string _suffix = "";

    /// <summary>Status suffix label (e.g. " (deleted)"/" (new)"), generated in the language active at build/switch time</summary>
    public string Suffix
    {
        get => _suffix;
        set
        {
            if (_suffix == value) return;
            _suffix = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Suffix)));
        }
    }

    private bool _isExpanded;

    /// <summary>Expanded state (bound TwoWay via ItemContainerStyle; dual-tree sync is code-driven)</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Mark as a lazy node (inserts a placeholder child so the TreeView shows an expand arrow)</summary>
    public void MarkLazy()
    {
        IsLazy = true;
        Children.Clear();
        Children.Add(new DiffTreeItem { Name = "…", Icon = "⏳" });
    }
}

// ── Line-level diff row model (used only by ImmersiveRestorePage) ──

public class DiffLine
{
    public string Number { get; set; } = "";
    public string Prefix { get; set; } = " ";
    public string Text { get; set; } = "";
    public string Background { get; set; } = "Transparent";
    public string Foreground { get; set; } = "#E2E8F0";

    private static string Num(int n) => n.ToString().PadLeft(4);

    public static DiffLine Context(string text, int newLineNo) => new()
        { Number = Num(newLineNo), Prefix = " ", Text = text };

    public static DiffLine Added(string text, int newLineNo) => new()
        { Number = Num(newLineNo), Prefix = "+", Text = text, Background = "#1A22C55E", Foreground = "#86EFAC" };

    public static DiffLine Deleted(string text, int oldLineNo) => new()
        { Number = Num(oldLineNo), Prefix = "-", Text = text, Background = "#1AEF4444", Foreground = "#FCA5A5" };

    public static DiffLine Notice(string text) => new()
        { Number = "", Prefix = "", Text = text, Foreground = "#64748B" };
}
