using Unlose.Core.Models;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Unlose.UI.Views.Pages;

public partial class RestoreWizard : Page, ILocalizable
{
    private int _currentStep = 1;

    public RestoreWizard()
    {
        InitializeComponent();
        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        if (LblTitle == null) return;

        var zh = LocalizationService.IsChinese;

        LblTitle.Text = zh ? "⟲ 回滚向导" : "⟲ Restore Wizard";
        LblStep1Label.Text = zh ? "时间范围" : "Time Range";
        LblStep2Label.Text = zh ? "选择还原点" : "Restore Point";

        LblStep2Prompt.Text = zh ? "大概是什么时候丢失/被改动的？" : "When was the data lost or modified?";
        LblStep3Prompt.Text = zh ? "请选择还原点：" : "Select a restore point:";

        // Step 1 (time range) RadioButtons
        LblTodayTitle.Text = zh ? "📅 今天" : "📅 Today";
        LblTodayDesc.Text = zh ? "展示今天（自然日）以来的快照。" : "Shows snapshots created today (calendar day).";
        Lbl7dTitle.Text = zh ? "🗓 近 7 天" : "🗓 Last 7 Days";
        Lbl7dDesc.Text = zh ? "展示最近 7 天以来的快照。" : "Shows snapshots from the last 7 days.";
        Lbl30dTitle.Text = zh ? "🗓 近 30 天" : "🗓 Last 30 Days";
        Lbl30dDesc.Text = zh ? "展示最近 30 天以来的快照。" : "Shows snapshots from the last 30 days.";
        LblCustomTitle.Text = zh ? "🗓 指定日期范围…" : "🗓 Custom Date Range…";
        LblCustomDesc.Text = zh ? "选择自定义日期范围进行筛选。" : "Filter by custom date range.";
        LblStartDate.Text = zh ? "开始日期：" : "Start: ";
        LblEndDate.Text = zh ? "  结束日期：" : "  End: ";

        PrevButton.Content = zh ? "← 上一步" : "← Previous";
        BtnCancel.Content = zh ? "取消并返回" : "Cancel";

        GoToStep(_currentStep);
    }

    private void NextStep_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            // Load candidate snapshots when entering step 2 (select restore point)
            _ = LoadCandidateSnapshotsAsync();
            GoToStep(2);
        }
        else if (_currentStep == 2)
        {
            LaunchImmersiveRestore();
        }
    }

    private void PrevStep_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            GoToStep(_currentStep - 1);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        NavigationService?.GoBack();
    }

    private void GoToStep(int step)
    {
        _currentStep = step;
        var zh = LocalizationService.IsChinese;

        // Two-step flow: step1 = time range (Step2Content), step2 = select restore point (Step3Content)
        Step2Content.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3Content.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;

        PrevButton.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;

        StepSubtitleText.Text = step switch
        {
            1 => zh ? "第一步：选择时间范围" : "Step 1: Set Time Range",
            2 => zh ? "第二步：选择还原点" : "Step 2: Select Restore Point",
            _ => string.Empty
        };

        NextButton.Content = step == 2
            ? (zh ? "🚀 进入沉浸式还原" : "🚀 Enter Immersive Restore")
            : (zh ? "下一步 ➡️" : "Next ➡️");

        var activeColor = new SolidColorBrush(Color.FromRgb(0x03, 0x69, 0xA1));
        var inactiveColor = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
        var activeTextColor = new SolidColorBrush(Color.FromRgb(0x03, 0x69, 0xA1));
        var inactiveTextColor = new SolidColorBrush(Color.FromRgb(0x4A, 0x7A, 0x9B));

        Step1Indicator.Background = step >= 1 ? activeColor : inactiveColor;
        Step2Indicator.Background = step >= 2 ? activeColor : inactiveColor;

        LblStep1Label.Foreground = step >= 1 ? activeTextColor : inactiveTextColor;
        LblStep2Label.Foreground = step >= 2 ? activeTextColor : inactiveTextColor;

        Connector1.Background = step >= 2 ? activeColor : inactiveColor;
    }

    private bool _snapshotLoadFailed;

    private async Task LoadCandidateSnapshotsAsync()
    {
        try
        {
            DateTime cutoff;
            if (TimeCustom?.IsChecked == true)
            {
                var start = StartDatePicker?.SelectedDate ?? DateTime.Today.AddDays(-7);
                var end = EndDatePicker?.SelectedDate ?? DateTime.Today;
                var days = Math.Max(1, (int)(end - start).TotalDays + 1);
                cutoff = DateTime.UtcNow.AddDays(-days);
            }
            else if (Time30d?.IsChecked == true)
            {
                cutoff = DateTime.UtcNow.AddDays(-30);
            }
            else if (Time7d?.IsChecked == true)
            {
                cutoff = DateTime.UtcNow.AddDays(-7);
            }
            else
            {
                // TimeToday: by calendar day (CreatedAt is UTC, so convert using local midnight today)
                cutoff = DateTime.Today.ToUniversalTime();
            }

            var resp = await ServiceClient.SendAsync("LIST_SNAPSHOTS");
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Data))
            {
                _snapshotLoadFailed = true;
                CandidateSnapshotList.ItemsSource = null;
                UpdateStep3Status();
                return;
            }

            var all = JsonSerializer.Deserialize<List<SnapshotRecord>>(resp.Data) ?? [];
            CandidateSnapshotList.ItemsSource = all
                .Where(s => s.CreatedAt >= cutoff)
                .OrderByDescending(s => s.CreatedAt)
                .ToList();
            _snapshotLoadFailed = false;
            UpdateStep3Status();
        }
        catch
        {
            _snapshotLoadFailed = true;
            CandidateSnapshotList.ItemsSource = null;
            UpdateStep3Status();
        }
    }

    private void UpdateStep3Status()
    {
        if (Step3StatusPanel == null) return;

        var zh = LocalizationService.IsChinese;

        if (_snapshotLoadFailed)
        {
            Step3StatusText.Text = zh
                ? "无法加载快照列表：服务未连接或发生错误。"
                : "Could not load snapshots: the service is not connected or an error occurred.";
            BtnRetrySnapshots.Content = zh ? "重试" : "Retry";
            BtnRetrySnapshots.Visibility = Visibility.Visible;
            Step3StatusPanel.Visibility = Visibility.Visible;
        }
        else if (CandidateSnapshotList.Items.Count == 0)
        {
            Step3StatusText.Text = zh
                ? "该时间范围内暂无快照。"
                : "No snapshots in this time range.";
            BtnRetrySnapshots.Visibility = Visibility.Collapsed;
            Step3StatusPanel.Visibility = Visibility.Visible;
        }
        else
        {
            Step3StatusPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnRetrySnapshots_Click(object sender, RoutedEventArgs e)
        => await LoadCandidateSnapshotsAsync();

    private void LaunchImmersiveRestore()
    {
        var zh = LocalizationService.IsChinese;
        if (CandidateSnapshotList.SelectedItem is not SnapshotRecord selected)
        {
            MessageBox.Show(zh ? "请先从列表中选择一个还原点。" : "Please select a restore point from the list.",
                "unlose", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.MainFrame.Navigate(new ImmersiveRestorePage(selected.Id.ToString()));
    }
}
