using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/planned-site-deconstruct</c> (#3557): the construction activity read's planned-site answer over a ruin.
/// Native legality over another faction's door, its deconstructibility and the deconstruct job's work are game mechanics
/// a unit test cannot establish.
/// </summary>
internal static class PlannedSiteDeconstructCase
{
    private const string Stuff = "WoodLog";
    private const string Ruin = "Door";
    private const string Site = "planned/room1/wall/" + Stuff;

    // A 7x7 clear, unroofed site: the planned ring is the 5x5 rect at (1,1), its door at (3,1); an ancients' door stands
    // on the ring's west wall cell (1,3).
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 7,
            Height = 7,
            OffsetX = 6,
            Radius = 15,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 7, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Buildings = [new Fx.StageBuilding { Name = "ruin", Def = Ruin, Stuff = Stuff, Cells = [new(1, 3)], Faction = Fx.StageFaction.Ancients }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "observations/planned-site-deconstruct",
        Scope = "An ancients' door on a cell of a planned wood wall ring: the construction activity read answers the planned wall site "
            + "Feasible with one Deconstruct clearing naming the door (Input its id, Resource its def) and a known per-pawn FinishTicks "
            + "row through the native deconstruct work giver. Does not prove the brain's binding or the job (control/pawn-activity-deconstruct).",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("test/stage replied no site");
        var ruin = staged.Buildings["ruin"].Ids.Single();
        Cell At(int x, int z) => new(site.X + x, site.Z + z);
        var door = At(3, 1);
        var ring = (from z in Enumerable.Range(1, 5) from x in Enumerable.Range(1, 5) where x is 1 or 5 || z is 1 or 5 select At(x, z)).Where(c => c != door).ToList();

        var read = await s.CallAsync(
            "planned",
            NativeOps.ReadConstructionActivity,
            new Observations.ConstructionActivityRequest(s.Identity, default)
            {
                Planned = [new Observations.PlannedConstructionSite(Site, "Wall", Stuff, Rotation.North, EquatableArray.From(ring))],
            },
            cancellationToken).ConfigureAwait(false);
        if (read.Planned.ToList() is not [var answer] || answer.Site != Site)
        {
            throw new AcceptanceException($"planned: want the one answer for {Site}, got [{string.Join(",", read.Planned.Select(a => a.Site))}]; findings {string.Join(",", read.Findings)}");
        }

        s.RecordWire("answer", answer);
        var problems = new List<string>();
        if (answer.Verdict != Observations.PlannedSiteVerdict.Feasible)
        {
            problems.Add($"verdict {answer.Verdict}, findings {string.Join(",", read.Findings)}");
        }

        var expected = Site + "/deconstruct/" + ruin;
        if (answer.Clearing.Where(c => c.Kind == Observations.PlannedClearingKind.Deconstruct).ToList() is not [var op] || op.Id != expected)
        {
            problems.Add($"clearing: want one Deconstruct op {expected}, got [{string.Join(",", answer.Clearing.Select(c => c.Kind + ":" + c.Id))}]");
        }
        else if (!op.Options.Any(r => r.Input == ruin && r.Resource == Ruin && r.WorkGiver.Length > 0 && r.SiteApproachTicks.IsKnown
            && r.FinishTicks.IsKnown && r.FinishTicks.Value > 0))
        {
            problems.Add($"rows: want a pawn row naming {ruin} ({Ruin}) with a known route and finish, got [{string.Join(";", op.Options)}]");
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException("the planned site over the ruin is not estimated as a deconstruct clearing: " + string.Join("; ", problems));
        }
    }
}
