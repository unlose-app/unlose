using Unlose.Core.Enums;

namespace Unlose.Core.Models;

/// <summary>Marker interface for all domain events</summary>
public interface IUnloseEvent { }

/// <summary>Agent session started event</summary>
public record AgentSessionStartedEvent(
    AgentSessionRecord Session,
    bool SnapshotCreated,
    bool IsUnprotected) : IUnloseEvent;

/// <summary>Agent session ended event</summary>
public record AgentSessionEndedEvent(
    AgentSessionRecord Session) : IUnloseEvent;

/// <summary>Snapshot created successfully event</summary>
public record SnapshotCreatedEvent(
    SnapshotRecord Snapshot) : IUnloseEvent;

/// <summary>Snapshot creation failed event</summary>
public record SnapshotFailedEvent(
    TriggerType TriggerType,
    string Reason,
    int RetryCount) : IUnloseEvent;

/// <summary>Low storage space event</summary>
public record StorageLowEvent(
    string Volume,
    double FreeGb,
    double ThresholdGb) : IUnloseEvent;

/// <summary>Protection state changed event</summary>
public record ProtectionStateChangedEvent(
    bool IsPaused,
    DateTime? ResumesAt) : IUnloseEvent;

/// <summary>Generic security alert event (preserves the original ThreatType / Severity semantics)</summary>
public record AlertRaisedEvent(
    AlertRecord Alert) : IUnloseEvent;

