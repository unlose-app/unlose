namespace Unlose.Core.Agents;

/// <summary>
/// Catalog of known AI coding agents (surveyed 2026-07).
/// Two purposes:
/// ① Process monitoring in AgentSessionManager (union with MonitoredProcesses in config.json,
///    so agents added in new versions are monitored without users changing config);
/// ② Global memory injection in GlobalMemoryInjector (on service startup, idempotently
///    appends the unlose snapshot-protection instruction block to each agent's global memory file).
///
/// Process names exclude .exe (both forms are matched, case-insensitive).
/// GlobalMemoryFiles are paths relative to the user home directory (~/); only paths backed
/// by public documentation are included. Agents without a global memory mechanism are registered
/// by process name only, with ~/AGENTS.md as fallback.
/// </summary>
public static class AgentRegistry
{
    public sealed record KnownAgent(
        string Key,
        string DisplayName,
        string[] ProcessNames,
        /// <summary>Directory that proves this agent is installed (relative to the user home directory); probed before injection to avoid creating config directories for agents that are not installed</summary>
        string? InstallDir,
        string[] GlobalMemoryFiles,
        /// <summary>Snapshot skill-pack deployment directory (relative to the user home directory, e.g. ".claude/skills"); null means there is no public skill-directory convention.
        /// Deployment treats the existence of its parent directory as proof of installation; independent of, and complementary to, the InstallDir probe.</summary>
        string? SkillDir = null,
        /// <summary>Command-line patterns (fallback when the process name cannot be matched, e.g. deepcode-cli runs hosted by node.exe).
        /// A hit on any pattern registers the session under the agent's canonical process name (ProcessNames[0]).</summary>
        string[]? CommandLinePatterns = null,
        /// <summary>MCP client configuration (relative to the user home directory; JSON containing an mcpServers object).
        /// The unlose MCP server is idempotently injected on service startup (McpConfigInjector); null means there is no public JSON MCP config convention.</summary>
        string? McpConfigFile = null);

