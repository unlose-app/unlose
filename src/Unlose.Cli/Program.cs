using Unlose.Cli;
using Unlose.Core.Ipc;
using System.Text;
using System.Text.Json;

// CLI output is always UTF-8 (no BOM): the console otherwise follows the system ANSI
// code page (936/GBK), so Chinese labels would garble in UTF-8 terminals/pipes;
// JSON/script consumption scenarios require UTF-8.
Console.OutputEncoding = new UTF8Encoding(false);

// --help / -h / help: output usage before any pipe connection
if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintHelp();
    return;
}

var client = new UnlosePipeClient();

var command = args[0].ToLowerInvariant();

// BUG-CLI-001 (e2e finding 3) fix:
// The old implementation returned "unknown command" as one branch of the switch expression
// and the outer layer did an indiscriminate Console.WriteLine, so the user saw
// "Unknown command: xxx" while the exit code was still 0 — violating the CLI contract.
// Unknown commands are now rejected up front: write to stderr and exit non-zero.
CommandKind kind;
try { kind = ClassifyCommand(command); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
    return; // unreachable; required by the compiler
}

try
{
    string result = kind switch
    {
        CommandKind.Status => await client.SendCommandAsync("STATUS"),
        CommandKind.ListSnapshots => await client.SendCommandAsync("LIST_SNAPSHOTS"),
        CommandKind.Snapshot => await HandleSnapshotAsync(client, args),
        CommandKind.DeleteSnapshot => await client.SendCommandAsync("DELETE_SNAPSHOT", new Dictionary<string, string>
        {
            ["id"] = args.ElementAtOrDefault(1) ?? ""
        }),
        CommandKind.PinSnapshot => await client.SendCommandAsync("PIN_SNAPSHOT", new Dictionary<string, string>
        {
            ["id"] = args.ElementAtOrDefault(1) ?? "",
            ["pinned"] = args.ElementAtOrDefault(2) ?? "true"
        }),
        CommandKind.RestoreSnapshot => await client.SendCommandAsync("RESTORE_SNAPSHOT", new Dictionary<string, string>
        {
            ["id"] = args.ElementAtOrDefault(1) ?? ""
        }),
        CommandKind.ListRestorePoints => await client.SendCommandAsync("LIST_SYSTEM_RESTORE_POINTS"),
        CommandKind.CreateRestorePoint => await client.SendCommandAsync("CREATE_SYSTEM_RESTORE_POINT", new Dictionary<string, string>
        {
            ["description"] = args.ElementAtOrDefault(1) ?? "unlose Manual Restore Point"
        }),
        CommandKind.ApplyRestorePoint => await client.SendCommandAsync("APPLY_SYSTEM_RESTORE_POINT", new Dictionary<string, string>
        {
            ["sequenceNumber"] = args.ElementAtOrDefault(1) ?? ""
        }),
        CommandKind.Pause => await client.SendCommandAsync("PAUSE_PROTECTION", new Dictionary<string, string>
        {
            ["minutes"] = args.ElementAtOrDefault(1) ?? "30"
        }),
        CommandKind.Resume => await client.SendCommandAsync("RESUME_PROTECTION"),
        CommandKind.UninstallCleanup => await HandleUninstallCleanupAsync(client),
        _ => throw new InvalidOperationException($"Unreachable command kind: {kind}")
    };
    Console.WriteLine(result);

    // BUG-CLI-002 (e2e finding 3) fix:
    // For unknown commands the service returns a PipeResponse { Success=false, ErrorMessage="Unknown command: ..." };
    // the CLI must recognize this structured failure and exit non-zero so callers
    // (scripts/Pester) can assert correctly.
    if (TryGetEnvelopeFailure(result, out var failMsg))
    {
        Console.Error.WriteLine(failMsg);
        Environment.Exit(2);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Environment.Exit(1);
}

static CommandKind ClassifyCommand(string command) => command switch
{
    "status" => CommandKind.Status,
    "list" or "list-snapshots" => CommandKind.ListSnapshots,
    "snapshot" or "create-snapshot" => CommandKind.Snapshot,
    "delete-snapshot" => CommandKind.DeleteSnapshot,
    "pin-snapshot" => CommandKind.PinSnapshot,
    "restore-snapshot" => CommandKind.RestoreSnapshot,
    "restore-points" or "list-restore-points" => CommandKind.ListRestorePoints,
    "create-restore-point" => CommandKind.CreateRestorePoint,
    "apply-restore-point" => CommandKind.ApplyRestorePoint,
    "pause" => CommandKind.Pause,
    "resume" => CommandKind.Resume,
    "uninstall-cleanup" => CommandKind.UninstallCleanup,
    _ => throw new ArgumentException($"Unknown command: {command}. Run 'unlose --help' for usage.")
};

static void PrintHelp()
{
    Console.WriteLine("unlose CLI v1.0");
    Console.WriteLine("Usage: unlose <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  status                                  Show protection service status");
    Console.WriteLine("  snapshot [--label <text>]               Create a snapshot (default trigger: Manual)");
    Console.WriteLine("           [--source-tool <name>]         Calling agent process name (kimi.exe/...); 'agent' auto-resolves host process");
    Console.WriteLine("           [--channel <cli|mcp|skill>]    Trigger channel (default: cli); stored as source (channel) in description");
    Console.WriteLine("           [--trigger <type>]             Explicit trigger type (Scheduled/Manual/AgentInitiated/...)");
    Console.WriteLine("           [--session <id>]               Caller session/conversation ID (stored with snapshot)");
    Console.WriteLine("           [--volume <path>]");
    Console.WriteLine("           [--pin]                        Pin the snapshot (exempt from retention cleanup)");
    Console.WriteLine("           [--skip-if-recent <minutes>]   Skip if any snapshot exists within N minutes (session-baseline use only,");
    Console.WriteLine("                                          NEVER for pre-dangerous-operation snapshots); prints the existing id");
    Console.WriteLine("           [--quiet]                      Print only the snapshot id (errors still go to stderr)");
    Console.WriteLine("  pin-snapshot <id> [true|false]          Pin/unpin a snapshot (pinned snapshots are never auto-purged)");
    Console.WriteLine("  list, list-snapshots                    List all snapshots (JSON)");
    Console.WriteLine("  delete-snapshot <id>                    Delete snapshot by ID");
    Console.WriteLine("  restore-snapshot <id>                   Restore files from snapshot");
    Console.WriteLine("  restore-points, list-restore-points    List system restore points (JSON)");
    Console.WriteLine("  create-restore-point [description]      Create a Windows system restore point");
    Console.WriteLine("  apply-restore-point <seq>               [DANGER] Apply restore point (requires restart)");
    Console.WriteLine("  pause [minutes]                         Pause protection (default 30 min)");
    Console.WriteLine("  resume                                  Resume protection");
    Console.WriteLine("  uninstall-cleanup                       Remove guard blocks & unlose-snapshot skills from agent configs");
    Console.WriteLine("  --help, -h                              Show this help");
    Console.WriteLine();
    Console.WriteLine("Exit codes: 0=success, 1=local error, 2=service returned failure envelope");
}

static async Task<string> HandleSnapshotAsync(UnlosePipeClient client, string[] args)
{
    var label      = GetNamedArg(args, "--label");
    var sourceTool = GetNamedArg(args, "--source-tool");
    var channel    = GetNamedArg(args, "--channel");
    var trigger    = GetNamedArg(args, "--trigger");
    var sessionId  = GetNamedArg(args, "--session");
    // Without --volume the snapshot follows config.snapshot.volumes (multi-volume); an explicit
    // --volume keeps single-volume behavior. Previously the default was a hard-coded C:\.
    var volume     = GetNamedArg(args, "--volume");
    var quiet      = args.Contains("--quiet");
    var pin        = args.Contains("--pin");
    var skipRecent = GetNamedArg(args, "--skip-if-recent");

    // --skip-if-recent N: skip creation if any snapshot already exists within the last N minutes
    // (session-baseline semantics only; explicit snapshots before dangerous operations NEVER
    // carry this flag — protection semantics must not be skipped)
    if (skipRecent is not null
        && double.TryParse(skipRecent, out var skipMinutes) && skipMinutes > 0)
    {
        var existing = await FindRecentSnapshotIdAsync(client, TimeSpan.FromMinutes(skipMinutes));
        if (existing is not null)
            return quiet ? existing : $"Snapshot skipped (recent snapshot exists): {existing}";
    }

    // When the caller generically self-reports "agent" (legacy instruction wording), resolve the real
    // host process name (e.g. kimi.exe); keep "agent" if probing fails
    if (string.Equals(sourceTool, "agent", StringComparison.OrdinalIgnoreCase))
        sourceTool = Unlose.Core.ProcessAncestry.ResolveCallerName("agent");

    var parameters = new Dictionary<string, string>();
    if (volume     is not null) parameters["volume"]      = volume;
    if (label      is not null) parameters["label"]       = label;
    if (sourceTool is not null) parameters["source_tool"] = sourceTool;
    if (channel    is not null) parameters["channel"]     = channel;
    if (sessionId  is not null) parameters["sessionId"]   = sessionId;
    // A self-reported source tool (agent invoking via memory/skill instructions) is recorded as
    // AgentInitiated; an explicit --trigger takes precedence
    if (trigger    is not null) parameters["triggerType"] = trigger;
    else if (sourceTool is not null) parameters["triggerType"] = "AgentInitiated";

    var raw = await client.SendCommandAsync("CREATE_SNAPSHOT", parameters);

    // BUG-CLI-003 (e2e finding 3) fix:
    // The service returns the whole PipeResponse envelope ({ Success, Data, ErrorMessage, RespondedAt });
    // only its Data field holds the inner JSON ({ id, createdAt, label, ... }).
    // The old code read id/createdAt from the envelope root, which always failed -> catch fell back to
    // printing the raw envelope -> users never saw the friendly "Snapshot created: ..." format.
    // Fix: unwrap the envelope first, parse the inner JSON from Data; on envelope failure return the error.
    PipeResponse? envelope = null;
    try { envelope = JsonSerializer.Deserialize<PipeResponse>(raw); }
    catch { /* not an envelope; print as-is */ }

    if (envelope is null)
        return raw;

    if (envelope.Success != true)
        return $"Snapshot creation failed: {envelope.ErrorMessage ?? "unknown error"}";

    string? snapshotId = null;
    string? createdAt  = null;
    string? snapshotLabel = null;
    if (!string.IsNullOrWhiteSpace(envelope.Data))
    {
        try
        {
            using var innerDoc = JsonDocument.Parse(envelope.Data);
            var root = innerDoc.RootElement;
            snapshotId    = root.TryGetProperty("id", out var idProp)        ? idProp.GetString()        : null;
            createdAt     = root.TryGetProperty("createdAt", out var atProp) ? atProp.GetString()        : null;
            snapshotLabel = root.TryGetProperty("label", out var lblProp)    ? lblProp.GetString()       : null;
        }
        catch
        {
            return envelope.Data;
        }
    }

    // --pin: pin immediately after creation (exempt from retention policy), for pre-dangerous-operation scenarios
    if (pin && snapshotId is not null)
    {
        var pinRaw = await client.SendCommandAsync("PIN_SNAPSHOT", new Dictionary<string, string>
        {
            ["id"] = snapshotId,
            ["pinned"] = "true"
        });
        if (TryGetEnvelopeFailure(pinRaw, out var pinErr))
            return $"Snapshot created: {snapshotId}  but pin failed: {pinErr}";
    }

    if (quiet)
        return snapshotId ?? "OK";

    if (snapshotId is null || createdAt is null)
        return string.IsNullOrWhiteSpace(envelope.Data)
            ? "Snapshot created (no detail returned by service)."
            : envelope.Data;

    return $"Snapshot created: {snapshotId}  at {createdAt}"
        + (snapshotLabel is not null ? $"  label={snapshotLabel}" : "")
        + (pin ? "  (pinned)" : "");
}

/// <summary>Returns the id of an existing snapshot within the last <paramref name="window"/>, if any (for --skip-if-recent).</summary>
static async Task<string?> FindRecentSnapshotIdAsync(UnlosePipeClient client, TimeSpan window)
{
    try
    {
        var raw = await client.SendCommandAsync("LIST_SNAPSHOTS");
        var envelope = JsonSerializer.Deserialize<PipeResponse>(raw);
        if (envelope?.Success != true || string.IsNullOrWhiteSpace(envelope.Data))
            return null;

        using var doc = JsonDocument.Parse(envelope.Data);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var cutoff = DateTime.UtcNow - window;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // The service serializes SnapshotRecord directly with PascalCase fields; tolerate camelCase too
            var hasAt = item.TryGetProperty("CreatedAt", out var atProp)
                     || item.TryGetProperty("createdAt", out atProp);
            if (!hasAt)
                continue;
            if (DateTime.TryParse(atProp.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                && at.ToUniversalTime() >= cutoff)
            {
                if (item.TryGetProperty("Id", out var idProp) || item.TryGetProperty("id", out idProp))
                    return idProp.GetString();
                return null;
            }
        }
        return null;
    }
    catch
    {
        // If the query fails, prefer taking one extra snapshot (protection first); do not block creation
        return null;
    }
}

/// <summary>
/// Uninstall cleanup (patent claim 11, the "standalone command, manually triggered" form): asks the service
/// to remove the guard instruction blocks from each user's global memory files via the paired markers, remove
/// the deployed unlose-snapshot skill packs, and restore the original content. The MSI uninstall custom action
/// also goes through this command.
/// </summary>
static async Task<string> HandleUninstallCleanupAsync(UnlosePipeClient client)
{
    var raw = await client.SendCommandAsync("UNINSTALL_CLEANUP");
    PipeResponse? envelope = null;
    try { envelope = JsonSerializer.Deserialize<PipeResponse>(raw); }
    catch { /* not an envelope; print as-is */ }

    if (envelope is null)
        return raw;
    if (envelope.Success != true)
        return $"Uninstall cleanup failed: {envelope.ErrorMessage ?? "unknown error"}";
    return string.IsNullOrWhiteSpace(envelope.Data)
        ? "Uninstall cleanup done (no detail returned)."
        : "Uninstall cleanup done.\n" + envelope.Data;
}

/// <summary>
/// If <paramref name="raw"/> deserializes to a failure envelope, returns true and surfaces a readable
/// error via <paramref name="message"/>. Used at the CLI top level to detect structured service
/// failures and set a non-zero exit code.
/// </summary>
static bool TryGetEnvelopeFailure(string raw, out string message)
{
    message = string.Empty;
    if (string.IsNullOrWhiteSpace(raw)) return false;
    try
    {
        var env = JsonSerializer.Deserialize<PipeResponse>(raw);
        if (env is null) return false;
        if (env.Success) return false;
        message = $"Service failure: {env.ErrorMessage ?? "(no error message)"}";
        return true;
    }
    catch
    {
        // Not envelope JSON / not a failure envelope; not a structured failure
        return false;
    }
}

static string? GetNamedArg(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

enum CommandKind
{
    Status,
    ListSnapshots,
    Snapshot,
    DeleteSnapshot,
    PinSnapshot,
    RestoreSnapshot,
    ListRestorePoints,
    CreateRestorePoint,
    ApplyRestorePoint,
    Pause,
    Resume,
    UninstallCleanup,
    Unknown,
}
