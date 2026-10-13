namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_maintenance.go.
internal static class ClockMaintenance
{
    /// <summary>How many recent clock attempts retirement keeps (Go <c>clockHistoryTail</c>).</summary>
    public const uint HistoryTail = 128;

    /// <summary>
    /// Retires old clock attempts (Go <c>maintainClockAttempts</c>). It runs without the player gate. Retirement
    /// rechecks the catalog transactionally and retains unresolved writes and owned epochs. A concurrent
    /// allocation merely defers maintenance until the next poll.
    /// </summary>
    public static async Task MaintainAsync(StoreJournal journal, CancellationToken ct)
    {
        var head = await journal.ReadClockSequenceAsync(ct).ConfigureAwait(false);
        if (head.LastAllocated - head.RetiredThrough < HistoryTail)
        {
            return;
        }
        try
        {
            await journal.RetireClockHistoryAsync(head, HistoryTail, ct).ConfigureAwait(false);
        }
        catch (ConflictException)
        {
        }
    }
}
