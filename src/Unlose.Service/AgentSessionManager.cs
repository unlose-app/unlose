using System.Diagnostics;
using System.Collections.Concurrent;
using Unlose.Core.Config;
using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

/// <summary>
/// Agent session awareness manager.
/// Scans the process list every N seconds to detect monitored Agent processes starting/exiting.
/// On session start: triggers an agent_pre_session snapshot and publishes AgentSessionStartedEvent.
/// On session end: publishes AgentSessionEndedEvent and records it to the DB.
/// </summary>
public sealed class AgentSessionManager : BackgroundService
{
    private readonly ILogger<AgentSessionManager> _logger;
    private readonly EventBus _bus;
    private readonly UnloseConfig _config;
    private readonly SnapshotManager _snapshotManager;
    private readonly Core.Data.SqliteRepository _repo;
    private readonly IAuditService _auditService;

    /// <summary>Currently active sessions: Key = PID</summary>
    private readonly ConcurrentDictionary<int, AgentSessionRecord> _activeSessions = new();

    /// <summary>Last pre-session snapshot creation time per process name, for cooldown dedup</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastSnapshotTimeByName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Minimum interval between two pre-session snapshots of the same process name (read from config)</summary>
    private TimeSpan AgentSnapshotCooldown => TimeSpan.FromMinutes(_config.Agent.AgentSnapshotCooldownMinutes);

    /// <summary>Last audit-log write time per process name, avoiding high-frequency duplicate entries for the same session</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastAuditLogByName =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan AuditLogCooldown = TimeSpan.FromHours(1);

    public AgentSessionManager(
        ILogger<AgentSessionManager> logger,
        EventBus bus,
        UnloseConfig config,
        SnapshotManager snapshotManager,
        Core.Data.SqliteRepository repo,
        IAuditService auditService)
    {
        _logger = logger;
        _bus = bus;
        _config = config;
        _snapshotManager = snapshotManager;
        _repo = repo;
        _auditService = auditService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentSessionManager started, monitoring {Count} process patterns",
            _config.Agent.MonitoredProcesses.Length);

        // Recover active sessions from the previous run and clean up zombie sessions of exited processes
        await RecoverSessionStateAsync(stoppingToken);

