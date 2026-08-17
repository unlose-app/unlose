using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Unlose.UI.Views.Pages;

/// <summary>About page: version, service status, data directory, copyright.</summary>
public partial class AboutPage : Page, ILocalizable
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "unlose");

    private string? _statusEn;
    private string? _statusZh;
    private string _currentVersion = "?";
    private bool _updateAvailable;

    public AboutPage()
    {
        InitializeComponent();
        LoadAppInfo();
        ApplyLanguage();
    }

    private void LoadAppInfo()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // Assembly.Location is empty in single-file publishing: prefer AssemblyVersion (same source as FileVersion)
            var version = asm.GetName().Version?.ToString();
            if (string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(asm.Location))
                version = FileVersionInfo.GetVersionInfo(asm.Location).FileVersion;
            version ??= "1.0.0";
            _currentVersion = version;
            TxtVersion.Text = version;

            var (ok, paused, suspended, detail) = QueryServiceStatus();
            _statusZh = ok
                ? $"运行中{(paused ? "（已暂停）" : "")}{(suspended ? "（已挂起）" : "")}"
                : $"异常（{detail}）";
            _statusEn = ok
                ? $"Running{(paused ? " (paused)" : "")}{(suspended ? " (suspended)" : "")}"
                : $"Error ({detail})";

            TxtDataDir.Text = AppDataDir;
            TxtCopyright.Text = $"Copyright © 2026 unlose. All rights reserved.";
        }
        catch (Exception ex)
        {
            TxtVersion.Text = "?";
            _statusZh = _statusEn = ex.Message;
        }
    }

    /// <summary>Get the real service status via the bundled unlose.exe status command (JSON: Success/IsPaused/IsSuspended).</summary>
    private static (bool Ok, bool Paused, bool Suspended, string Detail) QueryServiceStatus()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "unlose.exe");
            if (!File.Exists(exe))
                return (false, false, false, "unlose.exe not found");

            var psi = new ProcessStartInfo(exe, "status")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, false, false, "start failed");
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            if (string.IsNullOrWhiteSpace(output)) return (false, false, false, "no response");
            var node = JsonNode.Parse(output);
            var ok = node?["Success"]?.GetValue<bool>() ?? false;
            if (!ok) return (false, false, false, node?["ErrorMessage"]?.ToString() ?? "unknown");
            var data = node?["Data"]?.ToString() ?? "";
            return (true,
                data.Contains("IsPaused=True"),
                data.Contains("IsSuspended=True"),
                data);
        }
        catch (Exception ex)
        {
            return (false, false, false, ex.Message);
        }
    }

    /// <summary>Show state from the cached daily check result: when a newer version exists the button becomes
    /// "Update" (clicking goes straight to the download page); when checked and up to date, show "You are up to date".
    /// Call after LoadAppInfo and ApplyLanguage.</summary>
    private void ApplyCachedUpdateState()
    {
        var zh = LocalizationService.IsChinese;
        _updateAvailable = UiSettings.IsUpdateAvailable(_currentVersion);
        if (_updateAvailable)
        {
            BtnCheckUpdate.Content = zh ? "更新" : "Update";
            TxtUpdateResult.Text = zh
                ? $"发现新版本 {UiSettings.LatestKnownVersion}"
                : $"New version {UiSettings.LatestKnownVersion} available";
        }
        else
        {
            BtnCheckUpdate.Content = zh ? "检查更新" : "Check for updates";
            if (UiSettings.LastUpdateCheckUtc is not null)
                TxtUpdateResult.Text = zh ? "已是最新版本" : "You are up to date";
        }
    }

    private static void OpenDownloadPage(string? url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                string.IsNullOrWhiteSpace(url) ? "https://unlose.app/" : url)
            { UseShellExecute = true });
        }
        catch { /* Browser launch failure is non-blocking */ }
    }

    /// <summary>When a newer version is cached, clicking opens the download page directly; otherwise trigger
    /// a manual check (the result is cached too). The daily auto-check is done by the App timer; this is just
    /// an on-demand re-check entry point.</summary>
    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var zh = LocalizationService.IsChinese;
        if (_updateAvailable)
        {
            OpenDownloadPage(UiSettings.LatestDownloadUrl);
            return;
        }
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateResult.Text = zh ? "正在检查…" : "Checking…";
        try
        {
            var result = await Unlose.Core.Updates.UpdateChecker.CheckAsync();
            UiSettings.RecordUpdateCheck(result);
            if (result is null)
            {
                TxtUpdateResult.Text = zh ? "检查失败（网络不可用或官网不可达）" : "Check failed (network or site unreachable)";
                return;
            }
            ApplyCachedUpdateState();
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    public void ApplyLanguage()
    {
        try
        {
            var zh = LocalizationService.IsChinese;
            LblAboutTitle.Text = zh ? "ℹ️  关于" : "ℹ️  About";
            LblAppName.Text = zh ? "unlose 文件安全防护" : "unlose File Safety Protection";
            LblVersionTitle.Text = zh ? "版本" : "Version";
            LblUpdateTitle.Text = zh ? "更新" : "Update";
            ApplyCachedUpdateState();
            LblStatusTitle.Text = zh ? "服务状态" : "Service Status";
            LblDataTitle.Text = zh ? "数据目录" : "Data Directory";
            LblCopyrightTitle.Text = zh ? "版权" : "Copyright";
            LblAboutBody.Text = zh
                ? "unlose 提供文件系统级卷影保护快照，在 AI 智能体会话开始、执行破坏性操作前自动创建保护，支持按会话定位并恢复误删误改的文件。"
                : "unlose provides filesystem-level shadow protection snapshots, created automatically at AI agent session start and before destructive operations, with session-based recovery for accidentally deleted or modified files.";

            if (_statusZh is not null && _statusEn is not null)
                TxtStatus.Text = zh ? _statusZh : _statusEn;
        }
        catch { /* Localization failure is non-blocking */ }
    }
}
