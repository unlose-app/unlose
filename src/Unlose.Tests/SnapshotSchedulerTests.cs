using Unlose.Service;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// Pure-function unit tests for SnapshotScheduler.GetDueSlot fixed-time scheduling decisions.
/// Semantics: among "today's already-due slots + yesterday's latest slot", pick the latest due slot after lastScheduled;
/// at most one catch-up run — no catch-up storm even after days of downtime.
/// </summary>
public class SnapshotSchedulerTests
{
    private static readonly TimeSpan[] DefaultSlots =
        [TimeSpan.FromHours(8), TimeSpan.FromHours(13), TimeSpan.FromHours(18)];

    private static readonly DateTime Today = new(2026, 8, 6); // local date; time of day specified per test

    [Fact]
    public void SingleDueSlot_WhenMorningAndNeverScheduled()
    {
        var now = Today.AddHours(9);
        var due = SnapshotScheduler.GetDueSlot(now, null, DefaultSlots);
        Assert.Equal(Today.AddHours(8), due);
    }

    [Fact]
    public void LatestDueSlot_WhenMultipleSlotsPassed()
    {
        var now = Today.AddHours(19);
        var due = SnapshotScheduler.GetDueSlot(now, null, DefaultSlots);
        Assert.Equal(Today.AddHours(18), due);
    }

    [Fact]
    public void NoDue_WhenSlotAlreadyCovered()
    {
        var now = Today.AddHours(9);
        var last = Today.AddHours(8).AddMinutes(30); // the 08:00 slot was already taken
        Assert.Null(SnapshotScheduler.GetDueSlot(now, last, DefaultSlots));
    }

    [Fact]
    public void LatestUncoveredSlot_WhenPartiallyCovered()
    {
        var now = Today.AddHours(19);
        var last = Today.AddHours(8); // only 08:00 was taken; 13:00/18:00 missed -> pick the latest, 18:00
        var due = SnapshotScheduler.GetDueSlot(now, last, DefaultSlots);
        Assert.Equal(Today.AddHours(18), due);
    }

    [Fact]
    public void YesterdayLastSlot_WhenBeforeFirstSlotOfDay()
    {
        var now = Today.AddHours(2); // starts at 2 AM; no slot is due yet today
        var due = SnapshotScheduler.GetDueSlot(now, null, DefaultSlots);
        Assert.Equal(Today.AddDays(-1).AddHours(18), due);
    }

    [Fact]
    public void NoDue_WhenYesterdayLastSlotAlreadyCovered()
    {
        var now = Today.AddHours(2);
        var last = Today.AddDays(-1).AddHours(18);
        Assert.Null(SnapshotScheduler.GetDueSlot(now, last, DefaultSlots));
    }

    [Fact]
    public void NoDue_WhenSlotsEmpty()
    {
        var now = Today.AddHours(9);
        Assert.Null(SnapshotScheduler.GetDueSlot(now, null, []));
    }

    [Fact]
    public void OnlyOneCatchUp_AfterMultiDayDowntime()
    {
        // Starting at 09:00 after 3 days of downtime: only catches up today's 08:00 slot, not the 9 slots across the 3 days
        var now = Today.AddHours(9);
        var last = Today.AddDays(-3).AddHours(18);
        var due = SnapshotScheduler.GetDueSlot(now, last, DefaultSlots);
        Assert.Equal(Today.AddHours(8), due);

        // After the catch-up run (last = today 08:00), the same slot is no longer due
        Assert.Null(SnapshotScheduler.GetDueSlot(now, Today.AddHours(8), DefaultSlots));
    }
}
