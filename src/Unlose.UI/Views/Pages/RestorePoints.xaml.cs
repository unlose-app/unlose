using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace Unlose.UI.Views.Pages;

/// <summary>View model for RestorePoints list binding</summary>
internal sealed class RestorePointViewModel
{
    public int SequenceNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string SourceLabel { get; set; } = "Windows";
}

public partial class RestorePoints : Page, ILocalizable
{
    public RestorePoints()
    {
        InitializeComponent();
        ApplyLanguage();
        Loaded += async (_, _) => await LoadAsync();
    }

    public void ApplyLanguage()
    {
        if (LblPageTitle == null) return;

        var zh = LocalizationService.IsChinese;

        LblPageTitle.Text = zh ? "🛡️  系统还原点" : "🛡️  System Restore Points";
        BtnRefreshList.Content = zh ? "↻  刷新列表" : "↻  Refresh List";
        BtnCreateRestorePoint.Content = zh ? "＋  立即创建还原点" : "＋  Create Restore Point";

        RunDescPrefix.Text = zh
            ? "用于修复系统崩溃、蓝屏、驱动问题。（恢复丢失的文件请使用 "
            : "For fixing system crashes, blue screens, driver issues. (For lost files use ";
        RunDescLink.Text = zh ? "📂 快照管理 →" : "📂 Snapshots →";
        RunDescSuffix.Text = zh ? "）" : ")";

        ColDateTime.Text = zh ? "日期与时间" : "Date & Time";
        ColDesc.Text = zh ? "描述" : "Description";
        ColSource.Text = zh ? "来源" : "Source";
        ColAction.Text = zh ? "操作" : "Action";

        EmptyStateTitle.Text = zh ? "暂无系统还原点" : "No System Restore Points";
        EmptyStateDesc.Text = zh
            ? "未检测到 Windows 系统还原点。点击上方「立即创建还原点」可立即生成一个。"
            : "No Windows system restore points detected. Click \"Create Restore Point\" to create one now.";

        // In-row buttons live in a DataTemplate and are localized via Loaded; refresh the container to retrigger
        RestorePointsItemsControl?.Items.Refresh();
    }

    // Buttons inside a DataTemplate cannot use ApplyLanguage; set their text in the Loaded event per current language
    private void BtnRestoreToPoint_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
            btn.Content = LocalizationService.IsChinese ? "还原到此点" : "Restore to this point";
    }

    private void SetEmptyState(bool isEmpty, string? errorMessage = null)
    {
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        RestorePointsItemsControl.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (isEmpty && errorMessage != null)
        {
            var zh = LocalizationService.IsChinese;
            EmptyStateTitle.Text = zh ? "无法加载还原点" : "Could Not Load Restore Points";
            EmptyStateDesc.Text = errorMessage;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var resp = await ServiceClient.SendAsync("LIST_SYSTEM_RESTORE_POINTS");
            if (resp.Success && !string.IsNullOrWhiteSpace(resp.Data))
            {
                var points = JsonSerializer.Deserialize<List<RestorePointInfo>>(resp.Data) ?? [];
                var viewModels = points.Select(p => new RestorePointViewModel
                {
                    SequenceNumber = p.SequenceNumber,
                    Description = p.Description,
                    CreatedAt = p.CreatedAt.ToLocalTime(),
                    SourceLabel = p.Description.Contains("unlose", StringComparison.OrdinalIgnoreCase)
                        ? "unlose" : "Windows"
                }).OrderByDescending(p => p.CreatedAt).ToList();

                RestorePointsItemsControl.ItemsSource = viewModels;
                SetEmptyState(viewModels.Count == 0);
            }
            else
            {
                RestorePointsItemsControl.ItemsSource = null;
                var errMsg = !resp.Success && !string.IsNullOrWhiteSpace(resp.ErrorMessage)
                    ? resp.ErrorMessage
                    : (LocalizationService.IsChinese
                        ? "服务未返回还原点数据。请确认 Unlose Service 正在运行，且系统还原功能已启用。"
                        : "Service did not return restore point data. Ensure Unlose Service is running and System Restore is enabled.");
                SetEmptyState(true, errMsg);
            }
        }
        catch (Exception ex)
        {
            RestorePointsItemsControl.ItemsSource = null;
            SetEmptyState(true, ex.Message);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void RunDescLink_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.MainFrame.Navigate(new SnapshotLibraryPage());
    }

    private async void CreateRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        var resp = await ServiceClient.SendAsync("CREATE_SYSTEM_RESTORE_POINT",
            new Dictionary<string, string> { ["description"] = "unlose Manual Restore Point" });
        if (resp.Success)
        {
            MessageBox.Show("系统还原点已创建。", "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadAsync();
        }
        else
        {
            MessageBox.Show($"创建失败：{resp.ErrorMessage}", "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RestorePoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int seqNumber) return;

        var zh = LocalizationService.IsChinese;
        var confirm = MessageBox.Show(
            zh
                ? $"确认将系统还原到序列号 {seqNumber} 的还原点？\n\n" +
                  "① 系统配置与驱动程序将回退到该还原点的状态；\n" +
                  "② 个人文件（文档、照片等）不受影响；\n" +
                  "③ 完成后需要重启计算机；\n" +
                  "④ 还原过程由 Windows 执行，且不可撤销。"
                : $"Restore the system to restore point #{seqNumber}?\n\n" +
                  "1. System settings and drivers will revert to this restore point;\n" +
                  "2. Personal files (documents, photos, etc.) are not affected;\n" +
                  "3. A restart is required to complete the restore;\n" +
                  "4. The restore is performed by Windows and cannot be undone.",
            zh ? "确认系统还原" : "Confirm System Restore",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var resp = await ServiceClient.SendAsync("APPLY_SYSTEM_RESTORE_POINT",
            new Dictionary<string, string> { ["sequenceNumber"] = seqNumber.ToString() });

        if (resp.Success)
            MessageBox.Show(
                zh ? "系统还原已调度。请手动重启计算机以完成还原。"
                   : "System restore has been scheduled. Restart the computer manually to complete it.",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(
                zh ? $"还原失败：{resp.ErrorMessage}" : $"Restore failed: {resp.ErrorMessage}",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // Used to deserialize the service response
    private sealed class RestorePointInfo
    {
        public int SequenceNumber { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}

