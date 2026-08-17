using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Ipc;
using Unlose.Core.Config;
using Unlose.Core.Data;
using Unlose.Core.Models;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

public class CommandDispatcher
{
    private readonly ILogger<CommandDispatcher> _logger;
    private readonly ISnapshotService _snapshotService;
    private readonly ProtectionPauseManager _pauseManager;
    private readonly IAuditService? _auditService;
    private readonly SqliteRepository? _repository;
    private readonly ISystemRestoreGateway? _systemRestoreService;
    private readonly UnloseConfig? _config;
    private readonly string? _configPath;
    private readonly GlobalMemoryInjector? _memoryInjector;
    private readonly SkillDeployer? _skillDeployer;
    private readonly McpConfigInjector? _mcpConfigInjector;
    private readonly RetentionPolicyEngine? _retentionEngine;

    public CommandDispatcher(
        ILogger<CommandDispatcher> logger,
        ISnapshotService snapshotService,
        ProtectionPauseManager pauseManager,
        IAuditService? auditService = null,
        SqliteRepository? repository = null,
        ISystemRestoreGateway? systemRestoreService = null,
        UnloseConfig? config = null,
        string? configPath = null,
        GlobalMemoryInjector? memoryInjector = null,
        SkillDeployer? skillDeployer = null,
        RetentionPolicyEngine? retentionEngine = null,
        McpConfigInjector? mcpConfigInjector = null)
    {
        _logger = logger;
        _snapshotService = snapshotService;
        _pauseManager = pauseManager;
        _auditService = auditService;
        _repository = repository;
        _systemRestoreService = systemRestoreService;
        _config = config;
        _configPath = configPath;
        _memoryInjector = memoryInjector;
        _skillDeployer = skillDeployer;
        _retentionEngine = retentionEngine;
        _mcpConfigInjector = mcpConfigInjector;
    }

