using Unlose.Core.Config;
using Unlose.Core.Data;
using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Unlose.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// SnapshotManager VSS-busy race handling tests:
/// on concurrent triggers (e.g. AgentPreSession hook + MCP session init), the later request waits on the lock and reuses the just-completed snapshot
/// instead of immediately failing with "Snapshot creation failed."; non-concurrent sequential requests are not coalesced.
/// </summary>
public class SnapshotManagerCoalesceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SnapshotManagerCoalesceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "unlose-coalesce-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "snapshots.db");
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ConcurrentCreate_SecondWaitsAndCoalescesToFirst()
    {
        await DatabaseInitializer.EnsureCreatedAsync(_dbPath);
        var repo = new SqliteRepository(_dbPath);
        var vss = new GatedVssGateway();
        var mgr = new SnapshotManager(
            NullLogger<SnapshotManager>.Instance, vss, repo, new EventBus(), new UnloseConfig());

        // First request holds VSS (simulates a slow shadow copy creation)
        var t1 = mgr.CreateAsync(TriggerType.AgentPreSession, triggerDetail: "kimi.exe (PID=1)", volumes: ["C:\\"]);
        await vss.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // Second request arrives while the first is in flight: it should queue and wait, not fail immediately
        var t2 = mgr.CreateAsync(TriggerType.AgentInitiated, triggerDetail: "kimi.exe (mcp)", volumes: ["C:\\"]);
        await Task.Delay(300);
        Assert.False(t2.IsCompleted, "VSS 忙时第二个请求不应立即返回（旧行为是立即失败）");

        vss.Release();
        var r1 = await t1.WaitAsync(TimeSpan.FromSeconds(10));
        var r2 = await t2.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal(r1!.Id, r2!.Id);   // coalesced: reuses the first snapshot
        Assert.Equal(1, vss.CreateCount); // underlying VSS created only once
    }

    [Fact]
    public async Task SequentialCreate_DoesNotCoalesce()
    {
        await DatabaseInitializer.EnsureCreatedAsync(_dbPath);
        var repo = new SqliteRepository(_dbPath);
        var vss = new GatedVssGateway { AutoRelease = true };
        var mgr = new SnapshotManager(
            NullLogger<SnapshotManager>.Instance, vss, repo, new EventBus(), new UnloseConfig());

        var r1 = await mgr.CreateAsync(TriggerType.AgentInitiated, volumes: ["C:\\"]);
        var r2 = await mgr.CreateAsync(TriggerType.Manual, volumes: ["C:\\"]);

        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.NotEqual(r1!.Id, r2!.Id);  // sequential requests each create their own snapshot, no coalescing
        Assert.Equal(2, vss.CreateCount);
    }

    /// <summary>Fake VSS gateway with controllable creation duration: with AutoRelease=false, creation blocks on Release().</summary>
    private sealed class GatedVssGateway : IVssGateway
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCount;
        public bool AutoRelease { get; init; }
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<SnapshotRecord> CreateShadowCopyAsync(string volumePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CreateCount);
            _entered.TrySetResult();
            if (!AutoRelease)
                await _release.Task.WaitAsync(ct);
            return new SnapshotRecord { Volumes = [volumePath], ShadowId = Guid.NewGuid().ToString() };
        }

        public Task DeleteShadowCopyAsync(string shadowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RestoreShadowCopyAsync(string shadowId, string targetVolume, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<IReadOnlyList<VssShadowInfo>> ListShadowCopiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VssShadowInfo>>([]);
        public Task<string?> MountShadowCopyAsync(string shadowId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>?> RestoreFilesFromShadowAsync(
            string shadowId, IReadOnlyList<string> relativePaths, string targetPath, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>?>([]);
    }
}
