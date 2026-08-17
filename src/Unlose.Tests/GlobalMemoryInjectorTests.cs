using Unlose.Core.Agents;
using Unlose.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// GlobalMemoryInjector tests: on service startup, idempotently inject the unlose snapshot-guard
/// instruction block into ~/AGENTS.md and the global memory files of installed agents.
/// </summary>
public class GlobalMemoryInjectorTests : IDisposable
{
    private readonly string _home;
    private readonly GlobalMemoryInjector _injector = new(NullLogger<GlobalMemoryInjector>.Instance);

    public GlobalMemoryInjectorTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "unlose-inject-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public async Task Inject_CreatesUniversalAgentsMd()
    {
        await _injector.InjectIntoHomeAsync(_home);

        var path = Path.Combine(_home, "AGENTS.md");
        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains(GlobalMemoryInjector.MarkerBegin, content);
        // v4 instruction essentials: Agent self-reports its process name (persisted as AgentInitiated and recorded in the description) + session ID + restore guidance
        Assert.Contains("snapshot --source-tool <你的进程名>", content);
        Assert.Contains("--session", content);
        Assert.Contains("新会话开始", content);
    }

    [Fact]
    public async Task Inject_SkipsAgentsNotInstalled()
    {
        // Create no agent config directories -> no files should be produced besides ~/AGENTS.md
        await _injector.InjectIntoHomeAsync(_home);

        Assert.False(Directory.Exists(Path.Combine(_home, ".claude")));
        Assert.False(Directory.Exists(Path.Combine(_home, ".codex")));
        Assert.False(Directory.Exists(Path.Combine(_home, ".kimi-code")));
    }

    [Fact]
    public async Task Inject_WritesMemoryForInstalledAgentOnly()
    {
        // Simulate claude and kimi installed (config directories exist; kimi's actual directory is .kimi-code)
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(Path.Combine(_home, ".kimi-code"));

        await _injector.InjectIntoHomeAsync(_home);

        Assert.True(File.Exists(Path.Combine(_home, ".claude", "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(_home, ".kimi-code", "AGENTS.md")));
        Assert.False(Directory.Exists(Path.Combine(_home, ".codex")));
    }

    [Fact]
    public async Task Inject_IsIdempotent_SecondRunSkips()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));

        await _injector.InjectIntoHomeAsync(_home);
        var first = await File.ReadAllTextAsync(Path.Combine(_home, "AGENTS.md"));

        var results = await _injector.InjectIntoHomeAsync(_home);
        var second = await File.ReadAllTextAsync(Path.Combine(_home, "AGENTS.md"));

        Assert.Equal(first, second); // content unchanged
        Assert.Contains(results, r => r.StartsWith("SKIP universal"));
        Assert.Contains(results, r => r.StartsWith("SKIP claude"));
    }

    [Fact]
    public async Task Inject_PreservesExistingUserContent()
    {
        var path = Path.Combine(_home, "AGENTS.md");
        await File.WriteAllTextAsync(path, "# 我的项目约定\n自定义内容第一行");

        await _injector.InjectIntoHomeAsync(_home);

        var content = await File.ReadAllTextAsync(path);
        Assert.StartsWith("# 我的项目约定\n自定义内容第一行", content); // original content stays in front
        Assert.Contains(GlobalMemoryInjector.MarkerBegin, content);       // injected block appended after
    }

    [Fact]
    public async Task Inject_UpgradesV1BlockToV4_PreservingUserContent()
    {
        // Simulate a v1 instruction block injected by an old service version (with the user's own content around it)
        var path = Path.Combine(_home, "AGENTS.md");
        var v1 = "# 我的配置\n\n" + GlobalMemoryInjector.LegacyMarkerBeginV1 + "\n旧指令内容\n"
               + GlobalMemoryInjector.MarkerEnd + "\n\n# 块之后的用户内容\n";
        await File.WriteAllTextAsync(path, v1);

        var results = await _injector.InjectIntoHomeAsync(_home);

        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(GlobalMemoryInjector.LegacyMarkerBeginV1, content); // legacy marker removed
        Assert.DoesNotContain("旧指令内容", content);                             // old instruction block fully cleared
        Assert.Contains(GlobalMemoryInjector.MarkerBegin, content);               // v4 written
        Assert.Contains("snapshot --source-tool <你的进程名>", content);
        Assert.Contains("# 我的配置", content);                                   // preceding text preserved
        Assert.Contains("# 块之后的用户内容", content);                           // following text preserved
        Assert.Contains(results, r => r.StartsWith("UPGRADED universal"));

        // Running another round after migration should be an idempotent no-op
        var second = await _injector.InjectIntoHomeAsync(_home);
        Assert.Contains(second, r => r.StartsWith("SKIP universal"));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Inject_UpgradesLegacyBlockToV4(int legacyVersion)
    {
        // v2 (08-06 morning deployment) / v3 (08-06 noon deployment) blocks are likewise migrated wholesale to v4
        var legacyMarker = legacyVersion == 2
            ? GlobalMemoryInjector.LegacyMarkerBeginV2
            : GlobalMemoryInjector.LegacyMarkerBeginV3;
        var path = Path.Combine(_home, "AGENTS.md");
        var legacy = "# 我的配置\n\n" + legacyMarker + "\n旧指令内容\n"
               + GlobalMemoryInjector.MarkerEnd + "\n";
        await File.WriteAllTextAsync(path, legacy);

        var results = await _injector.InjectIntoHomeAsync(_home);

        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(legacyMarker, content);
        Assert.DoesNotContain("旧指令内容", content);
        Assert.Contains(GlobalMemoryInjector.MarkerBegin, content);
        Assert.Contains("# 我的配置", content);
        Assert.Contains(results, r => r.StartsWith("UPGRADED universal"));
    }

    [Fact]
    public void Registry_KimiQwenCopilot_ProcessNamesPresent()
    {
        // Agents explicitly named by the user as missing must be in the watched directories
        Assert.Contains("kimi", AgentRegistry.AllProcessNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("qwen", AgentRegistry.AllProcessNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("copilot", AgentRegistry.AllProcessNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("deepcode", AgentRegistry.AllProcessNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("qoder", AgentRegistry.AllProcessNames, StringComparer.OrdinalIgnoreCase);
    }
}
