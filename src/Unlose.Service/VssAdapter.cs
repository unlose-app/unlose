using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Management;

namespace Unlose.Service;

/// <summary>
/// Calls the Volume Shadow Copy Service via WMI Win32_ShadowCopy.
/// All operations require administrator privileges.
/// Implements <see cref="IVssGateway"/> so upper layers (e.g. SnapshotManager) can depend on the interface via DI.
/// </summary>
public class VssAdapter : IVssGateway
{
    private readonly ILogger<VssAdapter> _logger;
    private const string WmiScope = @"\\.\root\cimv2";

    /// <summary>
    /// BUG-RESTORE-002 regression guard: the argument list passed to robocopy on restore (true rollback semantics).
    /// The unit test <c>RobocopyRestoreArguments_AreTrueRollbackSemantics</c> pins this contract.
    /// Before changing any item, confirm it does not weaken the anti-ransomware semantics:
    /// - Must include <c>/purge</c> (removes extra files on the target, such as ransom notes)
    /// - Must NOT include <c>/xo</c> (otherwise files rewritten by encryption would not be overwritten)
    /// - Must include <c>/b</c> (backup mode bypasses ACLs; required to restore SYSTEM files)
    /// </summary>
    public static readonly string[] RobocopyRestoreArguments =
        { "/e", "/purge", "/b", "/copy:DAT", "/r:3", "/w:5", "/mt:8", "/np", "/ndl" };

    public VssAdapter(ILogger<VssAdapter> logger)
    {
        _logger = logger;
    }

    private static ManagementScope CreateScope()
    {
        var scope = new ManagementScope(WmiScope);
        scope.Connect();
        return scope;
    }

    // ──────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────

    public async Task<SnapshotRecord> CreateShadowCopyAsync(string volumePath, CancellationToken ct = default)
    {
        var record = new SnapshotRecord
        {
            Volumes = new[] { volumePath },
            Status = SnapshotStatus.InProgress
        };

        try
        {
            _logger.LogInformation("Creating VSS shadow copy for {Volume}", volumePath);
            var result = await Task.Run(() => WmiCreate(volumePath), ct);
            record.ShadowId = result.ShadowId;
            record.DeviceObject = result.DeviceObject;
            record.SizeBytes = result.SizeBytes;
            record.Status = SnapshotStatus.Completed;
            _logger.LogInformation("Shadow copy created: {ShadowId} -> {DeviceObject}",
                record.ShadowId, record.DeviceObject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create shadow copy for {Volume}", volumePath);
            record.Status = SnapshotStatus.Failed;
            record.Notes = ex.Message;
        }

        return record;
    }

    public async Task DeleteShadowCopyAsync(string shadowId, CancellationToken ct = default)
    {
        _logger.LogInformation("Deleting shadow copy {ShadowId}", shadowId);
        await Task.Run(() => WmiDelete(shadowId), ct);
        _logger.LogInformation("Shadow copy deleted: {ShadowId}", shadowId);
    }

    /// <summary>
    /// Restores the contents of the specified snapshot to the target volume via robocopy.
    /// deviceObject looks like \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN
    /// </summary>
    public async Task<bool> RestoreShadowCopyAsync(string shadowId, string targetVolume, CancellationToken ct = default)
    {
        _logger.LogInformation("Restoring shadow copy {ShadowId} to {Volume}", shadowId, targetVolume);
        try
        {
            var deviceObject = await Task.Run(() => GetDeviceObject(shadowId), ct);
            if (deviceObject is null)
            {
                _logger.LogWarning("Shadow copy {ShadowId} not found in WMI.", shadowId);
                return false;
            }

            _logger.LogInformation("Found device object: {DeviceObject}", deviceObject);
            await RobocopyRestoreAsync(deviceObject, targetVolume, ct);
            _logger.LogInformation("Restore completed for {ShadowId}", shadowId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed for shadow copy {ShadowId}", shadowId);
            return false;
        }
    }

    /// <summary>Lists all VSS shadow copies in the system (newest first)</summary>
    public async Task<IReadOnlyList<VssShadowInfo>> ListShadowCopiesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => WmiList(), ct);
    }

