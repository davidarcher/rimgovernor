using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>needs/target-capacity</c>: vanilla reservation capacity under the served brain. Two hungry pawns both eat from one
/// four-meal stack with overlapping reservations (JobDriver_Ingest reserves it with maxPawns 10 and a count); then two
/// tired pawns and one single-slot bed: the bed is held by at most its sleeping slots at once through a contest
/// (JobDriver_LayDown reserves SleepingSlotsCount). test/need_capacity_prepare and _bed prepare the stack and the bed
/// (no task, Method or order) and record the reservations, which the primitives cannot express.
/// </summary>
internal static class NeedsTargetCapacity
{
    private const string Prepare = "test/need_capacity_prepare";
    private const string BedTool = "test/need_capacity_bed";
    private const string ReadTool = "test/need_capacity_read";

    // How long both pawns must have been tired while one sleeps, for the second claimant to have been offered the bed.
    private const int BedContestTicks = 1500;

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Prepared = ServedOps.Inject(Prepare);
    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Bed = ServedOps.Inject(BedTool);
    private static readonly FixtureOp<NoArgs, Fx.NeedCapacityRead> Read = ServedOps.Read<Fx.NeedCapacityRead>(ReadTool);

    internal static TypedCase Case() => new()
    {
        Name = "needs/target-capacity",
        Scope = "Under the served brain two hungry pawns both eat from one four-meal stack with overlapping vanilla "
            + "reservations, then two tired pawns contest one single-slot bed and it is never held by more than its "
            + "sleeping slots. A unit test cannot prove vanilla's ReservationManager and JobDriver behavior, which the pure "
            + "capacity rule only models.",
        Ticks = 18000,
        Tier = CaseTier.OnDemand,
        Ops = [Prepare, BedTool, ReadTool],
        Crew = new Crew { Size = 2 },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "target-capacity" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("capacity-prepare", Prepared, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var bed = false;
        _ = await s.ServeUntilAsync("capacity", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("capacity-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record(bed ? "bed" : "stack", ServedOps.Json(read));
            var signature = string.Create(CultureInfo.InvariantCulture, $"{read.Tick} bed={bed}");
            if (!bed)
            {
                if (read.Ate >= 2 && read.MaxStackReservers >= 2)
                {
                    _ = await s.FixtureAsync("capacity-bed", Bed, NoArgs.Value, ct).ConfigureAwait(false);
                    bed = true;
                }

                return (signature, false);
            }

            // The bed's capacity is the vanilla slot count, observed from the game and not assumed here.
            if (read.MaxBedReservers > read.BedSlots || read.MaxBedSleepers > read.BedSlots)
            {
                throw new AcceptanceException($"the bed was held by more than its sleeping slots at once: {ServedOps.Json(read)}");
            }

            return (signature, read.BedSlots == 1 && read.BedSlept >= 1 && read.SinceBothTired >= BedContestTicks);
        }, cancellationToken).ConfigureAwait(false);
    }
}
