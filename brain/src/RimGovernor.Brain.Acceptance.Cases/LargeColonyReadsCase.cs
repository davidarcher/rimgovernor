namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/large-colony-reads</c> (#3341): the main-thread time of the forecast's native reads once they cover
/// every colonist. The time a read takes on the game's main thread is a measurement a unit test cannot make.
/// </summary>
internal static class LargeColonyReadsCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "observations/large-colony-reads",
        Scope = "32 free colonists, five unplaced rooms (four 3x3 and one 12x12, a 144-cell floor), twenty wood stacks, eight meal stacks and eight beds "
            + "are injected; the one planned-site read, the one planned-route read, the needs read and the coupled read each answer every colonist "
            + "(the coupled read with routes), and each call's main-thread milliseconds are reported cold and warm. Records the time; does not assert a budget.",
        Start = new FixtureStart { Op = "test/large_colony_reads_prepare", On = new LabStart { Colonists = 1 } },
        RequiredOps = ["test/large_colony_reads_prepare", "test/large_colony_reads"],
        QuietWorld = true,
        Crew = new Crew { Size = 32 },
        Budget = TimeSpan.FromMinutes(2),
        Run = async (s, ct) =>
        {
            var read = await s.Harness.CallAsync("large-colony-reads", "test/large_colony_reads", [], ct).ConfigureAwait(false);
            if (read["success"]?.GetValue<bool>() != true) throw new AcceptanceException("Large-colony native reads failed: " + read.ToJson());
            s.Report["large_colony_reads"] = read.DeepClone();
        },
    };
}
