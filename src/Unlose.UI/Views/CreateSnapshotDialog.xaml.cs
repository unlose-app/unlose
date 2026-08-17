using System.Windows;
using System.Windows.Input;

namespace Unlose.UI.Views;

public partial class CreateSnapshotDialog : Window
{
    public string Description { get; private set; } = string.Empty;
    public bool Confirmed { get; private set; }

    public CreateSnapshotDialog()
    {
        InitializeComponent();
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        var zh = LocalizationService.IsChinese;
        LblTitle.Text = zh ? "创建快照" : "Create Snapshot";
        LblDesc.Text = zh ? "快照描述（可选）：" : "Description (optional):";
        DescTextBox.Text = zh ? "手动创建" : "Manual Snapshot";
        BtnCancel.Content = zh ? "取消" : "Cancel";
        BtnCreate.Content = zh ? "创建" : "Create";
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;
        var text = DescTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            text = zh ? "手动创建" : "Manual Snapshot";

        Description = text;
        Confirmed = true;
        Close();
    }
}
