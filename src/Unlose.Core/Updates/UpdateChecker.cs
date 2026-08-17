using System.Text.Json;

namespace Unlose.Core.Updates;

/// <summary>
/// New-version check: GETs the static version.json from the official website and compares it with the current version.
///
/// Zero-telemetry principle: only a single static JSON file is requested, carrying no user identity, machine information, or usage data.
/// Triggers: the UI checks automatically once per day (first check 30 seconds after startup + a 24-hour timer; results are cached in
/// UiSettings, and offline/failure cases are still throttled to 24 hours), plus a manual "Check for updates" click on the About page.
/// Network or parse failures always silently return null and never affect any local functionality.
/// </summary>
public static class UpdateChecker
{
    /// <summary>Version manifest URL (static file on the official website; website/public/version.json is published with the site).</summary>
    public const string DefaultManifestUrl = "https://unlose.app/version.json";

    /// <summary>Check result: latest version number, download page URL, optional notes.</summary>
    public sealed record CheckResult(Version Latest, string? DownloadUrl, string? Notes);

    /// <summary>Fetches and parses the version manifest; any failure (offline/timeout/malformed) returns null.</summary>
    public static async Task<CheckResult?> CheckAsync(
        string manifestUrl = DefaultManifestUrl, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync(manifestUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v)) return null;
            if (!Version.TryParse(v.GetString(), out var latest)) return null;
            var url = root.TryGetProperty("downloadUrl", out var u) ? u.GetString() : null;
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() : null;
            return new CheckResult(latest, url, notes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Determines whether latest is newer than current. Missing segments are padded with 0 ("1.0.4" == "1.0.4.0");
    /// an unparseable current is treated as needing an update (conservative prompting).</summary>
    public static bool IsNewer(string? current, Version latest)
    {
        if (!Version.TryParse(current, out var cur)) return true;
        return Normalize(latest) > Normalize(cur);
    }

    /// <summary>.NET Version uses -1 for omitted segments (less than 0); normalize them to 0 before comparing.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor,
            v.Build < 0 ? 0 : v.Build,
            v.Revision < 0 ? 0 : v.Revision);
}
