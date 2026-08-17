using Unlose.Core.Models;

namespace Unlose.Core.Interfaces;

/// <summary>
/// System restore point gateway interface (testability refactor ARCH-testability).
/// Abstracts WMI SystemRestore create/list/restore so upper layers (CommandDispatcher)
/// depend on the interface via DI, without real admin privileges or WMI.
/// </summary>
public interface ISystemRestoreGateway
{
    /// <summary>
    /// Creates an application-install restore point on the system volume.
    /// Returns a <see cref="RestorePointResult"/> with a success flag and (on failure) diagnostic information.
    /// Returning a result object instead of bool surfaces the root cause of Windows SR's
    /// "false success" (quota exhaustion / throttling) to upper layers and end users,
    /// instead of a generic "check privileges".
    /// </summary>
    Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken ct = default);

    /// <summary>Lists all restore points currently available on the system</summary>
    Task<IReadOnlyList<SystemRestorePointInfo>> ListRestorePointsAsync(CancellationToken ct = default);

    /// <summary>
    /// Performs a system restore (restores the system to the restore point with the given sequence number).
    /// This operation requires a reboot to take effect; returning true means the operation has been initiated and is awaiting reboot.
    /// </summary>
    Task<bool> RestoreToPointAsync(int sequenceNumber, CancellationToken ct = default);
}

/// <summary>
/// Result of creating a system restore point.
/// DIAG-SRP-001: when Windows SR silently fails due to quota/throttling, <see cref="DiagnosticMessage"/>
/// carries actionable diagnostics (VSS quota usage, throttle interval, suggested action) so users know how to fix it.
/// </summary>
public sealed record RestorePointResult
{
    /// <summary>Whether the restore point was truly created (confirmed persisted via cross-validation).</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Human-readable diagnostic message on failure (null on success).
    /// Example: "VSS shadow storage 93% full (9.33GB/10GB); SystemRestorePointCreationFrequency=1min.
    ///        Likely cause: quota near full. Suggest: vssadmin resize shadowstorage /for=C: /on=C: /maxsize=20GB"
    /// </summary>
    public string? DiagnosticMessage { get; init; }

    /// <summary>Convenience factory for success.</summary>
    public static RestorePointResult Ok() => new() { Success = true };

    /// <summary>Convenience factory for failure.</summary>
    public static RestorePointResult Fail(string? diagnostic = null) =>
        new() { Success = false, DiagnosticMessage = diagnostic };
}
