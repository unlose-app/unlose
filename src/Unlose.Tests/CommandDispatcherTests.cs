using Unlose.Core.Interfaces;
using Unlose.Core.Ipc;
using Unlose.Core.Models;
using Unlose.Core.Data;
using Unlose.Core.Config;
using Unlose.Service;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// Integration tests: CommandDispatcher → ISnapshotService round-trip,
/// and parameter validation edge cases.
/// </summary>
public class CommandDispatcherTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CommandDispatcher BuildDispatcher(
        out FakeSnapshotService fake,
        IAuditService? auditService = null,
        SqliteRepository? repository = null,
        ISystemRestoreGateway? systemRestoreGateway = null,
        UnloseConfig? config = null)
    {
        fake = new FakeSnapshotService();
        var pauseMgr = new ProtectionPauseManager(NullLogger<ProtectionPauseManager>.Instance, new EventBus());
        return new CommandDispatcher(
            NullLogger<CommandDispatcher>.Instance,
            fake,
            pauseMgr,
            auditService,
            repository,
            systemRestoreGateway,
            config);
    }

    private static PipeMessage Msg(string command, Dictionary<string, string>? p = null) =>
        new() { Command = command, Parameters = p ?? new() };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListSnapshots_EmptyInitially()
    {
        var dispatcher = BuildDispatcher(out _);
        var response = await dispatcher.DispatchAsync(Msg("LIST_SNAPSHOTS"));

        Assert.True(response.Success);
        var list = JsonSerializer.Deserialize<List<SnapshotRecord>>(response.Data!);
        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public async Task CreateSnapshot_AppearsInList()
    {
        var dispatcher = BuildDispatcher(out _);

        var createResp = await dispatcher.DispatchAsync(
            Msg("CREATE_SNAPSHOT", new() { ["volume"] = "C:\\" }));
        Assert.True(createResp.Success);

        var listResp = await dispatcher.DispatchAsync(Msg("LIST_SNAPSHOTS"));
        var list = JsonSerializer.Deserialize<List<SnapshotRecord>>(listResp.Data!);
        Assert.Single(list!);
        Assert.Equal("C:\\", list![0].VolumePath);
    }

    [Fact]
    public async Task CreateSnapshot_WithoutVolume_FallsBackToFirstConfiguredVolume()
    {
        // Multi-volume semantics: when the caller omits "volume", the service follows
        // config.snapshot.volumes instead of hard-coding C:\ (the non-SnapshotManager path
        // creates a single record on the first configured volume).
        var cfg = new UnloseConfig();
        cfg.Snapshot.Volumes = new[] { "D:\\", "E:\\" };
        var dispatcher = BuildDispatcher(out var fake, config: cfg);

        var resp = await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", new() { ["label"] = "no-vol" }));
        Assert.True(resp.Success);
        var rec = fake.Snapshots.Single(s => s.Label == "no-vol");
        Assert.Equal("D:\\", rec.Volumes[0]);
    }

    [Fact]
    public async Task CreateSnapshot_ExplicitVolume_IgnoresConfigVolumes()
    {
        // An explicit --volume must keep single-volume behavior regardless of config.snapshot.volumes.
        var cfg = new UnloseConfig();
        cfg.Snapshot.Volumes = new[] { "D:\\", "E:\\" };
        var dispatcher = BuildDispatcher(out var fake, config: cfg);

        var resp = await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", new() { ["volume"] = "E:\\", ["label"] = "explicit" }));
        Assert.True(resp.Success);
        var rec = fake.Snapshots.Single(s => s.Label == "explicit");
        Assert.Equal("E:\\", rec.Volumes[0]);
    }

    [Fact]
    public async Task DeleteSnapshot_RemovesFromList()
    {
        var dispatcher = BuildDispatcher(out var fake);
        await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", new() { ["volume"] = "D:\\" }));
        var id = fake.Snapshots[0].Id;

        var deleteResp = await dispatcher.DispatchAsync(
            Msg("DELETE_SNAPSHOT", new() { ["id"] = id.ToString() }));
        Assert.True(deleteResp.Success);

        var listResp = await dispatcher.DispatchAsync(Msg("LIST_SNAPSHOTS"));
        var list = JsonSerializer.Deserialize<List<SnapshotRecord>>(listResp.Data!);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task DeleteSnapshot_InvalidId_ReturnsFalse()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(
            Msg("DELETE_SNAPSHOT", new() { ["id"] = "not-a-guid" }));

        Assert.False(resp.Success);
        Assert.Contains("Invalid", resp.ErrorMessage);
    }

    [Fact]
    public async Task DeleteSnapshot_UnknownId_ReturnsFalse()
    {
        var dispatcher = BuildDispatcher(out var svc);
        await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", new() { ["volume"] = "C:\\" }));
        var foreignId = Guid.NewGuid();

        var resp = await dispatcher.DispatchAsync(
            Msg("DELETE_SNAPSHOT", new() { ["id"] = foreignId.ToString() }));

        Assert.False(resp.Success);
        Assert.Contains("not found", resp.ErrorMessage);
        Assert.Single(svc.Snapshots); // existing snapshots untouched
    }

    [Fact]
    public async Task PauseProtection_InvalidMinutes_UsesDefault_DoesNotThrow()
    {
        var dispatcher = BuildDispatcher(out _);
        // "bad" is not a valid int; dispatcher should fall back to 30 min, not throw
        var resp = await dispatcher.DispatchAsync(
            Msg("PAUSE_PROTECTION", new() { ["minutes"] = "bad" }));

        Assert.True(resp.Success);
    }

    [Fact]
    public async Task PauseProtection_NegativeMinutes_UsesDefault()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(
            Msg("PAUSE_PROTECTION", new() { ["minutes"] = "-5" }));

        Assert.True(resp.Success);
    }

    [Fact]
    public async Task Status_ReturnsIsPausedState()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(Msg("STATUS"));

        Assert.True(resp.Success);
        Assert.Contains("IsPaused=False", resp.Data);
    }

    [Fact]
    public async Task ListAuditLog_ReturnsEntriesFromAuditService()
    {
        var audit = new FakeAuditService();
        audit.Entries.Add(new AuditEntry
        {
            Action = "SnapshotCreated",
            Actor = "service",
            Details = "Created by test",
            Success = true,
            Timestamp = DateTime.UtcNow
        });

        var dispatcher = BuildDispatcher(out _, auditService: audit);
        var resp = await dispatcher.DispatchAsync(Msg("LIST_AUDIT_LOG", new() { ["days"] = "30" }));

        Assert.True(resp.Success);
        var list = JsonSerializer.Deserialize<List<AuditEntry>>(resp.Data!);
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal("SnapshotCreated", list[0].Action);
    }

    [Fact]
    public async Task ListMonitorEvents_ReturnsRowsFromRepository()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Unlose_CommandDispatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "dispatcher-tests.db");

        try
        {
            await DatabaseInitializer.EnsureCreatedAsync(dbPath);
            var repository = new SqliteRepository(dbPath);
            await repository.InsertMonitorEventAsync(new MonitorEventRecord
            {
                EventType = "DangerCommand",
                ProcessName = "pwsh.exe",
                Pid = 1234,
                Description = "test event"
            });

            var dispatcher = BuildDispatcher(out _, repository: repository);
            var resp = await dispatcher.DispatchAsync(Msg("LIST_MONITOR_EVENTS", new()
            {
                ["days"] = "7",
                ["max"] = "10"
            }));

            Assert.True(resp.Success);
            var list = JsonSerializer.Deserialize<List<MonitorEventRecord>>(resp.Data!);
            Assert.NotNull(list);
            Assert.Single(list!);
            Assert.Equal("DangerCommand", list[0].EventType);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ListAgentSessions_ReturnsOnlyActiveRowsWhenRequested()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Unlose_AgentSessionDispatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "agent-sessions.db");

        try
        {
            await DatabaseInitializer.EnsureCreatedAsync(dbPath);

            await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();

                var activeCmd = conn.CreateCommand();
                activeCmd.CommandText = "INSERT INTO agent_sessions (id, process_name, pid, started_at, ended_at, pre_session_snapshot) VALUES (@id, @pn, @pi, @sa, NULL, @ss);";
                activeCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                activeCmd.Parameters.AddWithValue("@pn", "code.exe");
                activeCmd.Parameters.AddWithValue("@pi", 1001);
                activeCmd.Parameters.AddWithValue("@sa", DateTime.UtcNow.ToString("O"));
                activeCmd.Parameters.AddWithValue("@ss", Guid.NewGuid().ToString());
                await activeCmd.ExecuteNonQueryAsync();

                var endedCmd = conn.CreateCommand();
                endedCmd.CommandText = "INSERT INTO agent_sessions (id, process_name, pid, started_at, ended_at, pre_session_snapshot) VALUES (@id, @pn, @pi, @sa, @ea, NULL);";
                endedCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                endedCmd.Parameters.AddWithValue("@pn", "cursor.exe");
                endedCmd.Parameters.AddWithValue("@pi", 1002);
                endedCmd.Parameters.AddWithValue("@sa", DateTime.UtcNow.AddMinutes(-10).ToString("O"));
                endedCmd.Parameters.AddWithValue("@ea", DateTime.UtcNow.ToString("O"));
                await endedCmd.ExecuteNonQueryAsync();
            }

            var repository = new SqliteRepository(dbPath);
            var dispatcher = BuildDispatcher(out _, repository: repository);
            var resp = await dispatcher.DispatchAsync(Msg("LIST_AGENT_SESSIONS", new()
            {
                ["activeOnly"] = bool.TrueString
            }));

            Assert.True(resp.Success);
            var list = JsonSerializer.Deserialize<List<AgentSessionRecord>>(resp.Data!);
            Assert.NotNull(list);
            Assert.Single(list!);
            Assert.Equal("code.exe", list[0].ProcessName);
            Assert.Null(list[0].EndedAt);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task CreateSnapshot_WithDescription_PassesToLabel()
    {
        var dispatcher = BuildDispatcher(out var fake);
        const string desc = "手动创建测试快照";
        var resp = await dispatcher.DispatchAsync(
            Msg("CREATE_SNAPSHOT", new() { ["volume"] = "C:\\", ["description"] = desc }));
        Assert.True(resp.Success);
        Assert.Single(fake.Snapshots);
        Assert.Equal(desc, fake.Snapshots[0].Label);
    }

    [Fact]
    public async Task CreateSnapshot_WithoutTriggerType_DefaultsToManual()
    {
        var dispatcher = BuildDispatcher(out var fake);
        var resp = await dispatcher.DispatchAsync(
            Msg("CREATE_SNAPSHOT", new() { ["volume"] = "C:\\" }));
        Assert.True(resp.Success);
        Assert.Single(fake.Snapshots);
        Assert.Equal(Unlose.Core.Enums.TriggerType.Manual, fake.Snapshots[0].TriggerType);
    }

    [Fact]
    public async Task CreateSnapshot_WithSourceTool_DefaultsToAgentInitiated()
    {
        // Line B: Agent calls via CLI/MCP self-reporting identity (--source-tool); falls into AgentInitiated when triggerType is not given explicitly
        var dispatcher = BuildDispatcher(out var fake);
        var resp = await dispatcher.DispatchAsync(
            Msg("CREATE_SNAPSHOT", new() { ["volume"] = "C:\\", ["source_tool"] = "agent", ["label"] = "新会话开始" }));
        Assert.True(resp.Success);
        Assert.Single(fake.Snapshots);
        Assert.Equal(Unlose.Core.Enums.TriggerType.AgentInitiated, fake.Snapshots[0].TriggerType);
        Assert.Equal("新会话开始", fake.Snapshots[0].Label);
    }

    [Fact]
    public async Task CreateSnapshot_ExplicitTriggerType_NotOverriddenBySourceTool()
    {
        // Explicit triggerType takes precedence over source_tool inference (e.g. the UI's PreRestore fallback chain)
        var dispatcher = BuildDispatcher(out var fake);
        var resp = await dispatcher.DispatchAsync(
            Msg("CREATE_SNAPSHOT", new()
            {
                ["volume"] = "C:\\", ["source_tool"] = "agent", ["triggerType"] = "PreRestore"
            }));
        Assert.True(resp.Success);
        Assert.Equal(Unlose.Core.Enums.TriggerType.PreRestore, fake.Snapshots[0].TriggerType);
    }

    [Fact]
    public async Task CreateSnapshot_WithSessionId_StoredAndEchoed()
    {
        // Session context identifier (patent claim 15): the caller carries sessionId -> persisted and echoed in the response's inner JSON
        var dispatcher = BuildDispatcher(out var fake);
        var resp = await dispatcher.DispatchAsync(
            Msg("CREATE_SNAPSHOT", new()
            {
                ["volume"] = "C:\\", ["source_tool"] = "agent", ["sessionId"] = "conv-2026-0806"
            }));

        Assert.True(resp.Success);
        Assert.Equal("conv-2026-0806", fake.Snapshots[0].SessionId);
        using var inner = JsonDocument.Parse(resp.Data!);
        Assert.Equal("conv-2026-0806", inner.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("AgentInitiated", inner.RootElement.GetProperty("triggerType").GetString());
    }

    [Theory]
    // Line B TriggerDetail convention "source (channel)": the UI description column composes "process name: note(channel)" from it
    [InlineData("kimi.exe", "cli", "kimi.exe (cli)")]
    [InlineData("kimi.exe", "mcp", "kimi.exe (mcp)")]
    [InlineData("kimi.exe", "skill", "kimi.exe (skill)")]
    [InlineData("kimi.exe", "MCP", "kimi.exe (mcp)")]   // channel casing normalized
    [InlineData("kimi.exe", "ftp", "kimi.exe (cli)")]   // non-whitelisted channel falls back to default inference
    [InlineData("agent", null, "agent (cli)")]          // legacy CLI client: defaults to cli
    [InlineData("mcp", null, "mcp (mcp)")]              // legacy MCP client: source_tool=mcp infers mcp
    public void ComposeAgentDetail_SourceAndChannel(string source, string? channel, string expected)
        => Assert.Equal(expected, CommandDispatcher.ComposeAgentDetail(source, channel));

    [Fact]
    public async Task UnknownCommand_ReturnsFalse()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(Msg("DOES_NOT_EXIST"));

        Assert.False(resp.Success);
        Assert.Contains("Unknown command", resp.ErrorMessage);
    }

    // ── CLI envelope contract tests (e2e finding 3 regression guard)───────────────────────────────
    // CLI HandleSnapshotAsync relies on the inner JSON of PipeResponse.Data returned by CREATE_SNAPSHOT
    // containing the id/createdAt fields; if the service flattens them into the envelope root, CLI unwrapping
    // silently fails and falls back to the raw envelope.
    // This test pins the contract: after serializing the whole PipeResponse, the Data field must be parseable inner JSON.
    [Fact]
    public async Task CreateSnapshot_PipeResponseEnvelope_DataContainsInnerJsonWithIdAndCreatedAt()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(
            Msg("CREATE_SNAPSHOT", new() { ["volume"] = "C:\\", ["label"] = "contract-test" }));

        Assert.True(resp.Success);
        Assert.False(string.IsNullOrWhiteSpace(resp.Data));

        // Serialize the whole envelope (simulating the byte stream PipeServer writes back to the CLI)
        var envelopeJson = JsonSerializer.Serialize(resp);
        var parsedEnv = JsonSerializer.Deserialize<JsonElement>(envelopeJson);

        // Envelope structure contract
        Assert.True(parsedEnv.GetProperty("Success").GetBoolean(),
            "PipeResponse.Success must be true for successful CREATE_SNAPSHOT");

        // Inner Data must be valid JSON containing id/createdAt — CLI HandleSnapshotAsync depends on this structure
        var innerJson = parsedEnv.GetProperty("Data").GetString();
        Assert.False(string.IsNullOrWhiteSpace(innerJson));
        using var inner = JsonDocument.Parse(innerJson!);
        Assert.True(inner.RootElement.TryGetProperty("id", out _),
            "Inner JSON (PipeResponse.Data) must contain 'id' for CLI envelope unwrapping");
        Assert.True(inner.RootElement.TryGetProperty("createdAt", out _),
            "Inner JSON (PipeResponse.Data) must contain 'createdAt' for CLI envelope unwrapping");
    }

    // CLI TryGetEnvelopeFailure relies on: when the service fails it returns an envelope with Success=false.
    // If the service instead embedded ErrorMessage inside a "success" JSON, the CLI exit-code logic would break.
    [Fact]
    public async Task UnknownCommand_ProducesFailureEnvelopeForCliExitCode()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(Msg("BOGUS"));
        var envelopeJson = JsonSerializer.Serialize(resp);

        var parsed = JsonSerializer.Deserialize<JsonElement>(envelopeJson);
        Assert.False(parsed.GetProperty("Success").GetBoolean(),
            "Unknown command must serialize as Success=false envelope so CLI can detect via TryGetEnvelopeFailure");
        Assert.NotNull(resp.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(parsed.GetProperty("ErrorMessage").GetString()));
    }

    // ── APPLY_SYSTEM_RESTORE_POINT path tests ────────────────────────────────────
    // Covers fixes ARCH-APPLY-001 (fire-and-forget) + ARCH-APPLY-002 (audit trail)

    [Fact]
    public async Task ApplyRestorePoint_Success_ReturnsTrueAndWritesAudit()
    {
        var audit = new FakeAuditService();
        var sr = new FakeSystemRestoreGateway { RestoreResult = true };
        var dispatcher = BuildDispatcher(out _, auditService: audit, systemRestoreGateway: sr);

        var resp = await dispatcher.DispatchAsync(
            Msg("APPLY_SYSTEM_RESTORE_POINT", new() { ["sequenceNumber"] = "42" }));

        Assert.True(resp.Success);
        Assert.Equal(42, sr.LastRestoredSequence);
        // Fix ARCH-APPLY-002: high-risk operations must leave an audit trail
        Assert.Single(audit.Entries);
        Assert.Equal("SystemRestoreApplied", audit.Entries[0].Action);
        Assert.True(audit.Entries[0].Success);
    }

    [Fact]
    public async Task ApplyRestorePoint_Failure_ReturnsFalseAndLogsAudit()
    {
        var audit = new FakeAuditService();
        var sr = new FakeSystemRestoreGateway { RestoreResult = false };
        var dispatcher = BuildDispatcher(out _, auditService: audit, systemRestoreGateway: sr);

        var resp = await dispatcher.DispatchAsync(
            Msg("APPLY_SYSTEM_RESTORE_POINT", new() { ["sequenceNumber"] = "99" }));

        Assert.False(resp.Success);
        Assert.NotNull(resp.ErrorMessage);
        // Failures must also be audited (Success=false)
        Assert.Single(audit.Entries);
        Assert.False(audit.Entries[0].Success);
    }

    [Fact]
    public async Task ApplyRestorePoint_InvalidSequence_ReturnsError()
    {
        var sr = new FakeSystemRestoreGateway { RestoreResult = true };
        var dispatcher = BuildDispatcher(out _, systemRestoreGateway: sr);

        var resp = await dispatcher.DispatchAsync(
            Msg("APPLY_SYSTEM_RESTORE_POINT", new() { ["sequenceNumber"] = "abc" }));

        Assert.False(resp.Success);
        Assert.Contains("Invalid", resp.ErrorMessage);
        // Invalid parameters must not trigger a WMI call, nor an audit write
        Assert.Null(sr.LastRestoredSequence);
    }

    [Fact]
    public async Task ApplyRestorePoint_NoServiceInjected_ReturnsUnavailable()
    {
        // Do not inject systemRestoreGateway (defaults to null) — simulates service degradation
        var dispatcher = BuildDispatcher(out _);

        var resp = await dispatcher.DispatchAsync(
            Msg("APPLY_SYSTEM_RESTORE_POINT", new() { ["sequenceNumber"] = "1" }));

        Assert.False(resp.Success);
        Assert.Contains("unavailable", resp.ErrorMessage);
    }

    // ── CREATE_SYSTEM_RESTORE_POINT envelope contract tests (e2e finding 2 regression guard)─────────
    // After the BUG-SRP-001 fix, SystemRestoreService returns false when cross-validation detects a "false success".
    // This test ensures CommandDispatcher propagates that failure correctly (instead of falsely reporting success),
    // and that the failure envelope carries ErrorMessage so the CLI can exit with a non-zero code.
    [Fact]
    public async Task CreateRestorePoint_GatewayReturnsFalse_PropagatesFailureWithMessage()
    {
        var sr = new FakeSystemRestoreGateway { CreateResult = false };
        var dispatcher = BuildDispatcher(out _, systemRestoreGateway: sr);

        var resp = await dispatcher.DispatchAsync(
            Msg("CREATE_SYSTEM_RESTORE_POINT", new() { ["description"] = "cross-check-failed" }));

        Assert.False(resp.Success, "Cross-check failure must propagate as Success=false (BUG-SRP-001)");
        Assert.NotNull(resp.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(resp.ErrorMessage));
    }

    [Fact]
    public async Task CreateRestorePoint_GatewayReturnsTrue_ReturnsSuccess()
    {
        var sr = new FakeSystemRestoreGateway { CreateResult = true };
        var dispatcher = BuildDispatcher(out _, systemRestoreGateway: sr);

        var resp = await dispatcher.DispatchAsync(
            Msg("CREATE_SYSTEM_RESTORE_POINT", new() { ["description"] = "real-success" }));

        Assert.True(resp.Success);
        // On success ErrorMessage should be null (ternary at CommandDispatcher.cs:220)
        Assert.Null(resp.ErrorMessage);
    }

    // DIAG-SRP-001: when cross-validation detects a "false success", the quota/throttling diagnostics
    // collected by the service must be passed through to PipeResponse.ErrorMessage so CLI users see
    // actionable guidance (instead of the generic "check Windows privileges").
    [Fact]
    public async Task CreateRestorePoint_Failure_PropagatesDiagnosticToErrorMessage()
    {
        var sr = new FakeSystemRestoreGateway
        {
            CreateResult = false,
            CreateDiagnostic = "VSS shadow storage used=9.33GB max=10GB (93% used); Likely cause: VSS quota near full (93%). Suggest: vssadmin resize shadowstorage /for=C: /on=C: /maxsize=20GB"
        };
        var dispatcher = BuildDispatcher(out _, systemRestoreGateway: sr);

        var resp = await dispatcher.DispatchAsync(
            Msg("CREATE_SYSTEM_RESTORE_POINT", new() { ["description"] = "quota-test" }));

        Assert.False(resp.Success);
        // Key point: ErrorMessage should contain the diagnostics, not the generic "check Windows privileges"
        Assert.NotNull(resp.ErrorMessage);
        Assert.Contains("VSS shadow storage", resp.ErrorMessage);
        Assert.Contains("93%", resp.ErrorMessage);
        Assert.Contains("vssadmin resize", resp.ErrorMessage);
        // The old generic message must no longer appear
        Assert.DoesNotContain("check Windows privileges", resp.ErrorMessage);
    }

    // ── MOUNT_SNAPSHOT path tests (the immersive restore page file tree depends on it; the service previously lacked this command, leaving the UI tree empty)──

    [Fact]
    public async Task MountSnapshot_Success_ReturnsRootPathJson()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(
            Msg("MOUNT_SNAPSHOT", new() { ["snapshotId"] = Guid.NewGuid().ToString() }));

        Assert.True(resp.Success);
        Assert.False(string.IsNullOrWhiteSpace(resp.Data));
        using var doc = JsonDocument.Parse(resp.Data!);
        Assert.True(doc.RootElement.TryGetProperty("rootPath", out var rp),
            "MOUNT_SNAPSHOT 响应必须含 rootPath —— UI LoadFileTreesAsync 依赖此字段");
        Assert.False(string.IsNullOrWhiteSpace(rp.GetString()));
    }

    [Fact]
    public async Task MountSnapshot_InvalidId_ReturnsError()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(
            Msg("MOUNT_SNAPSHOT", new() { ["snapshotId"] = "not-a-guid" }));

        Assert.False(resp.Success);
        Assert.Contains("Invalid", resp.ErrorMessage);
    }

    [Fact]
    public async Task MountSnapshot_ShadowMissing_ReturnsError()
    {
        var dispatcher = BuildDispatcher(out var svc);
        svc.MountResult = null; // simulates the shadow copy having been purged by the retention policy
        var resp = await dispatcher.DispatchAsync(
            Msg("MOUNT_SNAPSHOT", new() { ["snapshotId"] = Guid.NewGuid().ToString() }));

        Assert.False(resp.Success);
        Assert.Contains("no longer exists", resp.ErrorMessage);
    }

    // ── RESTORE_SNAPSHOT parameter contract (snapshotId/targetPath sent by the immersive restore page were not recognized)──

    [Fact]
    public async Task RestoreSnapshot_AcceptsSnapshotIdAlias()
    {
        // In-place restore requires the settings switch + a non-system volume (triple gate);
        // this test enables the switch and uses a data volume so only the alias is under test.
        var config = new UnloseConfig { Snapshot = { EnableInPlaceVolumeRestore = true } };
        var dispatcher = BuildDispatcher(out var svc, config: config);
        var created = await svc.CreateSnapshotAsync("D:\\");
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_SNAPSHOT", new() { ["snapshotId"] = created.Id.ToString() }));

        Assert.True(resp.Success, "UI 沉浸式还原页发送的参数名是 snapshotId，必须被识别");
    }

    [Fact]
    public async Task RestoreSnapshot_TargetPath_PassedThroughToService()
    {
        var dispatcher = BuildDispatcher(out var svc);
        var created = await svc.CreateSnapshotAsync("C:\\");
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_SNAPSHOT", new() { ["snapshotId"] = created.Id.ToString(), ["targetPath"] = "D:\\restore-out" }));

        Assert.True(resp.Success);
        // "Restore to a specified new directory" must never silently roll back to the original volume (with /purge semantics it would delete original-volume files)
        Assert.Equal("D:\\restore-out", svc.LastRestoreTarget);
    }

    // ── In-place full-volume restore triple gate (settings switch + system volume + PreRestore) ──

    [Fact]
    public async Task RestoreSnapshot_InPlace_DisabledByDefault_Rejected()
    {
        var dispatcher = BuildDispatcher(out var svc); // no config -> switch defaults off
        var created = await svc.CreateSnapshotAsync("D:\\");
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_SNAPSHOT", new() { ["id"] = created.Id.ToString() }));

        Assert.False(resp.Success);
        Assert.Contains("disabled", resp.ErrorMessage);
        Assert.Null(svc.LastRestoreTarget); // restore never reached the service layer
    }

    [Fact]
    public async Task RestoreSnapshot_InPlace_SystemVolume_Rejected()
    {
        var config = new UnloseConfig { Snapshot = { EnableInPlaceVolumeRestore = true } };
        var dispatcher = BuildDispatcher(out var svc, config: config);
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))!;
        var created = await svc.CreateSnapshotAsync(systemRoot);
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_SNAPSHOT", new() { ["id"] = created.Id.ToString() }));

        Assert.False(resp.Success);
        Assert.Contains("system volume", resp.ErrorMessage);
        Assert.Null(svc.LastRestoreTarget);
        Assert.Single(svc.Snapshots); // no PreRestore snapshot created for a rejected restore
    }

    [Fact]
    public async Task RestoreSnapshot_InPlace_NonSystemVolume_CreatesSafetySnapshot()
    {
        var config = new UnloseConfig { Snapshot = { EnableInPlaceVolumeRestore = true } };
        var dispatcher = BuildDispatcher(out var svc, config: config);
        var created = await svc.CreateSnapshotAsync("D:\\");
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_SNAPSHOT", new() { ["id"] = created.Id.ToString() }));

        Assert.True(resp.Success);
        Assert.Null(svc.LastRestoreTarget); // in-place: no targetPath passed down
        Assert.Equal(2, svc.Snapshots.Count); // original + auto PreRestore safety snapshot
    }

    // ── RESTORE_FILES selective-restore path tests (checkbox selections on the immersive restore page)──

    [Fact]
    public async Task RestoreFiles_Success_ReturnsRestoredCount()
    {
        var dispatcher = BuildDispatcher(out var svc);
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_FILES", new()
            {
                ["snapshotId"] = Guid.NewGuid().ToString(),
                ["paths"] = JsonSerializer.Serialize(new[] { @"Users\a.txt", @"Docs\proj" }),
                ["targetPath"] = @"D:\restore-out"
            }));

        Assert.True(resp.Success);
        Assert.Equal(new List<string> { @"Users\a.txt", @"Docs\proj" }, svc.LastRestoreFilesPaths);
        using var doc = JsonDocument.Parse(resp.Data!);
        Assert.Equal(2, doc.RootElement.GetProperty("restored").GetInt32());
    }

    [Fact]
    public async Task RestoreFiles_InvalidId_ReturnsError()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_FILES", new() { ["snapshotId"] = "abc", ["paths"] = "[\"a.txt\"]", ["targetPath"] = @"D:\x" }));

        Assert.False(resp.Success);
        Assert.Contains("Invalid", resp.ErrorMessage);
    }

    [Fact]
    public async Task RestoreFiles_EmptyPaths_ReturnsError()
    {
        var dispatcher = BuildDispatcher(out _);
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_FILES", new() { ["snapshotId"] = Guid.NewGuid().ToString(), ["paths"] = "[]", ["targetPath"] = @"D:\x" }));

        Assert.False(resp.Success);
        Assert.Contains("Empty", resp.ErrorMessage);
    }

    [Fact]
    public async Task RestoreFiles_PathTraversal_RejectedBeforeService()
    {
        var dispatcher = BuildDispatcher(out var svc);
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_FILES", new()
            {
                ["snapshotId"] = Guid.NewGuid().ToString(),
                ["paths"] = JsonSerializer.Serialize(new[] { @"..\..\Windows\System32" }),
                ["targetPath"] = @"D:\x"
            }));

        Assert.False(resp.Success);
        Assert.Contains("Illegal path", resp.ErrorMessage);
        Assert.Null(svc.LastRestoreFilesPaths); // must be intercepted before reaching the service layer
    }

    [Fact]
    public async Task RestoreFiles_ShadowMissing_ReturnsError()
    {
        var dispatcher = BuildDispatcher(out var svc);
        svc.RestoreFilesResult = null;
        var resp = await dispatcher.DispatchAsync(
            Msg("RESTORE_FILES", new()
            {
                ["snapshotId"] = Guid.NewGuid().ToString(),
                ["paths"] = "[\"a.txt\"]",
                ["targetPath"] = @"D:\x"
            }));

        Assert.False(resp.Success);
        Assert.Contains("no longer exists", resp.ErrorMessage);
    }

    // ── RELOAD_CONFIG hot-reload tests ─────────────────────────────────────────────
    // RELOAD_CONFIG applies each config.json field via in-place property assignment onto the running UnloseConfig singleton;
    // consumers (SnapshotScheduler/StorageGuard/SnapshotManager/AgentSessionManager)
    // hold the same reference, so they read the new values immediately after reload.

    [Fact]
    public async Task ReloadConfig_ResponseContainsReloaded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Unlose_ReloadConfigTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            await File.WriteAllTextAsync(configPath, """{"Snapshot":{"IntervalHours":12}}""");
            var liveConfig = new Unlose.Core.Config.UnloseConfig();
            var dispatcher = BuildReloadDispatcher(liveConfig, configPath);

            var resp = await dispatcher.DispatchAsync(Msg("RELOAD_CONFIG"));

            Assert.True(resp.Success);
            Assert.Contains("Reloaded", resp.Data);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReloadConfig_FileChanged_SingletonReflectsNewValues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Unlose_ReloadConfigTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            // Initial file (values match the default singleton)
            await File.WriteAllTextAsync(configPath,
                """{"Snapshot":{"IntervalHours":24,"StorageThresholdGb":2.0},"Agent":{"MonitoredProcesses":["a.exe"],"AgentSnapshotCooldownMinutes":300}}""");
            var liveConfig = new Unlose.Core.Config.UnloseConfig();
            var dispatcher = BuildReloadDispatcher(liveConfig, configPath);

            // Reload after modifying the config file: the running singleton must reflect new values immediately (same reference, in-place assignment, not instance replacement)
            await File.WriteAllTextAsync(configPath,
                """{"Snapshot":{"IntervalHours":6,"StorageThresholdGb":9.5,"Volumes":["D:\\"]},"Agent":{"MonitoredProcesses":["foo.exe"],"AgentSnapshotCooldownMinutes":15}}""");
            var resp = await dispatcher.DispatchAsync(Msg("RELOAD_CONFIG"));

            Assert.True(resp.Success);
            Assert.Equal(6, liveConfig.Snapshot.IntervalHours);
            Assert.Equal(9.5, liveConfig.Snapshot.StorageThresholdGb);
            Assert.Equal(new[] { "D:\\" }, liveConfig.Snapshot.Volumes);
            Assert.Equal(new[] { "foo.exe" }, liveConfig.Agent.MonitoredProcesses);
            Assert.Equal(15, liveConfig.Agent.AgentSnapshotCooldownMinutes);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    // RELOAD_CONFIG test-only construction: direct new (leaves the existing BuildDispatcher untouched, zero intrusion).
    // The two trailing optional parameters (config, configPath) of CommandDispatcher exist for this testability change.
    private static CommandDispatcher BuildReloadDispatcher(
        Unlose.Core.Config.UnloseConfig liveConfig, string configPath)
    {
        var pauseMgr = new ProtectionPauseManager(NullLogger<ProtectionPauseManager>.Instance, new EventBus());
        return new CommandDispatcher(
            NullLogger<CommandDispatcher>.Instance,
            new FakeSnapshotService(),
            pauseMgr,
            config: liveConfig,
            configPath: configPath);
    }

    // ── Session first-snapshot dedup (P1-3 service-side main path)────────────────────────────────────
    // Prevents the three paths "process detection + MCP initialize + CLI/skill instructions" from snapshotting the same session repeatedly:
    // baseline label (empty / session-start / 新会话开始) + same source process + within the cooldown window -> idempotently returns the existing snapshot.

    [Fact]
    public async Task AgentInitiated_BaselineLabel_SameSource_Deduped()
    {
        var dispatcher = BuildDispatcher(out var fake);
        var p = new Dictionary<string, string>
        {
            ["volume"] = "C:\\", ["source_tool"] = "kimi.exe", ["label"] = "session-start"
        };

        var first = await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", p));
        var second = await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", p));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Single(fake.Snapshots); // second call deduplicated, no new snapshot
        using var doc = JsonDocument.Parse(second.Data!);
        Assert.True(doc.RootElement.GetProperty("deduped").GetBoolean());
        Assert.Equal(fake.Snapshots[0].Id.ToString(), doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AgentInitiated_DangerLabel_NeverDeduped()
    {
        var dispatcher = BuildDispatcher(out var fake);
        var p = new Dictionary<string, string>
        {
            ["volume"] = "C:\\", ["source_tool"] = "kimi.exe", ["label"] = "before bulk delete"
        };

        await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", p));
        await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", p));

        // Explicit snapshots before dangerous operations (non-baseline label) are never deduplicated — protection semantics must not be skipped
        Assert.Equal(2, fake.Snapshots.Count);
    }

    [Fact]
    public async Task AgentInitiated_DifferentSessionId_NotDeduped()
    {
        var dispatcher = BuildDispatcher(out var fake);
        Dictionary<string, string> P(string sid) => new()
        {
            ["volume"] = "C:\\", ["source_tool"] = "kimi.exe",
            ["label"] = "session-start", ["sessionId"] = sid
        };

        await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", P("conv-1")));
        await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", P("conv-2")));

        Assert.Equal(2, fake.Snapshots.Count); // new session = new baseline, no dedup
    }

    [Fact]
    public async Task AgentInitiated_Baseline_OutsideCooldown_NotDeduped()
    {
        var dispatcher = BuildDispatcher(out var fake);
        var p = new Dictionary<string, string>
        {
            ["volume"] = "C:\\", ["source_tool"] = "kimi.exe", ["label"] = "session-start"
        };

        await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", p));
        // Manually move the first snapshot's timestamp outside the cooldown window (default 10 minutes)
        fake.Snapshots[0].CreatedAt = DateTime.UtcNow.AddMinutes(-11);

        await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", p));

        Assert.Equal(2, fake.Snapshots.Count);
    }

    [Fact]
    public async Task AgentInitiated_Dedup_MatchesAgentPreSessionDetail()
    {
        // The service-side process-detection AgentPreSession detail looks like "kimi.exe (PID=123)";
        // the subsequent CLI baseline call ("kimi.exe (cli)") should hit same-process dedup
        var dispatcher = BuildDispatcher(out var fake);
        fake.Snapshots.Add(new SnapshotRecord
        {
            Volumes = ["C:\\"],
            TriggerType = Unlose.Core.Enums.TriggerType.AgentPreSession,
            TriggerDetail = "kimi.exe (PID=123)",
            CreatedAt = DateTime.UtcNow
        });

        var resp = await dispatcher.DispatchAsync(Msg("CREATE_SNAPSHOT", new()
        {
            ["volume"] = "C:\\", ["source_tool"] = "kimi.exe", ["label"] = "session-start"
        }));

        Assert.True(resp.Success);
        Assert.Single(fake.Snapshots);
        using var doc = JsonDocument.Parse(resp.Data!);
        Assert.True(doc.RootElement.GetProperty("deduped").GetBoolean());
    }

    // ── Fake service ─────────────────────────────────────────────────────────

    private sealed class FakeSnapshotService : ISnapshotService
    {
        public List<SnapshotRecord> Snapshots { get; } = new();
        private readonly SemaphoreSlim _lock = new(1, 1);

        public async Task<SnapshotRecord> CreateSnapshotAsync(string volumePath, CancellationToken ct = default)
        {
            var record = new SnapshotRecord
            {
                Volumes = new[] { volumePath },
                ShadowId = Guid.NewGuid().ToString(),
                Status = Unlose.Core.Enums.SnapshotStatus.Completed
            };
            await _lock.WaitAsync(ct);
            try { Snapshots.Add(record); }
            finally { _lock.Release(); }
            return record;
        }

        public async Task<IReadOnlyList<SnapshotRecord>> ListSnapshotsAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try { return Snapshots.ToList().AsReadOnly(); }
            finally { _lock.Release(); }
        }

        public async Task DeleteSnapshotAsync(Guid snapshotId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var r = Snapshots.FirstOrDefault(s => s.Id == snapshotId);
                if (r is not null) Snapshots.Remove(r);
            }
            finally { _lock.Release(); }
        }

        public string? LastRestoreTarget { get; private set; }

        public Task<bool> RestoreSnapshotAsync(Guid snapshotId, string? targetPath, CancellationToken ct = default)
        {
            LastRestoreTarget = targetPath;
            return Task.FromResult(Snapshots.Any(s => s.Id == snapshotId));
        }

        /// <summary>Controllable mount result: returns a fixed path by default; set to null to simulate a missing snapshot/shadow copy</summary>
        public string? MountResult { get; set; } = @"C:\fake\mounts\mount_test";

        public Task<string?> MountSnapshotAsync(Guid snapshotId, CancellationToken ct = default) =>
            Task.FromResult(MountResult);

        /// <summary>Controllable selective-restore result: empty list by default (all succeeded); set to null to simulate a missing snapshot/shadow copy</summary>
        public IReadOnlyList<string>? RestoreFilesResult { get; set; } = Array.Empty<string>();
        public List<string>? LastRestoreFilesPaths { get; private set; }

        public Task<IReadOnlyList<string>?> RestoreFilesAsync(
            Guid snapshotId, IReadOnlyList<string> relativePaths, string targetPath, CancellationToken ct = default)
        {
            LastRestoreFilesPaths = relativePaths.ToList();
            return Task.FromResult(RestoreFilesResult);
        }
    }

    private sealed class FakeAuditService : IAuditService
    {
        public List<AuditEntry> Entries { get; } = new();

        public Task LogAsync(AuditEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> QueryAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
        {
            IEnumerable<AuditEntry> query = Entries;
            if (from.HasValue)
                query = query.Where(entry => entry.Timestamp >= from.Value);
            if (to.HasValue)
                query = query.Where(entry => entry.Timestamp <= to.Value);

            return Task.FromResult<IReadOnlyList<AuditEntry>>(query.OrderByDescending(entry => entry.Timestamp).ToList());
        }
    }

    /// <summary>
    /// Controllable ISystemRestoreGateway test double:
    /// - CreateResult determines the RestorePointResult.Success returned by CreateRestorePointAsync;
    /// - CreateDiagnostic determines the diagnostics carried on failure (simulates the quota/throttling hint detected by BUG-SRP-001 cross-validation);
    /// - RestoreResult determines the RestoreToPointAsync return value;
    /// - LastRestoredSequence records the most recently called sequence number (null means never called).
    /// </summary>
    private sealed class FakeSystemRestoreGateway : ISystemRestoreGateway
    {
        public bool CreateResult { get; set; } = true;
        public string? CreateDiagnostic { get; set; }
        public bool RestoreResult { get; set; } = true;
        public int? LastRestoredSequence { get; private set; }

        public Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken ct = default)
            => Task.FromResult(CreateResult
                ? RestorePointResult.Ok()
                : RestorePointResult.Fail(CreateDiagnostic ?? "fake silent failure"));

        public Task<IReadOnlyList<SystemRestorePointInfo>> ListRestorePointsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemRestorePointInfo>>(Array.Empty<SystemRestorePointInfo>());

        public Task<bool> RestoreToPointAsync(int sequenceNumber, CancellationToken ct = default)
        {
            LastRestoredSequence = sequenceNumber;
            return Task.FromResult(RestoreResult);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Test directory cleanup does not affect assertion results
        }
    }
}