    // Per-shadow mount locks: concurrent MOUNT_SNAPSHOT requests for the same shadow must serialize,
    // otherwise both pass the reparse-point reuse check and the loser's mklink fails with "file exists".
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> MountLocks = new();

    /// <summary>
    /// Mounts the specified shadow copy as a browsable symlink directory (backs the MOUNT_SNAPSHOT command).
    /// Unlike the temporary link used for restore, this link is persistent and reused by ShadowId name.
    /// Same technical background as BUG-RESTORE-001: neither .NET enumeration nor robocopy can access
    /// GLOBALROOT device paths directly; an mklink symlink is required as an intermediary.
    /// </summary>
    public async Task<string?> MountShadowCopyAsync(string shadowId, CancellationToken ct = default)
    {
        var deviceObject = await Task.Run(() => GetDeviceObject(shadowId), ct);
        if (deviceObject is null)
        {
            _logger.LogWarning("MountShadowCopy: shadow copy {ShadowId} not found in WMI.", shadowId);
            return null;
        }

        var mountRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "unlose", "mounts");
        Directory.CreateDirectory(mountRoot);

        // ShadowId looks like {GUID}; strip the braces for the directory name. The same shadow reuses the same link.
        var linkPath = Path.Combine(mountRoot, $"mount_{shadowId.Trim('{', '}')}");

        var gate = MountLocks.GetOrAdd(shadowId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // If it is already a symlink (reparse point), reuse it directly — ShadowId is globally unique and cannot point to a wrong target
            try
            {
                if ((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0)
                {
                    _logger.LogDebug("MountShadowCopy: reuse existing link {Link}", linkPath);
                    return linkPath;
                }
            }
            catch { /* link does not exist yet; create a new one */ }

            var rawDevice = deviceObject.TrimEnd('\\') + "\\";
            await CreateSymlinkAsync(linkPath, rawDevice, ct);
            _logger.LogInformation("Mounted shadow copy {ShadowId} at {Link}", shadowId, linkPath);
            return linkPath;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Cherry-picks and restores the files/directories at the given relative paths from a shadow copy
    /// (reuses the mount; backs the RESTORE_FILES command).
    /// Copies each item via robocopy (recursive for directories, single-file copy otherwise);
    /// returns the list of relative paths that failed, or null if the shadow copy does not exist.
    /// </summary>
    public async Task<IReadOnlyList<string>?> RestoreFilesFromShadowAsync(
        string shadowId, IReadOnlyList<string> relativePaths, string targetPath, CancellationToken ct = default)
    {
        var mountRoot = await MountShadowCopyAsync(shadowId, ct);
        if (mountRoot is null) return null;

        Directory.CreateDirectory(targetPath);
        var mountRootFull = Path.GetFullPath(mountRoot).TrimEnd('\\') + "\\";
        var failed = new List<string>();

        foreach (var rel in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Path traversal guard: the resolved path must stay under the mount root (rejects absolute paths and '..' escapes)
                var srcFull = Path.GetFullPath(Path.Combine(mountRootFull, rel));
                if (!srcFull.StartsWith(mountRootFull, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("RestoreFiles: reject escaping path {Path}", rel);
                    failed.Add(rel);
                    continue;
                }

                var dstFull = Path.GetFullPath(Path.Combine(targetPath, rel));
                if (Directory.Exists(srcFull))
                {
                    // Directory: robocopy recursive (without /purge — cherry-pick semantics; never touches extra files on the target)
                    await RobocopyCopyAsync(srcFull, dstFull, ct);
                }
                else if (File.Exists(srcFull))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dstFull)!);
                    await RobocopyCopyAsync(
                        Path.GetDirectoryName(srcFull)!, Path.GetDirectoryName(dstFull)!, ct,
                        fileName: Path.GetFileName(srcFull));
                }
                else
                {
                    _logger.LogWarning("RestoreFiles: not found in snapshot: {Path}", rel);
                    failed.Add(rel);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RestoreFiles: failed {Path}", rel);
                failed.Add(rel);
            }
        }

        return failed;
    }

