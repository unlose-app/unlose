using System.IO;
using System.Linq;

namespace Unlose.Core.Config;

public class UnloseConfig
{
    public SnapshotConfig Snapshot { get; set; } = new();
    public ServiceConfig Service { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
}

public class SnapshotConfig
{
    public string[] Volumes { get; set; } = ["C:\\"];
    /// <summary>
    /// Fixed-time automatic snapshot times (local time, "HH:mm"); when non-empty, takes precedence over IntervalHours.
    /// Defaults correspond to the start of three working periods: 8 AM, 1 PM, and 6 PM.
    /// </summary>
    public string[] ScheduleTimes { get; set; } = ["08:00", "13:00", "18:00"];
    /// <summary>Automatic snapshot interval (hours); 0 = manual only. Only takes effect when ScheduleTimes is empty (legacy mode)</summary>
    public int IntervalHours { get; set; } = 24;
    public int MaxCount { get; set; } = 10;
    public int RetentionDays { get; set; } = 30;
    /// <summary>
    /// Maximum number of non-pinned snapshots to keep from the last 24 hours (RetentionPolicyEngine tier-① threshold).
    /// Default 30: in high-frequency agent session scenarios (deepcode/ZCode create a snapshot before every session),
    /// a cap of 8 would churn through within an hour and users would perceive "snapshots mysteriously disappearing".
    /// 30 balances protection density against disk usage. Adjustable on the settings page.
    /// </summary>
    public int Retention24hCount { get; set; } = 30;
    /// <summary>Low-storage warning threshold (GB)</summary>
    public double StorageThresholdGb { get; set; } = 2.0;
    /// <summary>
    /// Tray balloon notification level: "all" (notify everything, default) / "failures-only" (notify on failures only) / "silent" (completely silent).
    /// Consumed only by the UI side (the service does not read it); real-time refresh of the snapshot list is not affected by this switch.
    /// </summary>
    public string NotificationLevel { get; set; } = "all";
    /// <summary>Maximum disk space snapshots may occupy (GB); 0 = unlimited</summary>
    public double MaxStorageGb { get; set; } = 0;
    /// <summary>
    /// Master switch for full-volume in-place restore. Default off: the operation force-overwrites
    /// and purges a live volume, so it is reserved for advanced recovery (e.g. ransomware
    /// mass-encryption on a data drive). The system volume is always refused regardless of this
    /// switch. Enforced service-side (UI toggle only writes this flag), so CLI/MCP cannot bypass it.
    /// </summary>
    public bool EnableInPlaceVolumeRestore { get; set; } = false;
}

public class ServiceConfig
{
    public string PipeName { get; set; } = "unlosePipe";
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public string LogLevel { get; set; } = "Information";
    /// <summary>Allowlist of signing certificate thumbprints (SHA1) for clients permitted to connect to the pipe</summary>
    public string[] TrustedClientThumbprints { get; set; } = [];
    /// <summary>
    /// When true: if TrustedClientThumbprints is not configured or a lenient policy is in effect, any local pipe client is accepted (including unsigned ones, to support default installs and dev builds).
    /// For enterprise deployment, set this to false and configure TrustedClientThumbprints.
    /// </summary>
    public bool AllowAnySignedClientInProduction { get; set; } = true;
}

public class AgentConfig
{
    /// <summary>List of monitored AI agent process names</summary>
    public string[] MonitoredProcesses { get; set; } =
    [
        "claude.exe",
        "cursor.exe",
        "code.exe",
        "windsurf.exe",
        "gemini-cli.exe",
        "gemini.exe",
        "aider.exe",
        "openclaw.exe",
        "opencode.exe",
        "zcode.exe",
        "trae.exe",
        "qoder.exe",
        "qodercn.exe",
        "codex.exe",
        "workbuddy.exe",
        // ── Expanded based on the 2026-07 survey ──
        "kimi.exe",
        "qwen.exe",
        "vibe.exe",
        "amp.exe",
        "copilot.exe",
        "crush.exe",
        "codebuddy.exe",
        "kiro.exe",
        "antigravity.exe",
        "kilocode.exe",
        "auggie.exe",
        "deepcode.exe"
    ];
    /// <summary>Session heartbeat polling interval (seconds)</summary>
    public int SessionPollIntervalSeconds { get; set; } = 5;
    /// <summary>Minimum interval (minutes) between two pre-session snapshots for the same process name; default 10 minutes</summary>
    public int AgentSnapshotCooldownMinutes { get; set; } = 10;
}