        var interval = TimeSpan.FromSeconds(_config.Agent.SessionPollIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ScanProcessesAsync(stoppingToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "AgentSessionManager scan error"); }
        }
    }

    private async Task ScanProcessesAsync(CancellationToken ct)
    {
        var allProcs = Process.GetProcesses();
        var currentPids = new HashSet<int>();
        // PID-reuse defense: a PID now occupied by a different Agent process means the old session should end
        var reusedPids = new HashSet<int>();
        // Command-line index (needed for Agents hosted by runtimes like node; skipped at zero cost when no command-line patterns exist)
        var cmdLines = Core.Agents.AgentRegistry.HasCommandLinePatterns ? TryGetAllCommandLines() : null;

        foreach (var proc in allProcs)
        {
            try
            {
                var name = proc.ProcessName + ".exe";
                if (!IsProcessNameMonitored(proc.ProcessName))
                {
                    // Process name did not match → try command-line matching (e.g. deepcode-cli hosted by node.exe)
                    if (cmdLines is not null && cmdLines.TryGetValue(proc.Id, out var cl))
                    {
                        var resolved = Core.Agents.AgentRegistry.ResolveProcessNameByCommandLine(cl);
                        if (resolved is null) continue;
                        name = resolved;   // register the session under the canonical Agent process name ("deepcode.exe")
                        _logger.LogDebug("Process {Pid} resolved by command line to {Name}", proc.Id, name);
                    }
                    else continue;
                }

                currentPids.Add(proc.Id);

                if (!_activeSessions.ContainsKey(proc.Id))
                {
                    // Newly detected Agent process
                    await OnSessionStartedAsync(proc, name, ct);
                }
                else if (_activeSessions.TryGetValue(proc.Id, out var existing)
                         && !existing.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    // The same PID is now occupied by another process name (Windows recycles PIDs) → the old session has exited
                    reusedPids.Add(proc.Id);
                }
            }
            catch { /* accessing some processes may throw; ignore */ }
        }

        // Detect exited processes (including old sessions displaced by PID reuse)
        var ended = _activeSessions.Keys.Except(currentPids).Union(reusedPids).ToList();
        foreach (var pid in ended)
        {
            if (_activeSessions.TryRemove(pid, out var session))
                await OnSessionEndedAsync(session, ct);
        }

        // Active sessions exist but no protection snapshot recently → auto-create one
        await EnsureActiveSessionsProtectedAsync(ct);
    }

    /// <summary>Time of the last auto-fill attempt (success or failure); avoids retrying on every scan after a failure</summary>
    private DateTime _lastAutoFillAttemptUtc = DateTime.MinValue;

    /// <summary>Retry interval after a failed auto-fill attempt</summary>
    private static readonly TimeSpan AutoFillRetryInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// When Agent sessions are active but no snapshot (of any trigger type) exists within the latest
    /// cooldown window, automatically create an agent_pre_session snapshot — turning the UI
    /// "create a snapshot now" warning into a service self-healing action.
    /// </summary>
    private async Task EnsureActiveSessionsProtectedAsync(CancellationToken ct)
    {
        if (_activeSessions.IsEmpty) return;

        var now = DateTime.UtcNow;
        var snapshots = await _snapshotManager.ListAsync(null, ct);
        var recentProtected = snapshots.Any(s => (now - s.CreatedAt) < AgentSnapshotCooldown);
        if (recentProtected) return;

        // Failure-retry throttling (on success the new snapshot itself satisfies recentProtected, so no repeated auto-fill)
        if ((now - _lastAutoFillAttemptUtc) < AutoFillRetryInterval) return;
        _lastAutoFillAttemptUtc = now;

        var session = _activeSessions.Values.OrderBy(s => s.StartedAt).First();
        _logger.LogWarning(
            "Active agent session {Process} (PID={Pid}) has no protection snapshot within {Cooldown}, auto-creating one",
            session.ProcessName, session.Pid, AgentSnapshotCooldown);

        var snapshot = await _snapshotManager.CreateAsync(
            TriggerType.AgentPreSession,
            triggerDetail: $"{session.ProcessName} (auto-fill: 无保护快照)",
            ct: ct);

        if (snapshot != null)
        {
            _lastSnapshotTimeByName[session.ProcessName] = now;
            _logger.LogInformation("Auto-fill protection snapshot created: {Id} for {Process}",
                snapshot.Id, session.ProcessName);
        }
    }

    private async Task OnSessionStartedAsync(Process proc, string processName, CancellationToken ct)
    {
        _logger.LogInformation("Agent session detected: {Process} (PID={Pid})", processName, proc.Id);

        var session = new AgentSessionRecord
        {
            ProcessName = processName,
            Pid = proc.Id,
            StartedAt = DateTime.UtcNow
        };
        _activeSessions.TryAdd(proc.Id, session);

        // Create an agent_pre_session snapshot (skipped within the same process name's cooldown window)
        bool withinCooldown = _lastSnapshotTimeByName.TryGetValue(processName, out var lastSnapshotTime)
            && (DateTime.UtcNow - lastSnapshotTime) < AgentSnapshotCooldown;

        SnapshotRecord? snapshot = null;
        bool snapshotCreated = false;

        if (!withinCooldown)
        {
            snapshot = await _snapshotManager.CreateAsync(
                TriggerType.AgentPreSession,
                triggerDetail: $"{processName} (PID={proc.Id})",
                ct: ct);
            snapshotCreated = snapshot != null;
            if (snapshotCreated && snapshot != null)
            {
                session.PreSessionSnapshotId = snapshot.Id;
                _lastSnapshotTimeByName[processName] = DateTime.UtcNow;
            }
        }
        else
        {
            _logger.LogDebug(
                "AgentPreSession snapshot suppressed for {Process}: cooldown active (last={Last:HH:mm:ss}, remaining={Remaining:mm\\:ss})",
                processName, lastSnapshotTime,
                AgentSnapshotCooldown - (DateTime.UtcNow - lastSnapshotTime));
        }

        // Persist the session to the database
        await PersistSessionAsync(session, ct);

        // Cooldown dedup for audit logging: no duplicate writes for the same process name within 1 hour
        bool logWithinCooldown = _lastAuditLogByName.TryGetValue(processName, out var lastLogTime)
            && (DateTime.UtcNow - lastLogTime) < AuditLogCooldown;

        if (!logWithinCooldown)
        {
            await _auditService.LogAsync(new AuditEntry
            {
                Action  = "AgentSessionStarted",
                Actor   = processName,
                Details = $"PID={proc.Id} snapshot={(snapshotCreated && session.PreSessionSnapshotId.HasValue ? session.PreSessionSnapshotId.ToString() : "none")}",
                Success = true
            }, ct);
            _lastAuditLogByName[processName] = DateTime.UtcNow;
        }

        _bus.Publish(new AgentSessionStartedEvent(
            Session: session,
            SnapshotCreated: snapshotCreated,
            IsUnprotected: !snapshotCreated));
    }

    private async Task OnSessionEndedAsync(AgentSessionRecord session, CancellationToken ct)
    {
        _logger.LogInformation("Agent session ended: {Process} (PID={Pid})", session.ProcessName, session.Pid);
        session.EndedAt = DateTime.UtcNow;
        await PersistSessionAsync(session, ct);

        // Write the audit log directly (does not rely on EventBus, avoiding loss from single-reader contention)
        await _auditService.LogAsync(new AuditEntry
        {
            Action  = "AgentSessionEnded",
            Actor   = session.ProcessName,
            Details = $"PID={session.Pid} duration={(session.EndedAt - session.StartedAt)?.TotalMinutes:F1}min",
            Success = true
        }, ct);

        _bus.Publish(new AgentSessionEndedEvent(session));
    }

    private async Task PersistSessionAsync(AgentSessionRecord session, CancellationToken ct)
    {
        // Save to the agent_sessions database table
        var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={GetDbPath()}");
        try
        {
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_sessions (id, process_name, pid, started_at, ended_at, pre_session_snapshot)
                VALUES (@id, @pn, @pi, @sa, @ea, @ss)
                ON CONFLICT(id) DO UPDATE SET ended_at=excluded.ended_at;
                """;
            cmd.Parameters.AddWithValue("@id", session.Id.ToString());
            cmd.Parameters.AddWithValue("@pn", session.ProcessName);
            cmd.Parameters.AddWithValue("@pi", session.Pid);
            cmd.Parameters.AddWithValue("@sa", session.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@ea", session.EndedAt.HasValue ? session.EndedAt.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("@ss", session.PreSessionSnapshotId.HasValue ? session.PreSessionSnapshotId.Value.ToString() : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to persist agent session"); }
        finally { await conn.DisposeAsync(); }
    }

    // Matches by process name only (config ∪ AgentRegistry built-in catalog); command-line-hosted Agents are matched separately in ScanProcessesAsync
    private bool IsProcessNameMonitored(string processName) =>
        // Union: config.json settings ∪ AgentRegistry built-in catalog (newly added agents take effect with no configuration)
        _config.Agent.MonitoredProcesses.Any(m =>
            m.Equals(processName + ".exe", StringComparison.OrdinalIgnoreCase) ||
            m.Equals(processName, StringComparison.OrdinalIgnoreCase)) ||
        Core.Agents.AgentRegistry.AllProcessNames.Any(m =>
            m.Equals(processName + ".exe", StringComparison.OrdinalIgnoreCase) ||
            m.Equals(processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Queries all process command lines at once (PID → CommandLine); returns null when WMI is unavailable (degrades to process-name-only matching)</summary>
    private Dictionary<int, string>? TryGetAllCommandLines()
    {
        try
        {
            var map = new Dictionary<int, string>();
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process");
            foreach (var obj in searcher.Get())
            {
                var pid = Convert.ToInt32(obj["ProcessId"]);
                if (obj["CommandLine"] is string cl && !string.IsNullOrWhiteSpace(cl))
                    map[pid] = cl;
            }
            return map;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI command-line query failed, falling back to process-name matching only");
            return null;   // fall back to process-name matching when WMI is unavailable (permissions/service failure)
        }
    }

    /// <summary>
    /// On service startup: restores the previously active sessions from the database into memory and marks
    /// sessions of exited processes as ended.
    /// Prevents duplicate agent_pre_session snapshots for the same process after a service restart and
    /// ensures ended_at is written correctly.
    /// </summary>
    private async Task RecoverSessionStateAsync(CancellationToken ct)
    {
        try
        {
            var dbPath = GetDbPath();
            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync(ct);

            // Query all sessions whose ended_at is NULL
            await using var selectCmd = conn.CreateCommand();
            selectCmd.CommandText = "SELECT id, process_name, pid, started_at, pre_session_snapshot FROM agent_sessions WHERE ended_at IS NULL;";
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);

            // Command-line index: needed to restore sessions hosted by runtimes like node (e.g. deepcode-cli)
            var cmdLines = Core.Agents.AgentRegistry.HasCommandLinePatterns ? TryGetAllCommandLines() : null;
            var staleIds = new List<string>();

            while (await reader.ReadAsync(ct))
            {
                var id          = Guid.Parse(reader.GetString(0));
                var processName = reader.GetString(1);
                var pid         = reader.GetInt32(2);
                var startedAt   = DateTime.Parse(reader.GetString(3));
                var preSnap     = reader.IsDBNull(4) ? (Guid?)null : Guid.Parse(reader.GetString(4));

                // PID-reuse defense: the process must be running, identity must match, and its start time
                // must not be later than the session start (otherwise the PID has been taken over by a
                // process started later and the old session exited long ago — Windows recycles recently freed PIDs)
                // Identity match: process name matches (note ProcessName excludes .exe), or command-line pattern matches (node-hosted type)
                var proc = GetProcessByIdSafe(pid);
                var nameMatches = proc is not null
                    && (proc.ProcessName + ".exe").Equals(processName, StringComparison.OrdinalIgnoreCase);
                var cmdMatches = proc is not null && !nameMatches && cmdLines is not null
                    && cmdLines.TryGetValue(pid, out var cl)
                    && Core.Agents.AgentRegistry.ResolveProcessNameByCommandLine(cl)
                        ?.Equals(processName, StringComparison.OrdinalIgnoreCase) == true;
                var pidReused = proc is null
                    || !(nameMatches || cmdMatches)
                    || SafeStartTimeUtc(proc) > startedAt.AddMinutes(2);

                if (!pidReused)
                {
                    // Process still running: restore to memory so the next scan does not trigger snapshot creation again
                    var session = new AgentSessionRecord
                    {
                        Id = id, ProcessName = processName, Pid = pid,
                        StartedAt = startedAt, PreSessionSnapshotId = preSnap
                    };
                    _activeSessions.TryAdd(pid, session);
                    _lastSnapshotTimeByName[processName] = DateTime.UtcNow;
                    _logger.LogInformation("Restored active session: {Process} (PID={Pid})", processName, pid);
                }
                else
                {
                    staleIds.Add(id.ToString());
                }
            }
            await reader.DisposeAsync();

            // Batch-update ended_at for zombie sessions
            var now = DateTime.UtcNow.ToString("O");
            foreach (var id in staleIds)
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = "UPDATE agent_sessions SET ended_at=@ea WHERE id=@id;";
                updateCmd.Parameters.AddWithValue("@ea", now);
                updateCmd.Parameters.AddWithValue("@id", id);
                await updateCmd.ExecuteNonQueryAsync(ct);
                _logger.LogInformation("Closed stale session {Id} (process no longer running)", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover agent session state on startup");
        }
    }

    // Safely get a process (it may have just exited or be inaccessible)
    private static Process? GetProcessByIdSafe(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch { return null; }
    }

    // Safely read the process start time (UTC); on failure returns a far-future time so the session is treated as "PID reused"
    private static DateTime SafeStartTimeUtc(Process proc)
    {
        try { return proc.StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow.AddYears(1); }
    }

    private static string GetDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "unlose", "snapshots.db");
}
