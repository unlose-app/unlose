using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Unlose.UI.Views;

public partial class PathListDialog : Window
{
    private readonly ObservableCollection<string> _paths = new();

    public string[] Paths => _paths.ToArray();
    public bool Saved { get; private set; }

    public PathListDialog(string defaultPaths)
    {
        InitializeComponent();
        ApplyLanguage();

        // Parse default paths (comma-separated)
        if (!string.IsNullOrWhiteSpace(defaultPaths))
        {
            foreach (var p in defaultPaths.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = p.Trim().TrimEnd('\\');
                if (!string.IsNullOrEmpty(trimmed) && !_paths.Contains(trimmed))
                    _paths.Add(trimmed + "\\");
            }
        }

        // If no paths are configured, auto-scan local fixed disks as defaults
        if (_paths.Count == 0)
        {
            foreach (var drive in DriveInfo.GetDrives()
                         .Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var root = drive.RootDirectory.FullName; // e.g. "C:\"
                if (!_paths.Contains(root))
                    _paths.Add(root);
            }
        }

        PathListBox.ItemsSource = _paths;
    }

    private void ApplyLanguage()
    {
        var zh = LocalizationService.IsChinese;
        LblTitle.Text = zh ? "变更监控目录" : "Change Monitored Paths";
        LblPrompt.Text = zh ? "请添加或删除监控路径（每行一个）：" : "Add or remove monitored paths:";
        BtnAdd.Content = zh ? "＋ 添加" : "＋ Add";
        BtnDelete.Content = zh ? "✕ 删除" : "✕ Delete";
        BtnCancel.Content = zh ? "取消" : "Cancel";
        BtnSave.Content = zh ? "保存" : "Save";
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void AddPath_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;
        var dlg = new OpenFolderDialog
        {
            Title = zh ? "选择监控目录" : "Select Monitored Directory",
            Multiselect = false
        };
        if (dlg.ShowDialog() == true)
        {
            var path = dlg.FolderName;
            if (!string.IsNullOrWhiteSpace(path) && !_paths.Contains(path))
                _paths.Add(path);
        }
    }

    private void DeletePath_Click(object sender, RoutedEventArgs e)
    {
        if (PathListBox.SelectedIndex >= 0)
            _paths.RemoveAt(PathListBox.SelectedIndex);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Saved = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Saved = true;
        Close();
    }
}
