using Unlose.Core.Enums;
using Unlose.Core.Models;

namespace Unlose.Core.Ipc;

// ── Snapshot operations ──────────────────────────────────────────────────────────────────
public record CreateSnapshotRequest(string[] Volumes, TriggerType TriggerType, string? Label = null);
public record CreateSnapshotResponse(bool Success, SnapshotRecord? Snapshot, string? ErrorMessage = null);

public record ListSnapshotsRequest(TriggerType? FilterByType = null);
public record ListSnapshotsResponse(SnapshotRecord[] Snapshots);

public record DeleteSnapshotRequest(Guid SnapshotId);
public record DeleteSnapshotResponse(bool Success, string? ErrorMessage = null);

public record SetSnapshotPinnedRequest(Guid SnapshotId, bool IsPinned);
public record SetSnapshotPinnedResponse(bool Success, string? ErrorMessage = null);

// ── Restore operations ──────────────────────────────────────────────────────────────────
public record MountSnapshotRequest(Guid SnapshotId);
public record MountSnapshotResponse(bool Success, string? MountPath, string? ErrorMessage = null);

public record UnmountSnapshotRequest(Guid SnapshotId);
public record UnmountSnapshotResponse(bool Success);

public record RestoreFileRequest(Guid SnapshotId, string SourcePath, string TargetPath);
public record RestoreFileResponse(bool Success, string? ErrorMessage = null);

public record RestoreFileTreeRequest(
    Guid SnapshotId,
    string? CancellationId = null,
    bool ForceWithoutPreRestore = false);

public record RestoreFileTreeResponse(
    bool Accepted,
    bool ForceConfirmRequired = false,
    Guid? PreRestoreSnapshotId = null,
    string? ErrorMessage = null);

public record CancelRestoreTreeRequest(string CancellationId);
public record CancelRestoreTreeResponse(bool Success);

// ── Monitoring and sessions ────────────────────────────────────────────────────────────────
public record ListMonitorEventsRequest(
    DateTime? From = null,
    DateTime? To = null,
    string? EventType = null,
    int MaxResults = 100);
public record ListMonitorEventsResponse(MonitorEventRecord[] Events);

public record GetAgentSessionsRequest(bool ActiveOnly = false);
public record GetAgentSessionsResponse(AgentSessionRecord[] Sessions);

public record SuspendProcessRequest(int Pid);
public record SuspendProcessResponse(bool Success, string? ErrorMessage = null);

public record ResumeProcessRequest(int Pid);
public record ResumeProcessResponse(bool Success, string? ErrorMessage = null);

// ── Configuration ──────────────────────────────────────────────────────────────────────
public record GetConfigRequest();
public record GetConfigResponse(object Config);

public record SetConfigRequest(object Config);
public record SetConfigResponse(bool Success);

// ── System restore points ────────────────────────────────────────────────────────────────
public record ListSystemRestorePointsRequest();
public record ListSystemRestorePointsResponse(SystemRestorePointInfo[] Points);

public record CreateSystemRestorePointRequest(string? Description = null);
public record CreateSystemRestorePointResponse(bool Success, SystemRestorePointInfo? Point = null, string? ErrorMessage = null);

public record RestoreSystemRestorePointRequest(int SequenceNumber);
public record RestoreSystemRestorePointResponse(bool Success, bool PendingReboot = true, string? ErrorMessage = null);

// ── Protection pause/resume ──────────────────────────────────────────────────────────────
public record PauseProtectionRequest(PauseDuration? Duration = null);
public record PauseProtectionResponse(bool Success, DateTime? ResumesAt = null);

public record ResumeProtectionRequest();
public record ResumeProtectionResponse(bool Success);

public record GetProtectionStateRequest();
public record GetProtectionStateResponse(bool IsPaused, DateTime? ResumesAt, string? PausedBy);

// ── Diagnostics ──────────────────────────────────────────────────────────────────────
public record RunDiagnosticsRequest();
public record RunDiagnosticsResponse(DiagnosticsReport Report);

// ── Push notifications (Service → connected clients) ────────────────────────────────────────
public record SnapshotCreatedNotification(SnapshotRecord Snapshot) : IUnloseEvent;
public record SnapshotFailedNotification(TriggerType TriggerType, string Reason, int RetryCount) : IUnloseEvent;
public record DangerCommandAlertNotification(
    string ProcessName,
    int Pid,
    string Command,
    string RuleId,
    string RuleName,
    DangerSeverity Severity,
    bool HasSessionSnapshot,
    Guid? SessionSnapshotId,
    Guid? LastAvailableSnapshotId) : IUnloseEvent;
public record SecurityAlertNotification(
    Guid AlertId,
    DateTime OccurredAt,
    ThreatType ThreatType,
    AlertSeverity Severity,
    string Description,
    bool Acknowledged) : IUnloseEvent;
public record AgentSessionStartedNotification(Guid SessionId, string ProcessName, bool SnapshotCreated, bool IsUnprotected) : IUnloseEvent;
public record AgentSessionEndedNotification(Guid SessionId, string ProcessName) : IUnloseEvent;
public record StorageLowNotification(string Volume, double FreeGb, double ThresholdGb) : IUnloseEvent;
public record ServiceHeartbeatNotification(DateTime Timestamp, int Pid) : IUnloseEvent;
public record ProtectionStateChangedNotification(bool IsPaused, DateTime? ResumesAt) : IUnloseEvent;
public record OperationRequiresRebootNotification(string OperationType, string Message) : IUnloseEvent;
public record RestoreTreeProgressNotification(int TotalFiles, int CompletedFiles, int FailedFiles, bool IsComplete) : IUnloseEvent;
