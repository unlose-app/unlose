using Unlose.Core.Enums;

namespace Unlose.Core.Models;

/// <summary>Agent session record</summary>
public class AgentSessionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProcessName { get; set; } = string.Empty;
    public int Pid { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public Guid? PreSessionSnapshotId { get; set; }
    public bool IsSnapshotCreated => PreSessionSnapshotId.HasValue;
}

/// <summary>Monitor event record (persisted to the database)</summary>
public class MonitorEventRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public string EventType { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string? CommandLine { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? RuleId { get; set; }
    public DangerSeverity? Severity { get; set; }
}

/// <summary>System restore point information</summary>
public class SystemRestorePointInfo
{
    public int SequenceNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    /// <summary>WMI SystemRestore.RestorePointType (integer enum, e.g. 0=APPLICATION_INSTALL)</summary>
    public int RestorePointType { get; set; }
}

/// <summary>Protection state</summary>
public class ProtectionState
{
    public bool IsActive { get; set; } = true;
    public DateTime? PausedUntil { get; set; }
    public string? PausedBy { get; set; }
}

/// <summary>Dangerous command rule</summary>
public class DangerRule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DangerSeverity Severity { get; set; }
    public string[] Patterns { get; set; } = [];
    public string[] AppliesTo { get; set; } = [];
    public string Description { get; set; } = string.Empty;
}

/// <summary>Diagnostics report</summary>
public class DiagnosticsReport
{
    public string VssServiceStatus { get; set; } = string.Empty;
    public bool HasNtfsVolume { get; set; }
    public bool HasSeBackupPrivilege { get; set; }
    public double FreeDiskSpaceGb { get; set; }
    public string[] Issues { get; set; } = [];
}

/// <summary>Storage space information</summary>
public class StorageInfo
{
    public string Volume { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBySnapshotsBytes { get; set; }
}
