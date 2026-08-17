using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Unlose.Core.Ipc;

namespace Unlose.McpServer;

internal sealed class PipeBackedMcpBridge
{
    private const string PipeName = "unlosePipe";

    private static readonly JsonArray ToolList = new(
        MakeTool("list_snapshots", "列出所有快照", new JsonObject()),
        MakeTool("create_snapshot", "创建文件系统保护快照。**新会话开始时必须调用一次**（安全要求，为会话建立基线，改坏/误删可回滚；服务端已自动兜底，本工具用于主动触发）；执行删除/覆盖等破坏性操作前也应调用。创建后用户可按会话定位并恢复误删误改的文件。",
            MakeProps(
                ("volume", "string", "目标卷路径，如 C:\\"),
                ("label", "string", "本次快照的备注（建议英文，避免经 agent 工具链传参时乱码），如 \"session-start\"、\"before-deletion\""),
                ("sessionId", "string", "你当前的会话/对话 ID，用于按会话定位快照"))),
        MakeTool("delete_snapshot", "删除快照", MakeProps(("id", "string", "快照 GUID"))),
        MakeTool("restore_snapshot", "恢复快照", MakeProps(("id", "string", "快照 GUID"))),
        MakeTool("pause_protection", "暂停保护", MakeProps(("minutes", "string", "暂停分钟数，默认 30"))),
        MakeTool("resume_protection", "恢复保护", new JsonObject()),
        MakeTool("status", "获取服务状态", new JsonObject())
    );

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8, leaveOpen: true);
        // JSON-RPC over stdio mandates no BOM: Encoding.UTF8 writes \ufeff at the stream head, which breaks strict clients
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
                break;

            string? id = null;
            try
            {
                var node = JsonNode.Parse(line) as JsonObject;
                if (node is null)
                    continue;

                id = node["id"]?.ToString();
                var method = node["method"]?.ToString() ?? string.Empty;
                var parameters = node["params"] as JsonObject;
                var result = await HandleMethodAsync(method, parameters, ct);
                // result == null means this is a notification (or a method without a response) — JSON-RPC forbids responding to notifications
                if (result is not null)
                    await writer.WriteLineAsync(OkResponse(id, result));
            }
            catch (Exception ex)
            {
                await writer.WriteLineAsync(ErrResponse(id, -32603, ex.Message));
            }
        }
    }

    /// <summary>MCP client initialization (= new session established): mechanism-level fallback that automatically
    /// creates a "session start" snapshot, without relying on the agent voluntarily following the guide
    /// (measured model compliance with skill wording is low).
    /// Idempotent: fires only once per McpServer process lifetime (per host process), avoiding duplication with AgentPreSession at process start.</summary>
    private static readonly object SessionInitLock = new();
    private static bool _sessionSnapshotCreated;

    private static Task<JsonNode> HandleInitializeAsync()
    {
        lock (SessionInitLock)
        {
            if (_sessionSnapshotCreated) return Task.FromResult<JsonNode>(InitializeResult());
            _sessionSnapshotCreated = true;
        }

        // The snapshot is sent asynchronously in the background and never blocks the handshake: a busy VSS queue/retry
        // may exceed the client's 30s timeout; the snapshot is only a protective fallback, handshake availability comes first.
        _ = Task.Run(() => TrySendSessionInitSnapshotAsync());

        return Task.FromResult<JsonNode>(InitializeResult());
    }

    private static async Task TrySendSessionInitSnapshotAsync()
    {
        try
        {
            // label follows the OS UI language (English on English systems); no "(MCP init)" suffix — the source channel is already conveyed by the (mcp) in triggerDetail, avoid duplication
            var zh = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var msg = CreatePipeMessage("CREATE_SNAPSHOT",
                ("volume", "C:\\"),
                ("triggerType", "AgentInitiated"),
                ("source_tool", ResolveCallerName()),
                ("channel", "mcp"),
                ("label", zh ? "新会话开始" : "Session start"));
            var resp = await SendPipeCommandAsync(msg, CancellationToken.None);
            if (!resp.Success)
                Console.Error.WriteLine($"[unlose-mcp] session-init snapshot failed: {resp.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[unlose-mcp] session-init snapshot error: {ex.Message}");
        }
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "unlose.McpServer",
            ["version"] = "1.0.0"
        }
    };

    private static async Task<JsonNode?> HandleMethodAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        // MCP notifications (e.g. notifications/initialized, mandatory in the standard handshake): the protocol forbids any response/error, so ignore silently
        if (method.StartsWith("notifications/", StringComparison.Ordinal))
            return null;

        return method switch
        {
            "initialize" => await HandleInitializeAsync(),
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = ToolList.DeepClone() },
            "tools/call" => await HandleToolCallAsync(parameters, ct),
            _ => throw new NotSupportedException($"Method not found: {method}")
        };
    }

    private static async Task<JsonNode> HandleToolCallAsync(JsonObject? parameters, CancellationToken ct)
    {
        var toolName = parameters?["name"]?.ToString() ?? string.Empty;
        var args = parameters?["arguments"] as JsonObject ?? new JsonObject();

        var msg = toolName switch
        {
            "list_snapshots" => CreatePipeMessage("LIST_SNAPSHOTS"),
            "create_snapshot" => BuildCreateSnapshotMessage(args),
            "delete_snapshot" => CreatePipeMessage("DELETE_SNAPSHOT", ("id", args["id"]?.ToString() ?? string.Empty)),
            "restore_snapshot" => CreatePipeMessage("RESTORE_SNAPSHOT", ("id", args["id"]?.ToString() ?? string.Empty)),
            "pause_protection" => CreatePipeMessage("PAUSE_PROTECTION", ("minutes", args["minutes"]?.ToString() ?? "30")),
            "resume_protection" => CreatePipeMessage("RESUME_PROTECTION"),
            "status" => CreatePipeMessage("STATUS"),
            _ => throw new NotSupportedException($"Unknown tool: {toolName}")
        };

        var response = await SendPipeCommandAsync(msg, ct);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = response.Success ? (response.Data ?? "OK") : $"Error: {response.ErrorMessage}"
            }),
            ["isError"] = !response.Success
        };
    }

    // MCP can only be invoked by an AI agent: always treat as agent-initiated; label/sessionId are forwarded only when non-empty, to avoid empty strings landing in the DB.
    // source_tool reports the host process name that launched this process (e.g. kimi.exe); the service uses it to show the source in the snapshot description.
    private static PipeMessage BuildCreateSnapshotMessage(JsonObject args)
    {
        var msg = CreatePipeMessage("CREATE_SNAPSHOT",
            ("volume", args["volume"]?.ToString() ?? "C:\\"),
            ("triggerType", "AgentInitiated"),
            ("source_tool", ResolveCallerName()),
            ("channel", "mcp"));
        var label = args["label"]?.ToString();
        if (!string.IsNullOrWhiteSpace(label)) msg.Parameters["label"] = label;
        var sessionId = args["sessionId"]?.ToString();
        if (!string.IsNullOrWhiteSpace(sessionId)) msg.Parameters["sessionId"] = sessionId;
        return msg;
    }

    private static PipeMessage CreatePipeMessage(string command, params (string Key, string Value)[] parameters)
    {
        var msg = new PipeMessage { Command = command };
        foreach (var (key, value) in parameters)
            msg.Parameters[key] = value;
        return msg;
    }

    // Host process name detection (the MCP server is launched by the agent host): shared implementation lives in Core/ProcessAncestry
    internal static string ResolveCallerName() => Unlose.Core.ProcessAncestry.ResolveCallerName("mcp");

    private static async Task<PipeResponse> SendPipeCommandAsync(PipeMessage msg, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct);

        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(msg));
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line))
            return new PipeResponse { Success = false, ErrorMessage = "No response from service." };

        return JsonSerializer.Deserialize<PipeResponse>(line) ?? new PipeResponse { Success = false, ErrorMessage = "Invalid response from service." };
    }

    private static string OkResponse(string? id, JsonNode result) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        });

    private static string ErrResponse(string? id, int code, string message) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        });

    private static JsonObject MakeTool(string name, string description, JsonObject inputSchema) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = inputSchema
            }
        };

    private static JsonObject MakeProps(params (string Name, string Type, string Description)[] props)
    {
        var obj = new JsonObject();
        foreach (var (name, type, description) in props)
        {
            obj[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = description
            };
        }
        return obj;
    }
}