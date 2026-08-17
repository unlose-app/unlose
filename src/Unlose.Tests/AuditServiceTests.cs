using Unlose.Core.Models;
using Unlose.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Unlose.Tests;

public class AuditServiceTests
{
    private static AuditService Create() =>
        new(NullLogger<AuditService>.Instance);

    private static AuditEntry MakeEntry(string action = "TestAction", DateTime? timestamp = null) =>
        new()
        {
            Action    = action,
            Actor     = "unit-test",
            Details   = "details",
            Success   = true,
            Timestamp = timestamp ?? DateTime.UtcNow
        };

    // -----------------------------------------------------------------------
    // LogAsync / QueryAsync basics
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LogAsync_SingleEntry_CanBeQueried()
    {
        var svc   = Create();
        var entry = MakeEntry("ACTION_A");

        await svc.LogAsync(entry);

        var results = await svc.QueryAsync();
        Assert.Contains(results, e => e.Id == entry.Id);
    }

    [Fact]
    public async Task QueryAsync_NoFilter_ReturnsAllEntries()
    {
        var svc = Create();
        await svc.LogAsync(MakeEntry("A"));
        await svc.LogAsync(MakeEntry("B"));
        await svc.LogAsync(MakeEntry("C"));

        var results = await svc.QueryAsync();
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task QueryAsync_Empty_ReturnsEmptyList()
    {
        var svc     = Create();
        var results = await svc.QueryAsync();
        Assert.Empty(results);
    }

    // -----------------------------------------------------------------------
    // Time range filtering
    // -----------------------------------------------------------------------

    [Fact]
    public async Task QueryAsync_FromFilter_ExcludesOlderEntries()
    {
        var svc = Create();
        var old  = DateTime.UtcNow.AddHours(-2);
        var now  = DateTime.UtcNow;

        await svc.LogAsync(MakeEntry("OLD",  old));
        await svc.LogAsync(MakeEntry("NEW",  now));

        // Query only the last 1 hour
        var results = await svc.QueryAsync(from: DateTime.UtcNow.AddHours(-1));
        Assert.DoesNotContain(results, e => e.Action == "OLD");
        Assert.Contains(results, e => e.Action == "NEW");
    }

    [Fact]
    public async Task QueryAsync_ToFilter_ExcludesNewerEntries()
    {
        var svc    = Create();
        var future = DateTime.UtcNow.AddHours(1);
        var now    = DateTime.UtcNow;

        await svc.LogAsync(MakeEntry("NOW",    now));
        await svc.LogAsync(MakeEntry("FUTURE", future));

        // Up to before the current time
        var cutoff  = now.AddSeconds(1);
        var results = await svc.QueryAsync(to: cutoff);
        Assert.Contains(results, e => e.Action == "NOW");
        Assert.DoesNotContain(results, e => e.Action == "FUTURE");
    }

    [Fact]
    public async Task QueryAsync_FromAndToFilter_ReturnsOnlyMatchingEntries()
    {
        var svc = Create();
        var t0  = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await svc.LogAsync(MakeEntry("BEFORE", t0.AddMinutes(-1)));
        await svc.LogAsync(MakeEntry("INSIDE", t0.AddMinutes(30)));
        await svc.LogAsync(MakeEntry("AFTER",  t0.AddMinutes(61)));

        var results = await svc.QueryAsync(from: t0, to: t0.AddHours(1));
        Assert.Single(results);
        Assert.Equal("INSIDE", results[0].Action);
    }

    // -----------------------------------------------------------------------
    // Ordering (descending)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task QueryAsync_ResultsAreDescendingByTimestamp()
    {
        var svc = Create();
        var t0  = DateTime.UtcNow;

        await svc.LogAsync(MakeEntry("FIRST",  t0));
        await svc.LogAsync(MakeEntry("SECOND", t0.AddSeconds(1)));
        await svc.LogAsync(MakeEntry("THIRD",  t0.AddSeconds(2)));

        var results = await svc.QueryAsync();
        Assert.Equal("THIRD",  results[0].Action);
        Assert.Equal("SECOND", results[1].Action);
        Assert.Equal("FIRST",  results[2].Action);
    }

    // -----------------------------------------------------------------------
    // Concurrency safety
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LogAsync_ConcurrentWrites_AllEntriesPersisted()
    {
        var svc   = Create();
        const int count = 50;

        await Task.WhenAll(
            Enumerable.Range(0, count).Select(i =>
                svc.LogAsync(MakeEntry($"ACTION_{i}"))));

        var results = await svc.QueryAsync();
        Assert.Equal(count, results.Count);
    }
}