    public static readonly IReadOnlyList<KnownAgent> All = new List<KnownAgent>
    {
        // ── Mainstream CLI agents (with documented global memory file conventions) ──
        new("claude",    "Claude Code",      ["claude"],               ".claude",            [".claude/CLAUDE.md"],              ".claude/skills",
            McpConfigFile: ".claude.json"),
        new("codex",     "OpenAI Codex CLI", ["codex"],                ".codex",             [".codex/AGENTS.md"],               ".codex/skills",
            McpConfigFile: ".codex/mcp.json"),
        new("gemini",    "Gemini CLI",       ["gemini", "gemini-cli"], ".gemini",            [".gemini/GEMINI.md"],              ".gemini/skills",
            CommandLinePatterns: ["gemini-cli"],
            McpConfigFile: ".gemini/settings.json"),
        new("kimi",      "Kimi Code CLI",    ["kimi"],                 ".kimi-code",         [".kimi-code/AGENTS.md"],           ".kimi-code/skills",
            McpConfigFile: ".kimi-code/mcp.json"),
        new("opencode",  "OpenCode",         ["opencode"],             ".config/opencode",   [".config/opencode/AGENTS.md"],     ".config/opencode/skills"),
        // qwen: QWEN.md is the official default memory file (AGENTS.md only takes effect with a
        // contextFileName setting; AGENTS.md is kept for users with that configuration and so that
        // historical injections can be cleaned up on uninstall)
        new("qwen",      "Qwen Code",        ["qwen"],                 ".qwen",              [".qwen/QWEN.md", ".qwen/AGENTS.md"], ".qwen/skills",
            CommandLinePatterns: ["qwen-code"],
            McpConfigFile: ".qwen/settings.json"),
        new("vibe",      "Vibe CLI",         ["vibe"],                 ".vibe",              [".vibe/AGENTS.md"],                ".vibe/skills"),
        new("amp",       "Amp",              ["amp"],                  ".config/amp",        [".config/amp/AGENTS.md"]),
        new("copilot",   "GitHub Copilot CLI", ["copilot"],            ".copilot",           [".copilot/copilot-instructions.md"], ".copilot/skills",
            McpConfigFile: ".copilot/mcp-config.json"),

        // ── IDE / China-based agents (global memory paths follow existing conventions) ──
        // windsurf: SkillDir (.codeium/windsurf/skills) is not publicly verified; kept pending official documentation
        new("windsurf",  "Windsurf",         ["windsurf"],             ".codeium",           [".codeium/windsurf/memories/global_rules.md"], ".codeium/windsurf/skills"),
        new("qoder",     "Qoder",            ["qoder", "qodercn", "qodercli"], ".qoder",        [".qoder/rules.md"],                ".qoder/skills",
            CommandLinePatterns: ["qodercli"]),
        // trae: the China edition's real image name is "Trae CN.exe" (verified on a live machine 2026-08-14; a space in the process name is valid) — do not change it to "traecn"
        new("trae",      "Trae",             ["trae", "trae cn"],       ".trae",              [".trae/rules.md"],                 ".trae/skills"),
        new("zcode",     "ZCode",            ["zcode"],                ".zcode",             [".zcode/rules.md"],                ".zcode/skills"),
        new("workbuddy", "WorkBuddy",        ["workbuddy"],            ".workbuddy",         [".workbuddy/rules.md"],            ".workbuddy/skills"),
        // kilocode: the official skill directory is ~/.kilo/skills (during the .kilocode→.kilo migration;
        // SkillDir parent-directory probing automatically limits deployment to users on the new platform,
        // while memory injection still uses .kilocode as proof of installation)
        new("kilocode",  "Kilo Code",        ["kilocode", "kilo"],      ".kilocode",          [".kilocode/rules/unlose-snapshot.md"], ".kilo/skills",
            CommandLinePatterns: ["kilocode"]),

        // ── Process monitoring only (no public global memory path; ~/AGENTS.md as fallback) ──
        new("cursor",      "Cursor",            ["cursor"],       null, []),
        new("codebuddy",   "CodeBuddy",         ["codebuddy"],    null, []),
        new("kiro",        "Kiro",              ["kiro"],         null, []),
        new("antigravity", "Antigravity",       ["antigravity"],  null, []),
        new("crush",       "Crush",             ["crush"],        null, []),
        new("aider",       "Aider",             ["aider"],        null, []),
        new("auggie",      "Augment Code",      ["auggie"],       null, [],
            CommandLinePatterns: ["augment.mjs"]),
        new("deepcode",    "DeepCode",          ["deepcode"],     ".deepcode", [".deepcode/AGENTS.md"], ".deepcode/skills",
            CommandLinePatterns: ["deepcode-cli", "deepcode"],
            McpConfigFile: ".deepcode/settings.json"),
        new("openclaw",    "OpenClaw",          ["openclaw"],     null, [],
            CommandLinePatterns: ["openclaw.mjs"]),
        new("pi",          "PI (Pi Coding)",    ["pi"],           null, [],
            CommandLinePatterns: ["pi-coding-agent"]),
        new("comate",      "Baidu Comate",      ["comate"],       null, []),
        new("devin",       "Devin",             ["devin"],        null, []),
        new("cline",       "Cline",             ["cline"],        null, [],
            CommandLinePatterns: ["cline"]),
        new("codewhale",   "CodeWhale",         ["codewhale"],    null, [],
            CommandLinePatterns: ["codewhale"]),

        // ── OpenClaw ecosystem variants for the China market (the "Lobster" family, surveyed 2026-08) ──
        // Note: 360 NamiClaw has no actual files after install, and ClawWork's network source is unreachable; neither is included
        new("qclaw",       "腾讯 QClaw",        ["qclaw"],        null, []),
        new("joyclaw",     "京东 JoyClaw",      ["joyclaw"],      null, []),
        new("easyclaw",    "灵宝 EasyClaw",     ["easyclaw"],     null, []),
    };

    /// <summary>All monitored process names (without .exe).</summary>
    public static IReadOnlyList<string> AllProcessNames =>
        All.SelectMany(a => a.ProcessNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether any agent requires command-line matching (determines whether process scanning enables command-line queries, to avoid unnecessary overhead)</summary>
    public static bool HasCommandLinePatterns =>
        All.Any(a => a.CommandLinePatterns is { Length: > 0 });

    /// <summary>
    /// Matches an agent by command-line pattern and returns its canonical process name (e.g. "deepcode.exe");
    /// used for agents hosted by generic runtimes such as node.exe (deepcode-cli runs in this form). Returns null when nothing matches.
    /// </summary>
    public static string? ResolveProcessNameByCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        foreach (var agent in All)
        {
            if (agent.CommandLinePatterns is null) continue;
            foreach (var pattern in agent.CommandLinePatterns)
            {
                if (commandLine.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return agent.ProcessNames[0] + ".exe";
            }
        }
        return null;
    }
}