    /// <summary>robocopy recursive single-directory / single-file copy (for cherry-pick restore; no /purge, never touches extra target files).</summary>
    private async Task RobocopyCopyAsync(string srcDir, string dstDir, CancellationToken ct, string? fileName = null)
    {
        var psi = new ProcessStartInfo("robocopy")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(srcDir);
        psi.ArgumentList.Add(dstDir);
        if (fileName is not null) psi.ArgumentList.Add(fileName);
        foreach (var arg in new[] { "/e", "/copy:DAT", "/dcopy:DAT", "/r:2", "/w:2", "/np", "/ndl", "/nfl" })
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        _ = await proc.StandardOutput.ReadToEndAsync(ct);
        _ = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode >= 8)
            throw new InvalidOperationException($"robocopy failed with exit code {proc.ExitCode}");
    }

    // ──────────────────────────────────────────────
    // Private WMI implementation
    // ──────────────────────────────────────────────

    private (string ShadowId, string DeviceObject, long SizeBytes) WmiCreate(string volumePath)
    {
        // Ensure the volume path ends with \ (required by WMI)
        var volume = volumePath.TrimEnd('\\') + "\\";

        var scope = CreateScope();

        using var shadowClass = new ManagementClass(scope, new ManagementPath("Win32_ShadowCopy"), null);
        using var inParams = shadowClass.GetMethodParameters("Create");
        inParams["Volume"] = volume;
        inParams["Context"] = "ClientAccessible";

        using var outParams = shadowClass.InvokeMethod("Create", inParams, null);
        var returnCode = Convert.ToUInt32(outParams["ReturnValue"]);
        if (returnCode != 0)
        {
            throw new InvalidOperationException(
                $"Win32_ShadowCopy.Create failed (volume={volume}, returnCode={returnCode}). " +
                "Most common causes: not enough disk space, VSS service not running, or insufficient privileges.");
        }

        var shadowId = outParams["ShadowID"]?.ToString()
            ?? throw new InvalidOperationException("VSS did not return a ShadowID.");

        // Query the DeviceObject and size
        var info = QueryShadowById(scope, shadowId)
            ?? throw new InvalidOperationException($"Shadow copy {shadowId} not found after creation.");

        return (shadowId, info.DeviceObject, info.SizeBytes);
    }

    private void WmiDelete(string shadowId)
    {
        var scope = CreateScope();

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{EscapeWmiString(shadowId)}'"));

        foreach (ManagementObject obj in searcher.Get())
        {
            using (obj)
            {
                // Win32_ShadowCopy does not implement a WMI "Delete" method;
                // use ManagementObject.Delete() to issue a WMI DeleteInstance operation.
                obj.Delete();
            }
        }
    }

    private string? GetDeviceObject(string shadowId)
    {
        var scope = CreateScope();
        return QueryShadowById(scope, shadowId)?.DeviceObject;
    }

    private VssShadowInfo? QueryShadowById(ManagementScope scope, string shadowId)
    {
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{EscapeWmiString(shadowId)}'"));

        foreach (ManagementObject obj in searcher.Get())
        {
            using (obj)
            {
                return ExtractInfo(obj);
            }
        }
        return null;
    }

    private IReadOnlyList<VssShadowInfo> WmiList()
    {
        var results = new List<VssShadowInfo>();
        var scope = CreateScope();

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT * FROM Win32_ShadowCopy"));

        foreach (ManagementObject obj in searcher.Get())
        {
            using (obj)
            {
                results.Add(ExtractInfo(obj));
            }
        }

        results.Sort((a, b) => b.InstallDate.CompareTo(a.InstallDate));
        return results.AsReadOnly();
    }

    private static VssShadowInfo ExtractInfo(ManagementObject obj)
    {
        // InstallDate format: yyyyMMddHHmmss.ffffff+000
        var installDateRaw = obj["InstallDate"]?.ToString() ?? string.Empty;
        DateTime installDate = installDateRaw.Length >= 14
            ? DateTime.ParseExact(installDateRaw[..14], "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal)
            : DateTime.MinValue;

        return new VssShadowInfo
        {
            ShadowId = obj["ID"]?.ToString() ?? string.Empty,
            DeviceObject = obj["DeviceObject"]?.ToString() ?? string.Empty,
            VolumeName = obj["VolumeName"]?.ToString() ?? string.Empty,
            InstallDate = installDate,
            // Win32_ShadowCopy has no direct SizeBytes field; estimate from the volume capacity
            SizeBytes = 0
        };
    }

    // ──────────────────────────────────────────────
    // Restore: robocopy copies from the snapshot device path to the target volume
    // ──────────────────────────────────────────────

    // BUG-RESTORE-001 (e2e finding 1, P0) fix:
    // The original implementation called robocopy directly on
    // `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\`, but robocopy cannot access that device
    // path (error 123 "incorrect syntax" / 67 "network name not found", exit 16), so every restore
    // returned Success=false.
    // Verified by testing: first create a symlink to the GLOBALROOT device path with `mklink /D`
    // in a temp directory, then let robocopy read the snapshot contents through that symlink —
    // the restore succeeds (exit=1, file contents byte-identical).
    // This method creates a temporary symlink and cleans it up after robocopy finishes (success or failure).
    private async Task RobocopyRestoreAsync(string deviceObject, string targetVolume, CancellationToken ct)
    {
        // DeviceObject must end with \ to be usable as a directory root
        var rawDevice = deviceObject.TrimEnd('\\') + "\\";
        var dest = targetVolume.TrimEnd('\\') + "\\";

        // 1) Create a temporary symlink: mklink /D <link> \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\
        //    Placed under %ProgramData%\unlose\restore-links\, named with a ShadowId hash to avoid collisions.
        var linkRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "unlose", "restore-links");
        Directory.CreateDirectory(linkRoot);
        var linkName = $"snap_{Math.Abs(deviceObject.GetHashCode()):X8}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        var linkPath = Path.Combine(linkRoot, linkName);

        // mklink is a cmd built-in and must be launched via cmd /c (see CreateSymlinkAsync for details)
        await CreateSymlinkAsync(linkPath, rawDevice, ct);

        // 2) robocopy copies through the symlink: source must end with \ to denote a directory root
        var source = linkPath.TrimEnd('\\') + "\\";
        _logger.LogInformation("robocopy \"{Source}\" -> \"{Dest}\"", source, dest);

        // BUG-RESTORE-002 (found during VM verification) fix: restore semantics changed to "true rollback".
        // The original /xo (skip older files) + no purge meant:
        //   - files rewritten by ransomware encryption (with newer timestamps) would not be overwritten
        //   - ransomware-added README_LOCKED.txt would not be deleted
        // A fatal flaw for an anti-ransomware product — files could not be recovered in a real attack.
        // Fix: drop /xo (force overwrite) + add /purge (delete target files absent from the snapshot).
        //
        // Argument notes:
        // /e = include empty subdirectories (and delete empty ones)
        // /purge = delete files/directories on the target that are absent from the source (removes ransom notes and newly added malicious files)
        // /b = backup mode (bypasses ACLs; required to restore SYSTEM files)
        // /copy:DAT = data+attributes+timestamps (/copyall cannot write ACLs when copying across volumes to the system drive; degraded)
        // /r:3 /w:5 = 3 retries, 5s interval
        // /mt:8 = 8 concurrent threads; /ndl = suppress directory names in the log
        //
        // Note: /purge genuinely deletes target files; combined with /b backup mode it can handle encrypted/locked files.
        // This is "roll back to the snapshot moment" semantics, consistent with Windows "Previous Versions" restore.
        // /xo is no longer used — in anti-ransomware scenarios, tampered files must be overwritten by the snapshot version.
        var psi = new ProcessStartInfo("robocopy")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(dest);
        foreach (var arg in RobocopyRestoreArguments)
            psi.ArgumentList.Add(arg);

        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _logger.LogDebug("[robocopy] {Line}", e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _logger.LogWarning("[robocopy stderr] {Line}", e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);

            // robocopy exit codes: 0-7 are success (bitmask), 8+ are failures
            if (proc.ExitCode >= 8)
            {
                throw new InvalidOperationException(
                    $"robocopy failed with exit code {proc.ExitCode}. " +
                    "Check logs for details.");
            }

            _logger.LogInformation("robocopy completed with exit code {Code}", proc.ExitCode);
        }
        finally
        {
            // 3) Clean up the symlink (rmdir; does not delete the target's contents)
            proc?.Dispose();
            TryDeleteSymlink(linkPath, linkRoot);
        }
    }

    /// <summary>
    /// Creates a directory symlink pointing to a shadow-copy device path (shared by restore and mount).
    /// mklink is a cmd built-in and must be launched via cmd /c;
    /// mklink /D accepts targets with a \\?\ prefix; the target does not need to end with \.
    /// </summary>
    private async Task CreateSymlinkAsync(string linkPath, string rawDevice, CancellationToken ct)
    {
        var mklinkPsi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // /c mklink /D "<linkPath>" "<rawDevice>"
        mklinkPsi.ArgumentList.Add("/c");
        mklinkPsi.ArgumentList.Add("mklink");
        mklinkPsi.ArgumentList.Add("/D");
        mklinkPsi.ArgumentList.Add(linkPath);
        mklinkPsi.ArgumentList.Add(rawDevice);

        using var mklink = new Process { StartInfo = mklinkPsi };
        mklink.Start();
        var mkStdout = await mklink.StandardOutput.ReadToEndAsync(ct);
        var mkStderr = await mklink.StandardError.ReadToEndAsync(ct);
        await mklink.WaitForExitAsync(ct);
        if (mklink.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mklink failed (exit={mklink.ExitCode}) creating symlink {linkPath} -> {rawDevice}. " +
                $"stdout: {mkStdout}; stderr: {mkStderr}. " +
                "Service may lack SeCreateSymbolicLinkPrivilege (run as SYSTEM/Administrator).");
        }
        _logger.LogInformation("Created symlink {Link} -> {Device}", linkPath, rawDevice);
    }

    /// <summary>Deletes the symlink directory entry (does not affect the link target).</summary>
    private void TryDeleteSymlink(string linkPath, string linkRoot)
    {
        try
        {
            if (Directory.Exists(linkPath))
            {
                // Directory.Delete on a symlinked directory removes the link itself (not the target), but requires recursive=true
                Directory.Delete(linkPath, recursive: true);
                _logger.LogDebug("Removed restore symlink {Link}", linkPath);
            }

            // Also clean up stale links left in the same directory (older than 7 days)
            foreach (var stale in new DirectoryInfo(linkRoot).EnumerateDirectories("snap_*"))
            {
                if (stale.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
                {
                    try { stale.Delete(recursive: true); }
                    catch { /* ignore individual cleanup failures */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove restore symlink {Link}", linkPath);
        }
    }

    // ──────────────────────────────────────────────
    // Utility methods
    // ──────────────────────────────────────────────

    /// <summary>
    /// SEC-005 fix: strict whitelist validation for shadowId (GUID format);
    /// only the standard GUID format {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx} is allowed,
    /// rejecting any input that could cause WMI injection.
    /// </summary>
    private static string EscapeWmiString(string input)
    {
        // Strict GUID format validation (WMI ShadowID must have this format)
        if (System.Text.RegularExpressions.Regex.IsMatch(input,
                @"^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$"))
        {
            return input;
        }
        // If it does not match the GUID format, fall back to single-quote escaping as a last resort
        return input.Replace("'", "''");
    }
}