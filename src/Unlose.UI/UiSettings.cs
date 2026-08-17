using System;
using System.IO;
using System.Text.Json;

namespace Unlose.UI;

/// <summary>
/// UI-side local settings (not written to the service config.json), persisted to %ProgramData%\unlose\ui-settings.json.
/// Records the deadline of the tray notification "snooze for 24 hours", plus the cached results of the daily
/// update check (last check time / latest known version / download URL), surviving UI restarts.
/// </summary>
public static class UiSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "unlose", "ui-settings.json");

    /// <summary>Daily auto-check interval</summary>
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private sealed class UiSettingsDto
    {
        public DateTime? NotificationSnoozedUntilUtc { get; set; }
        public DateTime? LastUpdateCheckUtc { get; set; }
        public string? LatestKnownVersion { get; set; }
        public string? LatestDownloadUrl { get; set; }
        public string? LanguagePreference { get; set; }
    }

    /// <summary>Notification snooze deadline (UTC); null or expired means not snoozed</summary>
    public static DateTime? NotificationSnoozedUntilUtc { get; private set; }

    /// <summary>Time of the last update check (UTC, including failures — failures are throttled too, to avoid hammering the site while offline)</summary>
    public static DateTime? LastUpdateCheckUtc { get; private set; }

    /// <summary>Latest version number found on the official site (keeps the old value when the check fails)</summary>
    public static string? LatestKnownVersion { get; private set; }

    /// <summary>Download page URL of the latest version (keeps the old value when the check fails)</summary>
    public static string? LatestDownloadUrl { get; private set; }

    /// <summary>UI language manually chosen by the user ("zh"/"en"); null means never chosen — follow the OS language</summary>
    public static string? LanguagePreference { get; private set; }

    static UiSettings() => Load();

    public static void SetLanguagePreference(string? lang)
    {
        LanguagePreference = lang;
        Save();
    }

    /// <summary>Whether notifications are currently snoozed (auto-resumes on expiry; no manual cleanup needed)</summary>
    public static bool IsNotificationSnoozed =>
        NotificationSnoozedUntilUtc is { } until && until > DateTime.UtcNow;

    /// <summary>Only check again when more than 24 hours have passed since the last check (or when never checked)</summary>
    public static bool ShouldCheckUpdatesNow =>
        LastUpdateCheckUtc is not { } t || (DateTime.UtcNow - t) > UpdateCheckInterval;

    /// <summary>Record an update check result; when result is null (network failure) only the check timestamp is refreshed</summary>
    public static void RecordUpdateCheck(Core.Updates.UpdateChecker.CheckResult? result)
    {
        LastUpdateCheckUtc = DateTime.UtcNow;
        if (result is not null)
        {
            LatestKnownVersion = result.Latest.ToString();
            LatestDownloadUrl = result.DownloadUrl;
        }
        Save();
    }

    /// <summary>Whether the cache holds a version newer than currentVersion</summary>
    public static bool IsUpdateAvailable(string? currentVersion) =>
        LatestKnownVersion is { } v
        && Version.TryParse(v, out var latest)
        && Core.Updates.UpdateChecker.IsNewer(currentVersion, latest);

    public static void SnoozeNotifications(TimeSpan duration)
    {
        NotificationSnoozedUntilUtc = DateTime.UtcNow.Add(duration);
        Save();
    }

    public static void ResumeNotifications()
    {
        NotificationSnoozedUntilUtc = null;
        Save();
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var dto = JsonSerializer.Deserialize<UiSettingsDto>(File.ReadAllText(FilePath));
            NotificationSnoozedUntilUtc = dto?.NotificationSnoozedUntilUtc;
            LastUpdateCheckUtc = dto?.LastUpdateCheckUtc;
            LatestKnownVersion = dto?.LatestKnownVersion;
            LatestDownloadUrl = dto?.LatestDownloadUrl;
            LanguagePreference = dto?.LanguagePreference;
        }
        catch
        {
            // Treat a corrupted file as not snoozed / never checked
            NotificationSnoozedUntilUtc = null;
            LastUpdateCheckUtc = null;
            LatestKnownVersion = null;
            LatestDownloadUrl = null;
            LanguagePreference = null;
        }
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new UiSettingsDto
                {
                    NotificationSnoozedUntilUtc = NotificationSnoozedUntilUtc,
                    LastUpdateCheckUtc = LastUpdateCheckUtc,
                    LatestKnownVersion = LatestKnownVersion,
                    LatestDownloadUrl = LatestDownloadUrl,
                    LanguagePreference = LanguagePreference
                }));
        }
        catch
        {
            // Persistence failure does not block interaction: in-process state still applies for this session
        }
    }
}
