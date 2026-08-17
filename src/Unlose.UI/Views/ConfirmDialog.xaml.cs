using System.Windows;
using System.Windows.Input;

namespace Unlose.UI.Views;

public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDialog(string title, string message, string confirmText, string cancelText)
    {
        InitializeComponent();
        LblTitle.Text = title;
        LblMessage.Text = message;
        BtnConfirm.Content = confirmText;
        BtnCancel.Content = cancelText;
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

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }
}
