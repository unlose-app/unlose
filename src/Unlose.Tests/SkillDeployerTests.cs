using Unlose.Core.Agents;
using Unlose.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// SkillDeployer tests: on service startup, deploy unlose-snapshot/SKILL.md into the skill directories of installed agents.
/// Convention: deploy only to installed agents (evidenced by the existence of the skill directory's parent), idempotent, instructions share the same body as the memory injection.
/// </summary>
public class SkillDeployerTests : IDisposable
{
    private readonly string _home;
    private readonly SkillDeployer _deployer = new(NullLogger<SkillDeployer>.Instance);

    public SkillDeployerTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "unlose-skill-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private string SkillPath(string skillDir) =>
        Path.Combine(_home, skillDir, SkillDeployer.SkillDirName, "SKILL.md");

    [Fact]
    public async Task Deploy_SkipsAgentsNotInstalled()
    {
        // Create no agent config directories -> no skill files should be produced
        var results = await _deployer.DeployIntoHomeAsync(_home);

        Assert.Empty(results);
        Assert.False(Directory.Exists(Path.Combine(_home, ".claude")));
        Assert.False(Directory.Exists(Path.Combine(_home, ".kimi-code")));
    }

    [Fact]
    public async Task Deploy_WritesSkillForInstalledAgentOnly()
    {
        // Simulate claude (~/.claude exists) and kimi (~/.kimi-code exists) installed; zcode not installed
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(Path.Combine(_home, ".kimi-code"));

        var results = await _deployer.DeployIntoHomeAsync(_home);

        Assert.True(File.Exists(SkillPath(".claude/skills")));
        Assert.True(File.Exists(SkillPath(".kimi-code/skills")));
        Assert.False(File.Exists(SkillPath(".zcode/skills")));
        Assert.Contains(results, r => r.StartsWith("OK claude"));
        Assert.Contains(results, r => r.StartsWith("OK kimi"));
        Assert.DoesNotContain(results, r => r.Contains("zcode"));
    }

    [Fact]
    public async Task Deploy_SkillContentHasFrontmatterAndGuardInstructions()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));

        await _deployer.DeployIntoHomeAsync(_home);

        var content = await File.ReadAllTextAsync(SkillPath(".claude/skills"));
        Assert.StartsWith("---", content);                       // frontmatter (required for the directory-form SKILL.md)
        Assert.Contains("name: unlose-snapshot", content);
        Assert.Contains("description:", content);
        Assert.Contains("snapshot --source-tool <你的进程名>", content); // same instruction body as the memory injection
        Assert.Contains("--channel skill", content);                     // the skill package self-reports the skill channel
        Assert.Contains("--session", content);
    }

    [Fact]
    public async Task Deploy_IsIdempotent_SecondRunSkips()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));

        await _deployer.DeployIntoHomeAsync(_home);
        var first = await File.ReadAllTextAsync(SkillPath(".claude/skills"));

        var results = await _deployer.DeployIntoHomeAsync(_home);
        var second = await File.ReadAllTextAsync(SkillPath(".claude/skills"));

        Assert.Equal(first, second);
        Assert.Contains(results, r => r.StartsWith("SKIP claude"));
    }

    [Fact]
    public void Registry_SkillDirsRegisteredForKnownAgents()
    {
        // kimi/zcode installed in the user environment must have skill directory registration (e2e verification target)
        Assert.Contains(AgentRegistry.All, a => a.Key == "kimi" && a.SkillDir != null);
        Assert.Contains(AgentRegistry.All, a => a.Key == "zcode" && a.SkillDir != null);
        Assert.Contains(AgentRegistry.All, a => a.Key == "claude" && a.SkillDir == ".claude/skills");
    }
}
