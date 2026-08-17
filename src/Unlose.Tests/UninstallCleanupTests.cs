using Unlose.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// Uninstall cleanup tests (patent claim 11): remove the guard instruction blocks from global memory
/// files by their paired markers, remove the deployed unlose-snapshot skill package, restore the original content, leave no residue.
/// </summary>
public class UninstallCleanupTests : IDisposable
{
    private readonly string _home;
    private readonly GlobalMemoryInjector _injector = new(NullLogger<GlobalMemoryInjector>.Instance);
    private readonly SkillDeployer _deployer = new(NullLogger<SkillDeployer>.Instance);

    public UninstallCleanupTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "unlose-clean-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public async Task Remove_RestoresOriginalUserContent_ByteIdentical()
    {
        // User's original content -> inject -> remove: must restore byte-for-byte
        var path = Path.Combine(_home, "AGENTS.md");
        var original = "# 我的项目约定\n自定义内容第一行\n\n## 另一节\n更多内容\n";
        await File.WriteAllTextAsync(path, original);

        await _injector.InjectIntoHomeAsync(_home);
        Assert.Contains(GlobalMemoryInjector.MarkerBegin, await File.ReadAllTextAsync(path));

        var results = await _injector.RemoveFromHomeAsync(_home);

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Contains(results, r => r.StartsWith("REMOVED universal"));
    }

    [Fact]
    public async Task Remove_DeletesFileCreatedByInjection()
    {
        // File was created by the injection (did not exist before): after removing the block nothing remains -> delete the whole file, no residue
        var path = Path.Combine(_home, "AGENTS.md");
        await _injector.InjectIntoHomeAsync(_home);
        Assert.True(File.Exists(path));

        await _injector.RemoveFromHomeAsync(_home);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Remove_KeepsFileWithoutGuardBlock()
    {
        var path = Path.Combine(_home, "AGENTS.md");
        var original = "# 只有用户内容，没有指令块\n";
        await File.WriteAllTextAsync(path, original);

        var results = await _injector.RemoveFromHomeAsync(_home);

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Contains(results, r => r.StartsWith("SKIP universal: no guard block"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RemoveGuardBlock_AnyVersionRemoved(int version)
    {
        // Version-agnostic prefix matching: marker blocks from any historical version can be removed
        var text = "前文\n\n<!-- unlose:snapshot-guard v" + version + " -->\n指令\n"
                 + GlobalMemoryInjector.MarkerEnd + "\n后文\n";
        var (cleaned, removed) = GlobalMemoryInjector.RemoveGuardBlock(text);
        Assert.True(removed);
        Assert.Equal("前文\n后文\n", cleaned);
    }

    [Fact]
    public void RemoveGuardBlock_IncompleteBlockKept()
    {
        // Only a begin marker with no end marker (user modified it): leave untouched to avoid collateral damage
        var text = "前文\n<!-- unlose:snapshot-guard v4 -->\n用户自己写的内容\n";
        var (cleaned, removed) = GlobalMemoryInjector.RemoveGuardBlock(text);
        Assert.False(removed);
        Assert.Equal(text, cleaned);
    }

    [Fact]
    public async Task Remove_IsIdempotent_SecondRunSkips()
    {
        await _injector.InjectIntoHomeAsync(_home);
        await _injector.RemoveFromHomeAsync(_home);

        var second = await _injector.RemoveFromHomeAsync(_home);
        Assert.Contains(second, r => r.StartsWith("SKIP universal: not present"));
    }

    [Fact]
    public async Task Remove_AlsoCleansAgentMemoryFile()
    {
        // kimi's actual config directory is .kimi-code (AgentRegistry InstallDir)
        Directory.CreateDirectory(Path.Combine(_home, ".kimi-code"));
        await _injector.InjectIntoHomeAsync(_home);
        Assert.True(File.Exists(Path.Combine(_home, ".kimi-code", "AGENTS.md")));

        var results = await _injector.RemoveFromHomeAsync(_home);

        Assert.False(File.Exists(Path.Combine(_home, ".kimi-code", "AGENTS.md")));
        Assert.Contains(results, r => r.StartsWith("REMOVED kimi"));
    }

    [Fact]
    public async Task SkillRemove_RemovesDeployedSkillDir()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        await _deployer.DeployIntoHomeAsync(_home);
        var skillDir = Path.Combine(_home, ".claude", "skills", "unlose-snapshot");
        Assert.True(Directory.Exists(skillDir));

        var results = await _deployer.RemoveFromHomeAsync(_home);

        Assert.False(Directory.Exists(skillDir));
        Assert.Contains(results, r => r.StartsWith("REMOVED claude"));
    }

    [Fact]
    public async Task SkillRemove_KeepsForeignContent()
    {
        // The directory contains the user's own same-named skill (no frontmatter): keep it, do not delete
        var skillDir = Path.Combine(_home, ".claude", "skills", "unlose-snapshot");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), "# 用户自己的技能\n");

        var results = await _deployer.RemoveFromHomeAsync(_home);

        Assert.True(Directory.Exists(skillDir));
        Assert.Contains(results, r => r.StartsWith("SKIP claude: foreign content"));
    }

    [Fact]
    public async Task SkillRemove_NotDeployed_SilentSkip()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        var results = await _deployer.RemoveFromHomeAsync(_home);
        Assert.Empty(results); // never deployed: stay silent, no uninstall output
    }
}
