using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class NeedTargetCapacityCase
{
    // Phase windows in game ticks: eating one meal takes a few hundred ticks, and a one-bed contest needs the first
    // sleeper plus time for the second tired pawn to be offered the same bed. Hang guards, not latency budgets.
    private const double FoodPhaseTicks = 6000;
    private const double BedPhaseTicks = 12000;
    private const double BedContestTicks = 1500;

    internal static AcceptanceCase Case() => new()
    {
        Name = "needs/target-capacity",
        Scope = "Two hungry pawns and one four-meal stack enter ordinary review, shared selection, Hands claims and native bind-time reservation: both eat from the one stack with overlapping vanilla reservations (JobDriver_Ingest reserves the stack with maxPawns 10 and a count). Then two tired pawns and one single-slot bed: the bed is held by at most one pawn at a time and the second claimant is refused (JobDriver_LayDown reserves SleepingSlotsCount). A unit test cannot prove this: it checks vanilla's ReservationManager/JobDriver_Ingest behavior in the real game, which the pure capacity rule only models. No task, Method or order is installed by the fixture.",
        Start = new FixtureStart { Op = "test/need_capacity_prepare", On = new LabStart() },
        RequiredOps = ["test/need_capacity_prepare", "test/need_capacity_bed", "test/need_capacity_read"],
        Keep = ["Food", "Rest"], QuietWorld = true, Crew = new Crew { Size = 2 },
        Service = true, NoCheckpoint = true, Budget = TimeSpan.FromMinutes(6), Stall = TimeSpan.FromSeconds(45),
        Run = async (s, ct) =>
        {
            var run = await BrainRun.StartAsync(s, null, "target-capacity", "target-capacity", ct).ConfigureAwait(false);
            try
            {
                var bed = false;
                var wait = new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited };
                await wait.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("capacity-observe", "test/need_capacity_read", [], token).ConfigureAwait(false);
                    var tick = read["tick"].AsNumber();
                    var elapsed = read["elapsed"].AsNumber();
                    s.Report[bed ? "bed" : "stack"] = read;
                    if (!bed)
                    {
                        if (read["ate"].AsNumber() >= 2 && read["maxStackReservers"].AsNumber() >= 2)
                        {
                            s.Report["stackPhase"] = read.DeepClone();
                            s.Report["bedPrepared"] = await s.Harness.CallAsync("capacity-bed", "test/need_capacity_bed", [], token).ConfigureAwait(false);
                            bed = true;
                            return (Wait.Signature(tick), false);
                        }

                        if (elapsed >= FoodPhaseTicks)
                            throw new AcceptanceException($"Two pawns did not both eat the one stack with overlapping reservations within {FoodPhaseTicks} ticks: " + read.ToJson());
                        return (Wait.Signature(tick), false);
                    }

                    // The bed's capacity is the vanilla slot count, observed from the game and not assumed here.
                    var slots = read["bedSlots"].AsNumber();
                    if (read["maxBedReservers"].AsNumber() > slots || read["maxBedSleepers"].AsNumber() > slots)
                        throw new AcceptanceException("A single-slot bed was held by more than its sleeping slots at once: " + read.ToJson());
                    var contested = read["bedSlept"].AsNumber() >= 1 && read["sinceBothTired"].AsNumber() >= BedContestTicks;
                    if (!contested && elapsed >= BedPhaseTicks)
                        throw new AcceptanceException($"No pawn slept in the one bed through a {BedContestTicks}-tick contest within {BedPhaseTicks} ticks: " + read.ToJson());
                    return (Wait.Signature(tick), contested && slots == 1);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        },
    };
}
