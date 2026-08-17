using System;

namespace Unlose.UI;

/// <summary>
/// Global language state management; pages respond to language switches by subscribing to the LanguageChanged event.
/// Default language: the user's manual choice (UiSettings) takes precedence; then the registry value written
/// by the install-time language dialog (HKLM\Software\unlose\UILanguage); if neither exists, follow the OS
/// display language (zh* → Chinese, otherwise → English), so English-region users get an English UI on first install.
/// </summary>
public static class LocalizationService
{
    private static bool _isChinese = ResolveInitialLanguage();

    private static bool ResolveInitialLanguage()
    {
        var pref = UiSettings.LanguagePreference ?? InstallerLanguageFromRegistry();
        if (pref == "zh") return true;
        if (pref == "en") return false;
        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Install-level default written by the install-time language selection dialog (HKLM\Software\unlose\UILanguage)</summary>
    private static string? InstallerLanguageFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\unlose");
            return key?.GetValue("UILanguage") as string;
        }
        catch { return null; }
    }

    /// <summary>Switch language manually and persist it (no longer follows the OS on next start)</summary>
    public static void SetLanguage(bool isChinese)
    {
        IsChinese = isChinese;
        UiSettings.SetLanguagePreference(isChinese ? "zh" : "en");
    }

    public static bool IsChinese
    {
        get => _isChinese;
        set
        {
            if (_isChinese == value) return;
            _isChinese = value;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? LanguageChanged;

    public static string T(string zh, string en) => _isChinese ? zh : en;
}
