using System.Windows;
using System.Windows.Input;

namespace Unlose.UI.Views;

public partial class InputDialog : Window
{
    public string InputText { get; private set; } = string.Empty;
    public bool Confirmed { get; private set; }

    public InputDialog(string title, string prompt, string defaultValue)
    {
        InitializeComponent();
        LblTitle.Text = title;
        LblPrompt.Text = prompt;
        InputTextBox.Text = defaultValue;

        var zh = LocalizationService.IsChinese;
        BtnCancel.Content = zh ? "取消" : "Cancel";
        BtnOk.Content = zh ? "确定" : "OK";
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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        InputText = InputTextBox.Text?.Trim() ?? string.Empty;
        Confirmed = true;
        Close();
    }
}
