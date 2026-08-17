using Unlose.Core.Agents;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Unlose.Service;

/// <summary>
/// Global memory injector: on service startup, idempotently appends the unlose
/// snapshot guard instruction block to ~/AGENTS.md and to each agent's global
/// memory files under real user home directories.
///
/// Design notes:
/// - Idempotent: the <!-- unlose:snapshot-guard v4 --> marker is used for dedup;
///   if already present, skip — never append twice;
/// - Version migration: when a legacy v1/v2/v3 marker block is detected, replace the
///   whole block with v4 (v4 upgrades: --source-tool takes the process name, "agent"
///   falls back to auto-detecting the host, skill packages self-report --channel skill;
///   v3 highlights: self-report agent name; v2 highlights: self-report identity to
///   trigger AgentInitiated, --session session id, full-path CLI reachability);
/// - Non-destructive: existing file content is preserved; only appended at the end;
/// - Real users: the service runs as LocalSystem and %USERPROFILE% points to
///   systemprofile, so real user directories under C:\Users\* must be enumerated
///   and injected one by one;
/// - Single-user injection logic is isolated (InjectIntoHomeAsync) for unit testing.
/// </summary>
public class GlobalMemoryInjector
{
    private readonly ILogger<GlobalMemoryInjector> _logger;
    private readonly string _cliExePath;

    public const string MarkerBegin = "<!-- unlose:snapshot-guard v5 -->";
    public const string MarkerEnd = "<!-- /unlose:snapshot-guard -->";
    public const string LegacyMarkerBeginV1 = "<!-- unlose:snapshot-guard v1 -->";
    public const string LegacyMarkerBeginV2 = "<!-- unlose:snapshot-guard v2 -->";
    public const string LegacyMarkerBeginV3 = "<!-- unlose:snapshot-guard v3 -->";
    public const string LegacyMarkerBeginV4 = "<!-- unlose:snapshot-guard v4 -->";

    /// <summary>Version-agnostic begin-marker prefix: uninstall removal locates blocks by this prefix, compatible with any historical version of the instruction block.</summary>
    public const string MarkerPrefix = "<!-- unlose:snapshot-guard";

    /// <param name="cliExePath">Full path of the unlose CLI (the invocation entry in the injected instructions); defaults to the service install directory.</param>
    public GlobalMemoryInjector(ILogger<GlobalMemoryInjector> logger, string? cliExePath = null)
    {
        _logger = logger;
        _cliExePath = cliExePath ?? GuardInstructions.DefaultCliExePath();
    }

    /// <summary>Injected instruction block content (bilingual Chinese/English, wrapped in markers).</summary>
    public string BuildBlock() => $"""

{MarkerBegin}
{GuardInstructions.BuildBody(_cliExePath)}
{MarkerEnd}
""";

    /// <summary>Target enumeration shared by inject/remove: ~/AGENTS.md + global memory files of installed agents.</summary>
    private static List<(string Key, string Path)> EnumerateTargets(string homeDir)
    {
        var targets = new List<(string Key, string Path)>
        {
            ("universal", Path.Combine(homeDir, "AGENTS.md"))
        };
        foreach (var agent in AgentRegistry.All)
        {
            // Install detection: only inject into agents that are installed (config directory exists); never create directories for uninstalled ones
            if (agent.InstallDir is null || !Directory.Exists(Path.Combine(homeDir, agent.InstallDir)))
                continue;
            foreach (var rel in agent.GlobalMemoryFiles)
                targets.Add((agent.Key, Path.Combine(homeDir, rel)));
        }
        return targets;
    }

    /// <summary>Inject into the specified user home directory (~/AGENTS.md + each agent's global memory files). Returns one result line per target.</summary>
    public async Task<IReadOnlyList<string>> InjectIntoHomeAsync(string homeDir, CancellationToken ct = default)
    {
        var results = new List<string>();

        foreach (var (key, path) in EnumerateTargets(homeDir))
        {
            try
            {
                var dir = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(dir);

                var existing = File.Exists(path)
                    ? await File.ReadAllTextAsync(path, Encoding.UTF8, ct)
                    : string.Empty;

                if (existing.Contains(MarkerBegin, StringComparison.Ordinal))
                {
                    results.Add($"SKIP {key}: already present ({path})");
                    continue;
                }

                // v1/v2/v3/v4 → v5 migration: remove the legacy block wholesale, then append the new content
                var upgraded = false;
                foreach (var legacy in new[] { LegacyMarkerBeginV1, LegacyMarkerBeginV2, LegacyMarkerBeginV3, LegacyMarkerBeginV4 })
                {
                    if (existing.Contains(legacy, StringComparison.Ordinal))
                    {
                        existing = RemoveLegacyBlock(existing, legacy);
                        upgraded = true;
                        break;
                    }
                }

                var content = existing.Length > 0 && !existing.EndsWith('\n')
                    ? existing + "\n"
                    : existing;
                await File.WriteAllTextAsync(path, content + BuildBlock() + "\n", new UTF8Encoding(false), ct);
                results.Add($"{(upgraded ? "UPGRADED" : "OK")} {key}: {path}");
            }
            catch (Exception ex)
            {
                results.Add($"FAIL {key}: {ex.Message} ({path})");
            }
        }

        return results;
    }

