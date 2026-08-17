using System.Text.Json.Serialization;

namespace Unlose.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PauseDuration
{
    ThirtyMinutes,
    OneHour,
    UntilReboot
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DangerSeverity
{
    High,
    Critical,
    /// <summary>Info-level event (for persisting to monitor_events, e.g. SnapshotCreated / ProtectionStateChanged)</summary>
    Info
}
