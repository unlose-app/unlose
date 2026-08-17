using Unlose.Core.Agents;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Unlose.Service;

/// <summary>
/// Snapshot skill package deployer: on service startup, deploys the unlose-snapshot
/// skill (SKILL.md, directory form, with name/description frontmatter) into the skill
/// directories of installed agents under real user home directories.
///
/// Design notes (the second track-B delivery channel, parallel to GlobalMemoryInjector):
/// - Deploy only to installed agents: the existence of the skill directory's parent
///   (the agent config root, e.g. ~/.claude) is taken as evidence; never create
///   directories for uninstalled agents;
/// - Idempotent: skip when the target file content is identical;
/// - The skill body shares GuardInstructions with the global memory injection block,
///   keeping both channels consistent.
/// </summary>
public class SkillDeployer
{
    private readonly ILogger<SkillDeployer> _logger;
    private readonly string _cliExePath;

    public const string SkillDirName = "unlose-snapshot";

    /// <param name="cliExePath">Full path of the unlose CLI (the invocation entry in the skill instructions); defaults to the service install directory.</param>
    public SkillDeployer(ILogger<SkillDeployer> logger, string? cliExePath = null)
    {
        _logger = logger;
        _cliExePath = cliExePath ?? GuardInstructions.DefaultCliExePath();
    }

    /// <summary>Skill file content (frontmatter + instruction body).</summary>
    public string BuildSkill() => $"""
---
name: unlose-snapshot
description: 在新会话开始时、执行删除/覆盖等破坏性文件操作前，调用 unlose 快照接口为文件系统创建卷级保护快照
whenToUse: 新会话开始时；执行批量删除、递归删除、覆盖关键配置、数据库或 Schema 变更、大规模重构等高风险操作前
---

# unlose-snapshot — 文件系统快照防护

{GuardInstructions.BuildBody(_cliExePath, "skill")}
""";

    /// <summary>Deploy the skill package into the skill directories of installed agents under the specified user home directory. Returns one result line per target.</summary>
    public async Task<IReadOnlyList<string>> DeployIntoHomeAsync(string homeDir, CancellationToken ct = default)
    {
        var results = new List<string>();
        var content = BuildSkill();

        foreach (var agent in AgentRegistry.All)
        {
            if (agent.SkillDir is null) continue;

            var skillRoot = Path.Combine(homeDir, agent.SkillDir);
            // Install evidence: the skill directory's parent (agent config root) already exists; never create one for uninstalled agents
            var evidenceDir = Path.GetDirectoryName(skillRoot)!;
            if (!Directory.Exists(evidenceDir))
                continue;

            var destFile = Path.Combine(skillRoot, SkillDirName, "SKILL.md");
            try
            {
                if (File.Exists(destFile) &&
                    string.Equals(await File.ReadAllTextAsync(destFile, Encoding.UTF8, ct), content, StringComparison.Ordinal))
                {
                    results.Add($"SKIP {agent.Key}: already present ({destFile})");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                await File.WriteAllTextAsync(destFile, content, new UTF8Encoding(false), ct);
                results.Add($"OK {agent.Key}: {destFile}");
            }
            catch (Exception ex)
            {
                results.Add($"FAIL {agent.Key}: {ex.Message} ({destFile})");
            }
        }

        return results;
    }

    /// <summary>Enumerate real user directories under Users on the system drive and deploy into each (LocalSystem friendly).</summary>
    public async Task DeployForAllUsersAsync(CancellationToken ct = default)
    {
        foreach (var homeDir in RealUserHomes.Enumerate())
        {
            try
            {
                var results = await DeployIntoHomeAsync(homeDir, ct);
                foreach (var r in results)
                    _logger.LogInformation("SkillDeploy [{User}]: {Result}", Path.GetFileName(homeDir), r);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SkillDeploy [{User}] failed", Path.GetFileName(homeDir));
            }
        }
    }

    // ── Uninstall cleanup: remove the unlose-snapshot skill directories deployed by this service (residue-free uninstall) ──────────

    /// <summary>
    /// Remove the unlose-snapshot skill directory deployed under the specified user home
    /// directory. Only deletes content verifiably deployed by this service (SKILL.md with
    /// "name: unlose-snapshot" frontmatter); directories repurposed by the user are kept.
    /// Returns one result line per target.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemoveFromHomeAsync(string homeDir, CancellationToken ct = default)
    {
        var results = new List<string>();

        foreach (var agent in AgentRegistry.All)
        {
            if (agent.SkillDir is null) continue;

            var skillDir = Path.Combine(homeDir, agent.SkillDir, SkillDirName);
            try
            {
                if (!Directory.Exists(skillDir))
                    continue; // never deployed: skip silently (uninstall output stays quiet)

                var skillFile = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillFile) ||
                    !(await File.ReadAllTextAsync(skillFile, Encoding.UTF8, ct))
                        .Contains("name: unlose-snapshot", StringComparison.Ordinal))
                {
                    results.Add($"SKIP {agent.Key}: foreign content, kept ({skillDir})");
                    continue;
                }

                Directory.Delete(skillDir, recursive: true);
                results.Add($"REMOVED {agent.Key}: {skillDir}");
            }
            catch (Exception ex)
            {
                results.Add($"FAIL {agent.Key}: {ex.Message} ({skillDir})");
            }
        }

        return results;
    }

    /// <summary>Enumerate real user directories and remove the skill package from each, with logging (invoked via pipe by the uninstall custom action).</summary>
    public async Task<IReadOnlyList<string>> RemoveForAllUsersAsync(CancellationToken ct = default)
    {
        var all = new List<string>();
        foreach (var homeDir in RealUserHomes.Enumerate())
        {
            try
            {
                var results = await RemoveFromHomeAsync(homeDir, ct);
                foreach (var r in results)
                {
                    _logger.LogInformation("UninstallClean [{User}]: {Result}", Path.GetFileName(homeDir), r);
                    all.Add($"[{Path.GetFileName(homeDir)}] {r}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UninstallClean [{User}] failed", Path.GetFileName(homeDir));
                all.Add($"[{Path.GetFileName(homeDir)}] FAIL: {ex.Message}");
            }
        }
        return all;
    }
}