    /// <summary>Enumerate real user directories under Users on the system drive and inject into each (LocalSystem friendly).</summary>
    public async Task InjectForAllUsersAsync(CancellationToken ct = default)
    {
        foreach (var homeDir in RealUserHomes.Enumerate())
        {
            try
            {
                var results = await InjectIntoHomeAsync(homeDir, ct);
                foreach (var r in results)
                    _logger.LogInformation("GlobalMemoryInject [{User}]: {Result}", Path.GetFileName(homeDir), r);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GlobalMemoryInject [{User}] failed", Path.GetFileName(homeDir));
            }
        }
    }

    /// <summary>Remove a specific version's marker block (whole block from begin marker to end marker, inclusive).</summary>
    private static string RemoveLegacyBlock(string text, string legacyMarker)
    {
        var start = text.IndexOf(legacyMarker, StringComparison.Ordinal);
        if (start < 0) return text;
        var end = text.IndexOf(MarkerEnd, start, StringComparison.Ordinal);
        if (end < 0) return text;
        end += MarkerEnd.Length;
        // Also remove the trailing newline of the block to avoid leaving a blank section
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        return (text[..start] + text[end..]).TrimEnd() + "\n";
    }

    // ── Uninstall cleanup (patent claim 11: locate and remove the guard block by paired markers; residue-free uninstall) ──────────

    /// <summary>Remove the guard instruction block from text (version-agnostic prefix match + paired end marker; incomplete blocks are left untouched to avoid damaging user content).</summary>
    public static (string Text, bool Removed) RemoveGuardBlock(string text)
    {
        var start = text.IndexOf(MarkerPrefix, StringComparison.Ordinal);
        if (start < 0) return (text, false);
        var end = text.IndexOf(MarkerEnd, start, StringComparison.Ordinal);
        if (end < 0) return (text, false);
        end += MarkerEnd.Length;
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        // The injected block starts with a blank line: remove that preceding separator blank line too, restoring the original layout
        if (start > 0 && text[start - 1] == '\n') start--;
        var result = (text[..start] + text[end..]).TrimEnd();
        return (result.Length == 0 ? string.Empty : result + "\n", true);
    }

    /// <summary>
    /// Uninstall cleanup: remove the guard instruction block from each global memory
    /// file under the specified user home directory, restoring the original content;
    /// files created solely by injection (nothing left after block removal) are deleted
    /// entirely for a residue-free uninstall. Returns one result line per target.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemoveFromHomeAsync(string homeDir, CancellationToken ct = default)
    {
        var results = new List<string>();

        foreach (var (key, path) in EnumerateTargets(homeDir))
        {
            try
            {
                if (!File.Exists(path))
                {
                    results.Add($"SKIP {key}: not present ({path})");
                    continue;
                }

                var existing = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
                var (cleaned, removed) = RemoveGuardBlock(existing);
                if (!removed)
                {
                    results.Add($"SKIP {key}: no guard block ({path})");
                    continue;
                }

                if (cleaned.Length == 0)
                    File.Delete(path); // file was created by injection; remove it entirely
                else
                    await File.WriteAllTextAsync(path, cleaned, new UTF8Encoding(false), ct);
                results.Add($"REMOVED {key}: {path}");
            }
            catch (Exception ex)
            {
                results.Add($"FAIL {key}: {ex.Message} ({path})");
            }
        }

        return results;
    }

    /// <summary>Enumerate real user directories and remove the guard block from each, with logging (invoked via pipe by the uninstall custom action).</summary>
    public async Task<IReadOnlyList<string>> RemoveForAllUsersAsync(CancellationToken ct = default)
    {
        var all = new List<string>();
        foreach (var homeDir in RealUserHomes.Enumerate())
        {
            try
            {
                var results = await RemoveFromHomeAsync(homeDir, ct);
                foreach (var r in results)
                {
                    _logger.LogInformation("UninstallClean [{User}]: {Result}", Path.GetFileName(homeDir), r);
                    all.Add($"[{Path.GetFileName(homeDir)}] {r}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UninstallClean [{User}] failed", Path.GetFileName(homeDir));
                all.Add($"[{Path.GetFileName(homeDir)}] FAIL: {ex.Message}");
            }
        }
        return all;
    }
}
