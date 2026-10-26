using Fx = RimGovernor.Fixtures.Contracts;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>One observed feeding lifecycle per mode. Expected about one minute, under two with startup.</summary>
internal static class ChildFeedingResponse
{
    private static readonly FixtureOp<(string Carer, string Crop, bool Nursing), Fx.BabyFeedingResponseRead> Prepare = new(
        "test/feeding_response_prepare", a => ServedOps.Args(("carer", a.Carer), ("crop", a.Crop), ("nursing", a.Nursing)),
        ServedOps.Reply<Fx.BabyFeedingResponseRead>("test/feeding_response_prepare"));
    private static readonly FixtureOp<NoArgs, Fx.BabyFeedingResponseRead> Read = new(
        "test/feeding_response_read", _ => ServedOps.Args(), ServedOps.Reply<Fx.BabyFeedingResponseRead>("test/feeding_response_read"));

    private static readonly FixtureOp<NoArgs, Fx.BabyFeedingResponseRead> Admit = new(
        "test/feeding_response_admit", _ => ServedOps.Args(), ServedOps.Reply<Fx.BabyFeedingResponseRead>("test/feeding_response_admit"));

    internal static TypedCase Case(bool nursing) => new()
    {
        Name = nursing ? "child/coordinated-nursing" : "child/coordinated-bottle-feeding",
        Scope = "A hungry baby receives actual native " + (nursing ? "nursing" : "bottle-feeding")
            + " after native selection takes over from an observed journalled harvest Method. "
            + "New work is refused during feeding and safe return; no malnutrition occurs. The fixture stages hunger once after ownership, never feeding or release.",
        Ticks = 8000, Tier = CaseTier.OnDemand, Start = new LabStart { Colonists = 1 },
        Crew = new() { Size = 1 },
        Expansions = ["ludeon.rimworld.biotech"], QuietWorld = true, NoKeep = true, Keep = ["Food"],
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite { Width = 9, Height = 7, Near = Fx.StageAnchor.Colonist, OffsetX = 3,
                Radius = 30, Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged] },
            Clear = [new Fx.StageClear { Rect = new(0, 0, 9, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Buildings = [new Fx.StageBuilding { Name = "crib", Def = "Crib", Stuff = "WoodLog", Cells = [new(1, 1)] }],
            Plants = [new Fx.StagePlant { Name = "crop", Def = "Plant_Rice", Cells = [new(4, 3)] }],
            Things = nursing
                ? [new Fx.StageThing { Name = "adult-meals", Def = "MealSimple", Count = 4, Cell = new(2, 2) }]
                : [new Fx.StageThing { Name = "milk", Def = "Milk", Count = 75, Cell = new(2, 2) }],
            Pawns = [new Fx.StagePawn { Name = "carer", Colonist = 0, Cell = new(4, 2) }],
        },
        Ops = [Prepare.Tool, Read.Tool, Admit.Tool],
        Serve = new ServeSpec { Families = ["work"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = nursing ? "coordinated-nursing" : "coordinated-bottle" },
        Run = (s, ct) => RunAsync(s, nursing, ct),
    };

    private static async Task RunAsync(TypedSession s, bool nursing, CancellationToken ct)
    {
        var prepared = await s.FixtureAsync("prepare", Prepare, (s.Staged!.Pawns["carer"].Id, s.Staged.Plants["crop"].Ids.Single(), nursing), ct).ConfigureAwait(false);
        if (!prepared.Success || prepared.Baby.Length == 0 || prepared.Carer != s.Staged.Pawns["carer"].Id)
            throw new AcceptanceException($"invalid feeding setup: {prepared.Reason}");
        var last = prepared;
        try
        {
            _ = await s.ServeUntilAsync("feed-and-return", s.TicksLeft, async call =>
            {
                if (!last.HungerStaged)
                    _ = await s.FixtureAsync("admit-work", Admit, NoArgs.Value, call).ConfigureAwait(false);
                last = await s.FixtureAsync("observe", Read, NoArgs.Value, call).ConfigureAwait(false);
                if (!last.Success || last.Dead || last.Malnutrition > 0 || last.ReassignedDuringCare)
                    throw new AcceptanceException($"feeding harm or conflicting work: {ServedOps.Json(last)}");
                return (Wait.Signature(last.HungerStaged, last.Released, last.FeedingSeen, last.Intake, last.ReturnSeen, last.Finished), last.Finished);
            }, ct).ConfigureAwait(false);
        }
        catch (AcceptanceException)
        {
            var unfinished = await s.CallAsync("care-unfinished", NativeOps.ReadColonyFacts,
                new RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest { Scope = s.Scope }, ct).ConfigureAwait(false);
            s.Record("research-unfinished", OrdersJson.Serialize(unfinished.Forecast.Coupled.Research));
            s.Record("care-unfinished", OrdersJson.Serialize(unfinished.Forecast.Needs.Babies.Babies));
            s.Record("carer-unfinished", OrdersJson.Serialize(await TypedApply.PawnAsync(s, "carer-unfinished", prepared.Carer, ct).ConfigureAwait(false)));
            throw;
        }
        s.Record("feeding", ServedOps.Json(last));
        if (last.CompetingMethod.Length == 0 || !last.Released || !last.FeedingSeen || !last.Intake
            || !last.ReturnSeen || !last.FeedingProtected || !last.ReturnProtected || !last.Finished || last.Food <= .21)
            throw new AcceptanceException($"native feeding lifecycle not established: {ServedOps.Json(last)}");
    }
}
