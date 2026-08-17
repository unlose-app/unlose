using System.Text.Json.Serialization;
using Unlose.Core.Enums;

namespace Unlose.Core.Models;

/// <summary>Full snapshot record, used for database persistence and API transport</summary>
public class SnapshotRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>List of volumes covered by the snapshot</summary>
    public string[] Volumes { get; set; } = [];
    /// <summary>Backward compatibility: returns the first volume path</summary>
    [JsonIgnore]
    public string VolumePath => Volumes.Length > 0 ? Volumes[0] : string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public TriggerType TriggerType { get; set; }
    public string? TriggerDetail { get; set; }
    public string? Label { get; set; }
    /// <summary>Session context identifier carried by the caller (passed through when track-B agents trigger via CLI/MCP; null on track A)</summary>
    public string? SessionId { get; set; }
    public string? ShadowId { get; set; }
    public string? DeviceObject { get; set; }
    public SnapshotStatus Status { get; set; } = SnapshotStatus.Completed;
    public long SizeBytes { get; set; }
    public string? IntegrityHash { get; set; }
    public bool IsPinned { get; set; }
    public string? Notes { get; set; }
}
