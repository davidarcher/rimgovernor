using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/planned-site-deconstruct</c> (#3557): the construction activity read's planned-site answer over a ruin.
/// Native legality over another faction's door, its deconstructibility and the deconstruct job's work are game mechanics
/// a unit test cannot establish. <c>observations/planned-site-deconstruct-roof-holder</c> (#3617): the ruin is a lone wall
/// holding a roof the planned ring's footprint shares; the answer is still Feasible and its Deconstruct op carries the
/// roofed cells the wall leaves unsupported, which the game's collapse rules decide.
/// </summary>
internal static class PlannedSiteDeconstructCase
{
    private const string Stuff = "WoodLog";
    private const string Site = "planned/room1/wall/" + Stuff;

    // A 7x7 clear, unroofed site: the planned ring is the 5x5 rect at (1,1), its door at (3,1); an ancients' door (or, for
    // the roof-holder, a wall under a thin rock roof over it and the interior cell east of it) stands on the ring's west
    // wall cell (1,3).
    private static Fx.StageSpec Spec(bool roofHolder) => new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 7,
            Height = 7,
            OffsetX = 6,
            Radius = 15,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed, Fx.StageGround.Heavy],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 7, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Cells = roofHolder ? [new Fx.StageCells { Rect = new Fx.StageRect(1, 3, 2, 1), Roof = "RoofRockThin" }] : [],
        Buildings = [new Fx.StageBuilding { Name = "ruin", Def = Ruin(roofHolder), Stuff = Stuff, Cells = [new(1, 3)], Faction = Fx.StageFaction.Ancients }],
    };

    private static string Ruin(bool roofHolder) => roofHolder ? "Wall" : "Door";

    internal static TypedCase Case(bool roofHolder = false) => new()
    {
        Name = roofHolder ? "observations/planned-site-deconstruct-roof-holder" : "observations/planned-site-deconstruct",
        Scope = roofHolder
            ? "An ancients' wall on a cell of a planned wood wall ring, holding a thin rock roof over itself and the interior cell "
                + "beside it: the construction activity read answers the planned wall site Feasible (valued, not unknown) with one "
                + "Deconstruct clearing naming the wall whose Cells are the two roofed cells the wall leaves unsupported, and a known "
                + "per-pawn Finishes row located at a roof cell. Removal is native designation work; no exact order works it."
            : "An ancients' door on a cell of a planned wood wall ring: the construction activity read answers the planned wall site "
                + "Feasible with one Deconstruct clearing naming the door (Input its id, Resource its def) and a per-pawn Finishes "
                + "row through the native deconstruct work giver. Removal is native designation work; no exact order works it.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec(roofHolder),
        Ops = [FixtureStage.Tool],
        QuietWorld = true,
        Run = (s, ct) => RunAsync(s, roofHolder, ct),
    };

    private static async Task RunAsync(TypedSession s, bool roofHolder, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("test/stage replied no site");
        var ruin = staged.Buildings["ruin"].Ids.Single();
        Cell At(int x, int z) => new(site.X + x, site.Z + z);
        var door = At(3, 1);
        var ring = (from z in Enumerable.Range(1, 5) from x in Enumerable.Range(1, 5) where x is 1 or 5 || z is 1 or 5 select At(x, z)).Where(c => c != door).ToList();
        List<Cell> roof = roofHolder ? [At(1, 3), At(2, 3)] : [];

        var read = await s.CallAsync(
            "planned",
            NativeOps.ReadConstructionActivity,
            new RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest(s.Identity, default)
            {
                Planned = [new RimGovernor.Brain.GameOrders.Observations.PlannedConstructionSite(Site, "Wall", Stuff, Rotation.North, EquatableArray.From(ring))],
            },
            cancellationToken).ConfigureAwait(false);
        if (read.Planned.ToList() is not [var answer] || answer.Site != Site)
        {
            throw new AcceptanceException($"planned: want the one answer for {Site}, got [{string.Join(",", read.Planned.Select(a => a.Site))}]; findings {string.Join(",", read.Findings)}");
        }

        s.RecordWire("answer", answer);
        var problems = new List<string>();
        if (answer.Verdict != RimGovernor.Brain.GameFacts.Observations.PlannedSiteVerdict.Feasible)
        {
            problems.Add($"verdict {answer.Verdict}, findings {string.Join(",", read.Findings)}");
        }

        var expected = Site + "/deconstruct/" + ruin;
        if (answer.Clearing.Where(c => c.Kind == RimGovernor.Brain.GameFacts.Observations.PlannedClearingKind.Deconstruct).ToList() is not [var op] || op.Id != expected)
        {
            problems.Add($"clearing: want one Deconstruct op {expected}, got [{string.Join(",", answer.Clearing.Select(c => c.Kind + ":" + c.Id))}]");
        }
        else
        {
            if (!op.Options.Any(r => r.Input == ruin && r.Resource == Ruin(roofHolder) && r.WorkGiver.Length > 0 && r.Finish is RimGovernor.Brain.GameFacts.Observations.ConstructionFinish.Finishes { Ticks: > 0 } && (!roofHolder || roof.Contains(r.Location))))
            {
                problems.Add($"rows: want a pawn row naming {ruin} ({Ruin(roofHolder)}) with a known route and finish{(roofHolder ? " at a roof cell" : "")}, got [{string.Join(";", op.Options)}]");
            }

            if (op.Cells.Count != roof.Count || !roof.All(op.Cells.Contains))
            {
                problems.Add($"cells: want [{string.Join(",", roof)}], got [{string.Join(",", op.Cells)}]");
            }
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException("the planned site over the ruin is not estimated as a deconstruct clearing: " + string.Join("; ", problems));
        }
    }
}
