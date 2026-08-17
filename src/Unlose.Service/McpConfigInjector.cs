using System.Text;
using System.Text.Json.Nodes;
using Unlose.Core.Agents;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

/// <summary>
/// MCP client config injector (one of the three track-B channels, parallel to
/// GlobalMemoryInjector / SkillDeployer).
/// On service startup, idempotently injects the unlose MCP server into the JSON MCP
/// config (mcpServers object) of installed agents, making the agent's MCP channel
/// available automatically (initialize triggers an automatic "session start" snapshot
/// + the create_snapshot tool) without manual user configuration; on uninstall the
/// unlose key is removed, leaving no residue.
/// Only JSON-format configs (mcpServers object) are handled; other formats such as
/// TOML are not covered for now.
/// </summary>
public class McpConfigInjector
{
    private const string ServerKey = "unlose";
    private const string McpServersKey = "mcpServers";
    private readonly ILogger<McpConfigInjector> _logger;
    private readonly string _mcpserverExePath;

    /// <param name="logger">Logger.</param>
    /// <param name="mcpserverExePath">Full path of unlose.McpServer.exe (the injected command entry); defaults to the service install directory.</param>
    public McpConfigInjector(ILogger<McpConfigInjector> logger, string? mcpserverExePath = null)
    {
        _logger = logger;
        _mcpserverExePath = mcpserverExePath
            ?? Path.Combine(AppContext.BaseDirectory, "unlose.McpServer.exe");
    }

    /// <summary>Enumerate real user directories under Users on the system drive and inject into each (LocalSystem friendly).</summary>
    public async Task<IReadOnlyList<string>> InjectForAllUsersAsync(CancellationToken ct = default)
    {
        var results = new List<string>();
        foreach (var homeDir in RealUserHomes.Enumerate())
        {
            var r = await InjectIntoHomeAsync(homeDir, ct);
            foreach (var line in r)
                _logger.LogInformation("McpConfigInject [{User}]: {Result}", Path.GetFileName(homeDir), line);
            results.AddRange(r);
        }
        return results;
    }

    /// <summary>Enumerate real user directories under Users on the system drive and remove from each (uninstall cleanup).</summary>
    public async Task<IReadOnlyList<string>> RemoveForAllUsersAsync(CancellationToken ct = default)
    {
        var results = new List<string>();
        foreach (var homeDir in RealUserHomes.Enumerate())
        {
            var r = await RemoveFromHomeAsync(homeDir, ct);
            foreach (var line in r)
                _logger.LogInformation("McpConfigRemove [{User}]: {Result}", Path.GetFileName(homeDir), line);
            results.AddRange(r);
        }
        return results;
    }

    private async Task<IReadOnlyList<string>> InjectIntoHomeAsync(string homeDir, CancellationToken ct)
    {
        var results = new List<string>();
        foreach (var agent in AgentRegistry.All)
        {
            var relPath = agent.McpConfigFile;
            if (string.IsNullOrWhiteSpace(relPath)) continue;

            // Install evidence: the config file itself already exists (never create configs for uninstalled agents, to avoid pollution)
            var path = Path.Combine(homeDir, relPath);
            if (!File.Exists(path)) continue;

            try
            {
                var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
                var root = JsonNode.Parse(text) as JsonObject
                           ?? throw new InvalidDataException("not a JSON object");
                var servers = root[McpServersKey] as JsonObject
                              ?? new JsonObject();
                if (servers[ServerKey] is not null)
                {
                    results.Add($"SKIP {agent.Key}: mcpServers.unlose already present ({path})");
                    continue;
                }

                servers[ServerKey] = BuildMcpServerEntry();
                root[McpServersKey] = servers;
                await File.WriteAllTextAsync(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct);
                results.Add($"OK {agent.Key}: {path}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "McpConfigInject failed for {Agent} ({Path})", agent.Key, path);
                results.Add($"FAIL {agent.Key}: {ex.Message} ({path})");
            }
        }
        return results;
    }

    private async Task<IReadOnlyList<string>> RemoveFromHomeAsync(string homeDir, CancellationToken ct)
    {
        var results = new List<string>();
        foreach (var agent in AgentRegistry.All)
        {
            var relPath = agent.McpConfigFile;
            if (string.IsNullOrWhiteSpace(relPath)) continue;

            var path = Path.Combine(homeDir, relPath);
            if (!File.Exists(path)) continue;

            try
            {
                var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
                var root = JsonNode.Parse(text) as JsonObject;
                if (root?[McpServersKey] is JsonObject servers && servers[ServerKey] is not null)
                {
                    servers.Remove(ServerKey);
                    await File.WriteAllTextAsync(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct);
                    results.Add($"REMOVED {agent.Key}: {path}");
                }
                else
                {
                    results.Add($"SKIP {agent.Key}: no unlose mcp entry ({path})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "McpConfigRemove failed for {Agent} ({Path})", agent.Key, path);
                results.Add($"FAIL {agent.Key}: {ex.Message} ({path})");
            }
        }
        return results;
    }

    private JsonObject BuildMcpServerEntry() => new()
    {
        ["command"] = _mcpserverExePath,
        ["args"] = new JsonArray()
    };

}
