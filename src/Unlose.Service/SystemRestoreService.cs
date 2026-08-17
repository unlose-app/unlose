using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Microsoft.Extensions.Logging;
using System.Management;

namespace Unlose.Service;

/// <summary>
/// Fix: implements create/list/apply of system restore points via the WMI SystemRestore class.
/// Replaces the original Task.Delay stub implementation.
/// Note: creating a system restore point requires administrator privileges, and System Restore must
/// already be enabled on the target drive.
/// Implements <see cref="ISystemRestoreGateway"/> so upper layers (CommandDispatcher) can depend on the interface via DI.
/// </summary>
public class SystemRestoreService : ISystemRestoreGateway
{
    private readonly ILogger<SystemRestoreService> _logger;
    private const string WmiScope = @"\\.\root\default";

    public SystemRestoreService(ILogger<SystemRestoreService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates an application-install restore point on the system volume.
    /// BUG-SRP-001 (e2e finding 2, P0) fix:
    /// The original implementation judged success solely by WMI SystemRestore.CreateRestorePoint
    /// ReturnValue==0, but in some cases (VSS/SR subsystem contention, low disk space, SR not enabled
    /// on the volume) the API exhibits a "false success": ReturnValue=0 (success) and event log 8194 shows
    /// "successfully created", yet Get-ComputerRestorePoint / the WMI SystemRestore class catalog has no
    /// corresponding record — while the service log confidently reports created successfully.
    /// Fix: cross-verify with a WMI list query immediately after creation; if no record matching the
    /// description/time window appears among new restore points within 5 seconds (polling), treat it as
    /// a silent failure, return false, and log detailed diagnostics.
    /// </summary>
    public async Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken ct = default)
    {
        _logger.LogInformation("Creating system restore point: {Description}", description);

        // Record the maximum SequenceNumber that exists before creation, as the lower bound for a "newly created point"
        int seqBefore;
        try { seqBefore = await GetMaxSequenceNumberAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to enumerate existing restore points before create; cross-check will be skipped.");
            seqBefore = -1;
        }
        var tCreate = DateTime.UtcNow;

        try
        {
            var apiResult = await Task.Run(() => WmiCreateRestorePoint(description), ct);
            if (!apiResult)
            {
                _logger.LogWarning("SystemRestore.CreateRestorePoint returned failure for: {Description}", description);
                return RestorePointResult.Fail(
                    "SystemRestore.CreateRestorePoint returned non-zero (API rejected the request). " +
                    "Check Windows privileges and that System Restore is enabled on the target volume.");
            }

            // The API reported success; proceed to cross-verification (unless the earlier list enumeration itself failed)
            if (seqBefore < 0)
            {
                _logger.LogWarning(
                    "API reported success for '{Description}' but cross-check is skipped (pre-list failed). " +
                    "Treat as success-to-be-verified.", description);
                return RestorePointResult.Ok();
            }

            // Polling query: the SR subsystem may take a few hundred milliseconds to register the new point
            const int maxAttempts = 5;
            const int delayMs = 1000;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(delayMs, ct);

                IReadOnlyList<SystemRestorePointInfo> after;
                try { after = await WmiListRestorePoints(ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Cross-check attempt {Attempt}: list failed; will retry.", attempt);
                    continue;
                }

                var newPoint = after.FirstOrDefault(p =>
                    p.SequenceNumber > seqBefore &&
                    p.CreatedAt >= tCreate.AddMinutes(-1));

                if (newPoint is not null)
                {
                    _logger.LogInformation(
                        "Cross-check PASSED: new restore point seq={Seq} desc='{Desc}' createdAt={At} (apiResult=true).",
                        newPoint.SequenceNumber, newPoint.Description, newPoint.CreatedAt);
                    return RestorePointResult.Ok();
                }

                _logger.LogDebug(
                    "Cross-check attempt {Attempt}/{Max}: no new restore point found yet (seqBefore={Before}).",
                    attempt, maxAttempts, seqBefore);
            }

            // DIAG-SRP-001 improvement: when cross-verification fails, collect diagnostic context (VSS quota +
            // throttle interval) so the user/operator knows whether "the quota is full" or "throttled", and can act directly.
            var diag = await CollectSilentFailureDiagnosticsAsync(ct);
            _logger.LogError(
                "BUG-SRP-001 detected: SystemRestore.CreateRestorePoint returned 0 (success) " +
                "AND Windows event 8194 may show 'successfully created', but the new point did NOT appear " +
                "in the WMI SystemRestore catalog within {Seconds}s. This is a silent failure. " +
                "Diagnostics: {Diag}. Description='{Description}'.",
                maxAttempts * delayMs / 1000, diag, description);
            return RestorePointResult.Fail(diag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create system restore point: {Description}", description);
            return RestorePointResult.Fail(
                $"Exception while creating restore point: {ex.Message}");
        }
    }

    /// <summary>Gets the current maximum SequenceNumber as the lower bound for newly created points (returns 0 when no restore points exist).</summary>
    private async Task<int> GetMaxSequenceNumberAsync(CancellationToken ct)
    {
        var list = await Task.Run(() => WmiListRestorePoints(), ct);
        return list.Count == 0 ? 0 : list.Max(p => p.SequenceNumber);
    }

    /// <summary>
    /// DIAG-SRP-001: when cross-verification detects a "false success" (API returned 0 but the point was
    /// never cataloged), collects actionable diagnostic context.
    /// Investigation confirmed two main root causes of Windows SR false success (they apply equally to
    /// any account; not an unlose bug):
    /// 1. VSS shadow-copy storage quota exhausted (the Max reported by vssadmin list shadowstorage)
    /// 2. SystemRestorePointCreationFrequency throttling (by default only 1 point per 24h)
    /// This method reads both and composes a readable string so the user knows how to fix it
    /// (resize the quota / adjust the frequency).
    /// Failure of any individual item does not affect the main flow (returns whatever was collected).
    /// </summary>
    private async Task<string> CollectSilentFailureDiagnosticsAsync(CancellationToken ct)
    {
        var parts = new List<string>();

        // 1) Throttle interval: SystemRestorePointCreationFrequency (minutes)
        //    Microsoft docs: default is 1440 (24h); an absent key means the default; 0 disables throttling.
        //    https://learn.microsoft.com/en-us/windows/win32/api/srrestoreptapi/nf-srrestoreptapi-srsetrestorepointw
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", writable: false);
            if (key?.GetValue("SystemRestorePointCreationFrequency") is int freq)
            {
                parts.Add(freq == 0
                    ? "SystemRestorePointCreationFrequency=0 (no throttle)"
                    : $"SystemRestorePointCreationFrequency={freq}min (throttle active; duplicate calls within this window are silently skipped)");
            }
            else
            {
                parts.Add("SystemRestorePointCreationFrequency=<absent>=1440min default (24h throttle active)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read SystemRestorePointCreationFrequency");
            parts.Add("SystemRestorePointCreationFrequency=<read failed>");
        }

        // 2) VSS shadow-copy storage quota: vssadmin list shadowstorage
        //    Output looks like: "Used Shadow Copy Storage space: 9.33 GB (11%)" / "Maximum Shadow Copy Storage space: 10.0 GB (12%)"
        //    Must be compatible with both Chinese and English Windows.
        try
        {
            var (usedStr, maxStr, usedPct) = await QueryVssShadowStorageAsync(ct);
            if (usedStr is not null)
            {
                var pct = usedPct >= 0 ? $"{usedPct}%" : "?";
                parts.Add($"VSS shadow storage used={usedStr} max={maxStr} ({pct} used)");
                if (usedPct is >= 85 and < 100)
                {
                    parts.Add($"Likely cause: VSS quota near full ({usedPct}%). Suggest: vssadmin resize shadowstorage /for=C: /on=C: /maxsize=20GB");
                }
                else if (usedPct >= 100)
                {
                    parts.Add($"Likely cause: VSS quota EXHAUSTED. Suggest: vssadmin resize shadowstorage /for=C: /on=C: /maxsize=20GB (or delete old shadows)");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query vssadmin shadowstorage");
            parts.Add("VSS shadow storage=<query failed>");
        }

        parts.Add("Also verify: System Restore enabled on target volume (Enable-ComputerRestore -Drive C:\\)");
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Runs vssadmin list shadowstorage and parses Used / Max / UsedPercent.
    /// A null Used indicates parsing failure. Compatible with both Chinese and English Windows output.
    /// </summary>
    private async Task<(string? Used, string? Max, int UsedPct)> QueryVssShadowStorageAsync(CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("vssadmin", "list shadowstorage")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        // Bilingual regex: matches the Chinese and English variants of "Used ... storage: X GB (Y%)"
        var usedMatch = System.Text.RegularExpressions.Regex.Match(
            output,
            @"(?:已用|Used)[^\n]*?(?:存储空间|storage)[^\n]*?:\s*([0-9.]+\s*(?:GB|MB|TB))\s*\(([0-9]+)%\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var maxMatch = System.Text.RegularExpressions.Regex.Match(
            output,
            @"(?:最大|Maximum)[^\n]*?(?:存储空间|storage)[^\n]*?:\s*([0-9.]+\s*(?:GB|MB|TB))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        string? used = usedMatch.Success ? usedMatch.Groups[1].Value.Trim() : null;
        string? max = maxMatch.Success ? maxMatch.Groups[1].Value.Trim() : null;
        int pct = usedMatch.Success && int.TryParse(usedMatch.Groups[2].Value, out var p) ? p : -1;
        return (used, max, pct);
    }

    /// <summary>Thread-safe wrapper around WmiListRestorePoints for reuse by async cross-verification.</summary>
    private Task<IReadOnlyList<SystemRestorePointInfo>> WmiListRestorePoints(CancellationToken _)
        => Task.Run<IReadOnlyList<SystemRestorePointInfo>>(WmiListRestorePoints);

    /// <summary>Lists all restore points currently available on the system</summary>
    public async Task<IReadOnlyList<SystemRestorePointInfo>> ListRestorePointsAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => WmiListRestorePoints(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list system restore points");
            throw; // let the caller see the error instead of masking the problem with an empty list
        }
    }

    /// <summary>
    /// Performs a system restore (restores the system to the restore point with the given sequence number).
    /// A reboot is required for it to take effect; returning true means the operation was initiated and awaits a restart.
    /// </summary>
    public async Task<bool> RestoreToPointAsync(int sequenceNumber, CancellationToken ct = default)
    {
        _logger.LogWarning("Initiating system restore to sequence {Seq}; system will restart.", sequenceNumber);
        try
        {
            await Task.Run(() => WmiRestore(sequenceNumber), ct);
            _logger.LogInformation("System restore initiated for sequenceNumber={Seq}. Reboot required.", sequenceNumber);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate system restore for sequence {Seq}", sequenceNumber);
            return false;
        }
    }

    // ──────────────────────────────────────────────
    // Private WMI implementation
    // ──────────────────────────────────────────────

    private bool WmiCreateRestorePoint(string description)
    {
        var scope = new ManagementScope(WmiScope);
        scope.Connect();
        using var srClass = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
        using var inParams = srClass.GetMethodParameters("CreateRestorePoint");
        inParams["Description"] = description;
        inParams["RestorePointType"] = 0;  // APPLICATION_INSTALL
        inParams["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE

        using var outParams = srClass.InvokeMethod("CreateRestorePoint", inParams, null);
        var returnCode = Convert.ToUInt32(outParams["ReturnValue"]);
        return returnCode == 0;
    }

    private IReadOnlyList<SystemRestorePointInfo> WmiListRestorePoints()
    {
        var results = new List<SystemRestorePointInfo>();
        var scope = new ManagementScope(WmiScope);
        scope.Connect();

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT * FROM SystemRestore"));

        foreach (ManagementObject obj in searcher.Get())
        {
            try
            {
                using (obj)
                {
                    var creationTimeObj = obj["CreationTime"];
                    DateTime createdAt;
                    if (creationTimeObj != null)
                    {
                        var dmtfStr = creationTimeObj.ToString();
                        // Try ManagementDateTimeConverter first (proper WMI date handling)
                        try
                        {
                            createdAt = ManagementDateTimeConverter.ToDateTime(dmtfStr);
                        }
                        catch
                        {
                            // Fallback: parse first 14 chars as yyyyMMddHHmmss
                            createdAt = dmtfStr.Length >= 14
                                ? DateTime.ParseExact(dmtfStr[..14], "yyyyMMddHHmmss",
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.AssumeUniversal)
                                : DateTime.MinValue;
                        }
                    }
                    else
                    {
                        createdAt = DateTime.MinValue;
                    }

                    results.Add(new SystemRestorePointInfo
                    {
                        SequenceNumber = Convert.ToInt32(obj["SequenceNumber"]),
                        Description = obj["Description"]?.ToString() ?? string.Empty,
                        CreatedAt = createdAt,
                        RestorePointType = Convert.ToInt32(obj["RestorePointType"])
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipped malformed SystemRestorePoint record");
            }
        }
        _logger.LogInformation("Listed {Count} system restore points", results.Count);
        return results.AsReadOnly();
    }

    private static void WmiRestore(int sequenceNumber)
    {
        var scope = new ManagementScope(@"\\.\root\default");
        scope.Connect();
        using var srClass = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
        using var inParams = srClass.GetMethodParameters("Restore");
        inParams["SequenceNumber"] = (uint)sequenceNumber;
        srClass.InvokeMethod("Restore", inParams, null);
    }
}