    public async Task<PipeResponse> DispatchAsync(PipeMessage msg, CancellationToken ct = default)
    {
        _logger.LogInformation("Dispatching command: {Command}", msg.Command);
        try
        {
            return msg.Command.ToUpperInvariant() switch
            {
                "LIST_SNAPSHOTS" => await HandleListSnapshotsAsync(ct),
                "CREATE_SNAPSHOT" => await HandleCreateSnapshotAsync(msg, ct),
                "DELETE_SNAPSHOT" => await HandleDeleteSnapshotAsync(msg, ct),
                "RESTORE_SNAPSHOT" => await HandleRestoreSnapshotAsync(msg, ct),
                "MOUNT_SNAPSHOT" => await HandleMountSnapshotAsync(msg, ct),
                "RESTORE_FILES" => await HandleRestoreFilesAsync(msg, ct),
                "PAUSE_PROTECTION" => await HandlePauseProtectionAsync(msg, ct),
                "RESUME_PROTECTION" => await HandleResumeProtectionAsync(ct),
                "LIST_AUDIT_LOG" => await HandleListAuditLogAsync(msg, ct),
                "LIST_MONITOR_EVENTS" => await HandleListMonitorEventsAsync(msg, ct),
                "LIST_AGENT_SESSIONS" => await HandleListAgentSessionsAsync(msg, ct),
                "STATUS" => new PipeResponse { Success = true, Data = $"IsPaused={!_pauseManager.GetState().IsActive}; IsSuspended={StorageGuard.Current?.IsSuspended == true}" },
                "PIN_SNAPSHOT" => await HandlePinSnapshotAsync(msg, ct),
                "LIST_SYSTEM_RESTORE_POINTS" => await HandleListSystemRestorePointsAsync(ct),
                "CREATE_SYSTEM_RESTORE_POINT" => await HandleCreateSystemRestorePointAsync(msg, ct),
                "APPLY_SYSTEM_RESTORE_POINT" => await HandleApplySystemRestorePointAsync(msg, ct),
                "RELOAD_CONFIG" => await HandleReloadConfigAsync(ct),
                "UNINSTALL_CLEANUP" => await HandleUninstallCleanupAsync(ct),
                _ => new PipeResponse { Success = false, ErrorMessage = $"Unknown command: {msg.Command}" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command dispatch error");
            return new PipeResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<PipeResponse> HandleListSnapshotsAsync(CancellationToken ct)
    {
        // Reconcile before listing (throttled to once per 5 min): records whose shadow copy was
        // evicted by Windows must not show up as restorable entries in the library/wizard.
        if (_retentionEngine is not null)
            await _retentionEngine.ReconcileOrphansThrottledAsync(ct);
        var list = await _snapshotService.ListSnapshotsAsync(ct);
        return new PipeResponse { Success = true, Data = System.Text.Json.JsonSerializer.Serialize(list) };
    }

    private async Task<PipeResponse> HandleCreateSnapshotAsync(PipeMessage msg, CancellationToken ct)
    {
        // Multi-volume semantics: an explicit "volume" keeps single-volume behavior; without one,
        // the snapshot follows config.snapshot.volumes (every volume gets its own VSS shadow + record,
        // consistent with scheduled snapshots). Previously the default was a hard-coded C:\.
        var volume     = msg.Parameters.GetValueOrDefault("volume");
        var label      = msg.Parameters.GetValueOrDefault("label")
                      ?? msg.Parameters.GetValueOrDefault("description");
        var sourceTool = msg.Parameters.GetValueOrDefault("source_tool");
        var channel    = msg.Parameters.GetValueOrDefault("channel");
        var sessionId  = msg.Parameters.GetValueOrDefault("sessionId")
                      ?? msg.Parameters.GetValueOrDefault("session_id");
        // An explicit triggerType wins; a source_tool self-identification (track-B Agents calling via
        // CLI/MCP) defaults to AgentInitiated; with neither, fall back to Manual (the user typing
        // `unlose snapshot` in the CLI directly).
        var triggerType = msg.Parameters.TryGetValue("triggerType", out var tt)
                       && Enum.TryParse<TriggerType>(tt, true, out var parsed)
            ? parsed
            : !string.IsNullOrEmpty(sourceTool) ? TriggerType.AgentInitiated : TriggerType.Manual;

        // Session-first-snapshot dedup (primary of the two tracks): AgentInitiated with "session baseline"
        // semantics (label empty or a baseline phrase), same source process, same sessionId (or both empty),
        // and an existing snapshot within the cooldown window → idempotently return the existing record
        // instead of creating a new one.
        // Prevents triple-triggering from "process detection + MCP initialize + CLI/skill guidance".
        // Explicit pre-dangerous-operation snapshots carry non-baseline labels and never enter dedup —
        // protection semantics must not be skipped.
        if (triggerType == TriggerType.AgentInitiated
            && !string.IsNullOrEmpty(sourceTool)
            && IsBaselineLabel(label))
        {
            var existing = await FindSameSessionSnapshotAsync(sourceTool, sessionId, ct);
            if (existing is not null)
            {
                _logger.LogDebug("AgentInitiated baseline snapshot deduped: reusing {Id} for {Source}", existing.Id, sourceTool);
                return new PipeResponse
                {
                    Success = true,
                    Data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id          = existing.Id,
                        createdAt   = existing.CreatedAt,
                        label       = existing.Label,
                        triggerType = existing.TriggerType.ToString(),
                        triggerDetail = existing.TriggerDetail,
                        sessionId   = existing.SessionId,
                        deduped     = true
                    })
                };
            }
        }

        SnapshotRecord record;
        // volumes == null → SnapshotManager falls back to _config.Snapshot.Volumes (multi-volume)
        string[]? volumes = string.IsNullOrWhiteSpace(volume) ? null : [volume];
        if (_snapshotService is SnapshotManager manager)
        {
            var detail = !string.IsNullOrEmpty(sourceTool)
                ? ComposeAgentDetail(sourceTool, channel)
                : label;
            record = await manager.CreateAsync(
                triggerType,
                triggerDetail: detail,
                label: label,
                volumes: volumes,
                sessionId: sessionId,
                ct: ct)
                ?? throw new InvalidOperationException("Snapshot creation failed.");
        }
        else
        {
            // Alternate/mock implementation: single record; fall back to the first configured volume
            var vol = string.IsNullOrWhiteSpace(volume)
                ? _config?.Snapshot.Volumes.FirstOrDefault() ?? "C:\\"
                : volume;
            record = await _snapshotService.CreateSnapshotAsync(vol, ct);
            record.Label = label;
            record.TriggerType = triggerType;
            record.SessionId = sessionId;
            // TriggerDetail matches the SnapshotManager path: "source (channel)" when a source exists, otherwise falls back to the label
            record.TriggerDetail = !string.IsNullOrEmpty(sourceTool)
                ? ComposeAgentDetail(sourceTool, channel)
                : label;
        }

        return new PipeResponse
        {
            Success = true,
            Data = System.Text.Json.JsonSerializer.Serialize(new
            {
                id          = record.Id,
                createdAt   = record.CreatedAt,
                label       = record.Label,
                triggerType = record.TriggerType.ToString(),
                triggerDetail = record.TriggerDetail,
                sessionId   = record.SessionId
            })
        };
    }

    /// <summary>
    /// TriggerDetail format for track-B AgentInitiated: "source (channel)", e.g. "kimi.exe (cli)", "kimi.exe (mcp)";
    /// the UI description column composes "source: label(channel)" from it. Only cli/mcp/skill are valid
    /// channels; anything else or missing falls back to the legacy-data convention
    /// (source_tool=mcp → mcp, otherwise cli).
    /// </summary>
    public static string ComposeAgentDetail(string sourceTool, string? channel)
    {
        var ch = channel?.ToLowerInvariant() is "cli" or "mcp" or "skill" ? channel!.ToLowerInvariant()
            : sourceTool == "mcp" ? "mcp" : "cli";
        return $"{sourceTool} ({ch})";
    }

    /// <summary>Session-baseline label check: empty or one of the three baseline phrases (including the Chinese/English spellings from older instructions).</summary>
    private static bool IsBaselineLabel(string? label) =>
        string.IsNullOrWhiteSpace(label)
        || label.Trim().ToLowerInvariant() is "session-start" or "session start" or "新会话开始";

    /// <summary>
    /// Session-first-snapshot dedup query: within the cooldown window, same source process
    /// (TriggerDetail prefix-matched on "sourceTool ", covering both AgentPreSession "name (PID=..)"
    /// and AgentInitiated "name (channel)"), and identical sessionId or both empty.
    /// Returns null on query failure (better to take one extra snapshot — protection semantics first).
    /// </summary>
    private async Task<SnapshotRecord?> FindSameSessionSnapshotAsync(string sourceTool, string? sessionId, CancellationToken ct)
    {
        var windowMinutes = _config?.Agent.AgentSnapshotCooldownMinutes ?? 10;
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(windowMinutes);

        IReadOnlyList<SnapshotRecord> all;
        try { all = await _snapshotService.ListSnapshotsAsync(ct); }
        catch { return null; }

        foreach (var s in all)
        {
            if (s.CreatedAt.ToUniversalTime() < cutoff) continue;
            if (string.IsNullOrEmpty(s.TriggerDetail)
                || !s.TriggerDetail.StartsWith(sourceTool + " ", StringComparison.OrdinalIgnoreCase))
                continue;
            var sameSession = !string.IsNullOrEmpty(sessionId) && sessionId == s.SessionId;
            var bothEmpty = string.IsNullOrEmpty(sessionId) && string.IsNullOrEmpty(s.SessionId);
            if (sameSession || bothEmpty)
                return s;
        }
        return null;
    }

    /// <summary>
    /// Uninstall cleanup (patent claim 11): removes the protection instruction blocks from each user's
    /// global memory files by paired markers, removes the deployed unlose-snapshot skill package and the
    /// MCP config injection, restoring the original content with no leftovers. Returns per-line results.
    /// </summary>
    private async Task<PipeResponse> HandleUninstallCleanupAsync(CancellationToken ct)
    {
        if (_memoryInjector is null || _skillDeployer is null)
            return new PipeResponse { Success = false, ErrorMessage = "Uninstall cleanup unavailable" };

        var results = new List<string>();
        results.AddRange(await _memoryInjector.RemoveForAllUsersAsync(ct));
        results.AddRange(await _skillDeployer.RemoveForAllUsersAsync(ct));
        if (_mcpConfigInjector is not null)
            results.AddRange(await _mcpConfigInjector.RemoveForAllUsersAsync(ct));
        var removed = results.Count(r => r.Contains("REMOVED", StringComparison.Ordinal));
        return new PipeResponse
        {
            Success = true,
            Data = $"removed={removed}\n" + string.Join("\n", results)
        };
    }

    private async Task<PipeResponse> HandleDeleteSnapshotAsync(PipeMessage msg, CancellationToken ct)
    {
        if (!msg.Parameters.TryGetValue("id", out var idStr) || !Guid.TryParse(idStr, out var id))
            return new PipeResponse { Success = false, ErrorMessage = "Invalid snapshot id" };
        // Deleting an unknown id must fail cleanly instead of silently "succeeding" —
        // otherwise the UI would confirm a deletion that never happened (fault-injection F5 finding).
        var existing = await _snapshotService.ListSnapshotsAsync(ct);
        if (!existing.Any(s => s.Id == id))
            return new PipeResponse { Success = false, ErrorMessage = "Snapshot not found" };
        await _snapshotService.DeleteSnapshotAsync(id, ct);
        return new PipeResponse { Success = true };
    }

    private async Task<PipeResponse> HandleRestoreSnapshotAsync(PipeMessage msg, CancellationToken ct)
    {
        // Parameter-name compatibility: CLI and the snapshot library page send "id"; the immersive restore page sends "snapshotId"
        if ((!msg.Parameters.TryGetValue("id", out var idStr) &&
             !msg.Parameters.TryGetValue("snapshotId", out idStr)) || !Guid.TryParse(idStr, out var id))
            return new PipeResponse { Success = false, ErrorMessage = "Invalid snapshot id" };

        // Optional targetPath: when set, restores into that directory ("restore to a specified new directory"); otherwise rolls back the original volume
        msg.Parameters.TryGetValue("targetPath", out var targetPath);
        var inPlace = string.IsNullOrWhiteSpace(targetPath);
        if (inPlace)
        {
            // Gate 1: master switch (default off). Enforced service-side so CLI/MCP callers cannot bypass the UI.
            if (_config?.Snapshot.EnableInPlaceVolumeRestore != true)
                return new PipeResponse { Success = false, ErrorMessage = "In-place full-volume restore is disabled. Enable it in Settings (non-system volumes only), or use immersive file-pick restore / restore-to-directory instead." };

            var snaps = await _snapshotService.ListSnapshotsAsync(ct);
            var rec = snaps.FirstOrDefault(s => s.Id == id);
            if (rec is null)
                return new PipeResponse { Success = false, ErrorMessage = "Snapshot not found" };

            // Gate 2: the system volume is never rolled back in place — force-overwriting/purging a running
            // OS volume skips locked files and leaves an inconsistent, potentially unbootable system.
            var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            var volRoot = Path.GetPathRoot(rec.VolumePath);
            if (string.Equals(systemRoot?.TrimEnd('\\'), volRoot?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return new PipeResponse { Success = false, ErrorMessage = "In-place restore is not supported on the system volume. Use Windows System Restore instead." };

            // Gate 3: safety net — auto PreRestore snapshot of the target volume before the destructive
            // rollback (previously UI-only; CLI/MCP callers had none). Abort if it fails.
            try
            {
                await _snapshotService.CreateSnapshotAsync(volRoot ?? rec.VolumePath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PreRestore snapshot failed; aborting in-place restore of {Id}", id);
                return new PipeResponse { Success = false, ErrorMessage = $"Pre-restore safety snapshot failed; in-place restore aborted: {ex.Message}" };
            }
        }

        var result = await _snapshotService.RestoreSnapshotAsync(id, targetPath, ct);
        // BUG-RESTORE-003: the original implementation returned Success=result without an ErrorMessage,
        // so on failure the CLI could only print "(no error message)".
        // Include a clear reason for user/script diagnosis (failure may stem from: snapshot not found, missing ShadowId, robocopy error).
        return new PipeResponse
        {
            Success = result,
            ErrorMessage = result ? null : $"Restore failed for snapshot {id} (not found, missing shadow id, or robocopy error; check service logs)."
        };
    }

    // MOUNT_SNAPSHOT: mounts a snapshot's shadow copy as a browsable directory (needed by the immersive restore page's file tree).
    // Implementation: mklink symlink to the GLOBALROOT device path (neither .NET enumeration nor robocopy can access device paths directly).
    private async Task<PipeResponse> HandleMountSnapshotAsync(PipeMessage msg, CancellationToken ct)
    {
        if (!msg.Parameters.TryGetValue("snapshotId", out var idStr) || !Guid.TryParse(idStr, out var id))
            return new PipeResponse { Success = false, ErrorMessage = "Invalid snapshotId" };

        var rootPath = await _snapshotService.MountSnapshotAsync(id, ct);
        if (rootPath is null)
            return new PipeResponse { Success = false, ErrorMessage = $"Snapshot {id} not found or its shadow copy no longer exists." };

        return new PipeResponse
        {
            Success = true,
            Data = System.Text.Json.JsonSerializer.Serialize(new { rootPath })
        };
    }

    // RESTORE_FILES: cherry-pick restore (items checked on the immersive restore page → target directory).
    // paths accepts a JSON array or semicolon-separated relative paths; includes server-side path-traversal validation.
    private async Task<PipeResponse> HandleRestoreFilesAsync(PipeMessage msg, CancellationToken ct)
    {
        if ((!msg.Parameters.TryGetValue("snapshotId", out var idStr) &&
             !msg.Parameters.TryGetValue("id", out idStr)) || !Guid.TryParse(idStr, out var id))
            return new PipeResponse { Success = false, ErrorMessage = "Invalid snapshotId" };

        if (!msg.Parameters.TryGetValue("paths", out var pathsRaw))
            return new PipeResponse { Success = false, ErrorMessage = "Missing paths" };

        List<string>? paths;
        try { paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(pathsRaw); }
        catch { paths = pathsRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); }

        if (paths is null || paths.Count == 0)
            return new PipeResponse { Success = false, ErrorMessage = "Empty paths" };

        // Path-traversal guard (the server-side last line of defense): rejects absolute paths and '..' segments
        if (paths.Any(p => Path.IsPathRooted(p) || p.Split('\\', '/').Any(seg => seg == "..")))
            return new PipeResponse { Success = false, ErrorMessage = "Illegal path (rooted or contains '..')" };

        if (!msg.Parameters.TryGetValue("targetPath", out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
            return new PipeResponse { Success = false, ErrorMessage = "Missing targetPath" };

        var failed = await _snapshotService.RestoreFilesAsync(id, paths, targetPath, ct);
        if (failed is null)
            return new PipeResponse { Success = false, ErrorMessage = $"Snapshot {id} not found or its shadow copy no longer exists." };

        return new PipeResponse
        {
            Success = failed.Count == 0,
            Data = System.Text.Json.JsonSerializer.Serialize(new { restored = paths.Count - failed.Count, failed }),
            ErrorMessage = failed.Count == 0 ? null : $"{failed.Count} item(s) failed: {string.Join("; ", failed.Take(5))}"
        };
    }

    private async Task<PipeResponse> HandlePauseProtectionAsync(PipeMessage msg, CancellationToken ct)
    {
        var minutesStr = msg.Parameters.GetValueOrDefault("minutes", "30");
        if (!int.TryParse(minutesStr, out var minutes) || minutes <= 0 || minutes > 1440)
            minutes = 30;
        var duration = minutes <= 30 ? Unlose.Core.Enums.PauseDuration.ThirtyMinutes
                     : minutes <= 60 ? Unlose.Core.Enums.PauseDuration.OneHour
                     : Unlose.Core.Enums.PauseDuration.UntilReboot;
        await _pauseManager.PauseAsync(duration, requestedBy: "cli", ct: ct);
        return new PipeResponse { Success = true };
    }

    private async Task<PipeResponse> HandleResumeProtectionAsync(CancellationToken ct)
    {
        await _pauseManager.ResumeAsync(ct);
        return new PipeResponse { Success = true };
    }

    private async Task<PipeResponse> HandleListAuditLogAsync(PipeMessage msg, CancellationToken ct)
    {
        if (_auditService is null)
            return new PipeResponse { Success = false, ErrorMessage = "Audit service is unavailable." };

        var days = ParsePositiveInt(msg.Parameters.GetValueOrDefault("days"), 30);
        var from = DateTime.UtcNow.AddDays(-days);
        var entries = await _auditService.QueryAsync(from: from, ct: ct);
        return new PipeResponse { Success = true, Data = System.Text.Json.JsonSerializer.Serialize(entries) };
    }

    private async Task<PipeResponse> HandleListMonitorEventsAsync(PipeMessage msg, CancellationToken ct)
    {
        if (_repository is null)
            return new PipeResponse { Success = false, ErrorMessage = "Monitor event repository is unavailable." };

        var days = ParsePositiveInt(msg.Parameters.GetValueOrDefault("days"), 1);
        var max = ParsePositiveInt(msg.Parameters.GetValueOrDefault("max"), 200);
        var eventType = msg.Parameters.GetValueOrDefault("eventType");
        if (string.IsNullOrWhiteSpace(eventType))
            eventType = null;

        var from = DateTime.UtcNow.AddDays(-days);
        var events = await _repository.ListMonitorEventsAsync(from: from, eventType: eventType, maxResults: max, ct: ct);
        return new PipeResponse { Success = true, Data = System.Text.Json.JsonSerializer.Serialize(events) };
    }

    private async Task<PipeResponse> HandleListAgentSessionsAsync(PipeMessage msg, CancellationToken ct)
    {
        if (_repository is null)
            return new PipeResponse { Success = false, ErrorMessage = "Agent session repository is unavailable." };

        var activeOnly = bool.TryParse(msg.Parameters.GetValueOrDefault("activeOnly"), out var parsed) && parsed;
        var sessions = await _repository.ListAgentSessionsAsync(activeOnly, ct);
        return new PipeResponse { Success = true, Data = System.Text.Json.JsonSerializer.Serialize(sessions) };
    }

    private async Task<PipeResponse> HandlePinSnapshotAsync(PipeMessage msg, CancellationToken ct)
    {
        if (!msg.Parameters.TryGetValue("id", out var idStr) || !Guid.TryParse(idStr, out var id))
            return new PipeResponse { Success = false, ErrorMessage = "Invalid snapshot id" };
        var pinned = bool.TryParse(msg.Parameters.GetValueOrDefault("pinned", "true"), out var p) && p;
        if (_snapshotService is SnapshotManager manager)
            await manager.SetPinnedAsync(id, pinned, ct);
        else
            return new PipeResponse { Success = false, ErrorMessage = "Pin not supported by current snapshot service." };
        return new PipeResponse { Success = true };
    }

    private async Task<PipeResponse> HandleListSystemRestorePointsAsync(CancellationToken ct)
    {
        if (_systemRestoreService is null)
            return new PipeResponse { Success = false, ErrorMessage = "SystemRestoreService is unavailable." };
        try
        {
            var points = await _systemRestoreService.ListRestorePointsAsync(ct);
            return new PipeResponse { Success = true, Data = System.Text.Json.JsonSerializer.Serialize(points) };
        }
        catch (Exception ex)
        {
            return new PipeResponse { Success = false, ErrorMessage = $"查询系统还原点失败：{ex.Message}" };
        }
    }

    private async Task<PipeResponse> HandleCreateSystemRestorePointAsync(PipeMessage msg, CancellationToken ct)
    {
        if (_systemRestoreService is null)
            return new PipeResponse { Success = false, ErrorMessage = "SystemRestoreService is unavailable." };
        var description = msg.Parameters.GetValueOrDefault("description", "unlose Manual Restore Point");
        // DIAG-SRP-001: the service now returns a RestorePointResult (with diagnostics), passed through to the CLI.
        // On failure ErrorMessage is no longer a vague "check Windows privileges" but a specific
        // "VSS quota 93% full / throttled 1440min / suggest resizing..." so the user knows how to fix it.
        var result = await _systemRestoreService.CreateRestorePointAsync(description, ct);
        return new PipeResponse
        {
            Success = result.Success,
            ErrorMessage = result.Success ? null : (result.DiagnosticMessage ?? "Create restore point failed (no diagnostic available).")
        };
    }

    private async Task<PipeResponse> HandleApplySystemRestorePointAsync(PipeMessage msg, CancellationToken ct)
    {
        if (_systemRestoreService is null)
            return new PipeResponse { Success = false, ErrorMessage = "SystemRestoreService is unavailable." };
        if (!msg.Parameters.TryGetValue("sequenceNumber", out var seqStr) || !int.TryParse(seqStr, out var seq))
            return new PipeResponse { Success = false, ErrorMessage = "Invalid sequenceNumber" };

        // Fix ARCH-APPLY-001: previously fire-and-forget (_ = ...RestoreToPointAsync) discarded the Task,
        // so the returned Success was always true and the real WMI result never propagated. Now awaits the real result.
        var initiated = await _systemRestoreService.RestoreToPointAsync(seq, ct);

        // Fix ARCH-APPLY-002: high-risk operations must leave an audit trail (audit failure must not block the response)
        if (_auditService is not null)
        {
            try
            {
                await _auditService.LogAsync(new AuditEntry
                {
                    Action = "SystemRestoreApplied",
                    Actor = "service",
                    Details = $"Applied restore point SequenceNumber={seq}; initiated={initiated}",
                    Success = initiated
                }, ct);
            }
            catch (Exception auditEx)
            {
                _logger.LogWarning(auditEx, "Failed to write audit log for SystemRestoreApplied (seq={Seq})", seq);
            }
        }

        // Note: WMI Restore only schedules a restore for the next reboot and returns immediately;
        // the actual restore happens on the next system restart. The service does not reboot on its own — the reboot decision is left to the user.
        return new PipeResponse
        {
            Success = initiated,
            Data = initiated ? "Restore scheduled; please restart the computer to complete." : null,
            ErrorMessage = initiated ? null : "Failed to initiate system restore (check service logs)."
        };
    }

    // RELOAD_CONFIG: hot-reloads the configuration. Re-reads config.json and assigns each field
    // IN PLACE on the live UnloseConfig singleton — consumers (SnapshotScheduler/StorageGuard/
    // SnapshotManager/AgentSessionManager) hold the same reference, so field-by-field assignment
    // (rather than replacing the instance) guarantees they see the new values immediately.
    // Returns Success=false if the file is missing or fails to parse; the running config stays unchanged.
    private async Task<PipeResponse> HandleReloadConfigAsync(CancellationToken ct)
    {
        if (_config is null || string.IsNullOrWhiteSpace(_configPath))
            return new PipeResponse { Success = false, ErrorMessage = "Config reload is unavailable (no live config bound)." };

        if (!File.Exists(_configPath))
            return new PipeResponse { Success = false, ErrorMessage = $"配置文件不存在：{_configPath}" };

        UnloseConfig fresh;
        try
        {
            fresh = await ConfigLoader.LoadAsync(_configPath, ct);
        }
        catch (Exception ex)
        {
            // Parse failure: the running config stays unchanged
            return new PipeResponse { Success = false, ErrorMessage = $"配置解析失败，运行中配置未修改：{ex.Message}" };
        }

        // In-place field-by-field update (instances/sub-objects are not replaced, so references held by consumers stay valid)
        _config.Snapshot.Volumes = fresh.Snapshot.Volumes;
        _config.Snapshot.ScheduleTimes = fresh.Snapshot.ScheduleTimes;
        _config.Snapshot.IntervalHours = fresh.Snapshot.IntervalHours;
        _config.Snapshot.MaxCount = fresh.Snapshot.MaxCount;
        _config.Snapshot.RetentionDays = fresh.Snapshot.RetentionDays;
        _config.Snapshot.StorageThresholdGb = fresh.Snapshot.StorageThresholdGb;
        _config.Snapshot.MaxStorageGb = fresh.Snapshot.MaxStorageGb;
        _config.Snapshot.Retention24hCount = fresh.Snapshot.Retention24hCount;
        _config.Snapshot.EnableInPlaceVolumeRestore = fresh.Snapshot.EnableInPlaceVolumeRestore;

        _config.Service.PipeName = fresh.Service.PipeName;
        _config.Service.HeartbeatIntervalSeconds = fresh.Service.HeartbeatIntervalSeconds;
        _config.Service.LogLevel = fresh.Service.LogLevel;
        _config.Service.TrustedClientThumbprints = fresh.Service.TrustedClientThumbprints;
        _config.Service.AllowAnySignedClientInProduction = fresh.Service.AllowAnySignedClientInProduction;

        _config.Agent.MonitoredProcesses = fresh.Agent.MonitoredProcesses;
        _config.Agent.SessionPollIntervalSeconds = fresh.Agent.SessionPollIntervalSeconds;
        _config.Agent.AgentSnapshotCooldownMinutes = fresh.Agent.AgentSnapshotCooldownMinutes;

        _logger.LogInformation(
            "Config reloaded from {Path}: Volumes=[{Volumes}], IntervalHours={H}, StorageThresholdGb={T}, MonitoredProcesses={P}",
            _configPath, string.Join(",", _config.Snapshot.Volumes),
            _config.Snapshot.IntervalHours, _config.Snapshot.StorageThresholdGb,
            _config.Agent.MonitoredProcesses.Length);

        // Immediately run one retention-policy cleanup after the hot reload, so a changed 24h retention
        // count takes effect at once (no need to wait for the next snapshot).
        // Failure here does not affect the success semantics of the reload itself.
        if (_retentionEngine is not null)
        {
            try { await _retentionEngine.EnforceAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Post-reload retention enforce failed (non-fatal)"); }
        }

        return new PipeResponse
        {
            Success = true,
            Data = $"Reloaded from {_configPath}; Volumes={string.Join(",", _config.Snapshot.Volumes)}; " +
                   $"IntervalHours={_config.Snapshot.IntervalHours}; StorageThresholdGb={_config.Snapshot.StorageThresholdGb}; " +
                   $"MonitoredProcesses={_config.Agent.MonitoredProcesses.Length}"
        };
    }

    private static int ParsePositiveInt(string? raw, int fallback)
        => int.TryParse(raw, out var value) && value > 0 ? value : fallback;
}
