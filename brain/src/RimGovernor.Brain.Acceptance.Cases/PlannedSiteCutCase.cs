using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/planned-site-cut</c> (#3613): the construction activity read's planned-site answer over trees. The Cut
/// clearing op names the plants it clears (Targets, lowest id first), what the brain binds its Work/CutPlant set order
/// over; native legality over plants and the cut job's work are game mechanics a unit test cannot establish.
/// </summary>
internal static class PlannedSiteCutCase
{
    private const string Stuff = "WoodLog";
    private const string Tree = "Plant_TreeOak";
    private const string Site = "planned/room1/wall/" + Stuff;

    // A 7x7 clear, unroofed site: the planned ring is the 5x5 rect at (1,1), its door at (3,1); two oaks stand on the
    // ring's west wall cells (1,3) and (1,4).
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
        Plants = [new Fx.StagePlant { Name = "trees", Def = Tree, Cells = [new(1, 3), new(1, 4)], Growth = 0.3f }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "observations/planned-site-cut",
        Scope = "Two oaks on cells of a planned wood wall ring: the construction activity read answers the planned wall site Feasible "
            + "with one Cut clearing whose Targets are the two oaks (lowest id first) and a per-pawn Finishes row through the "
            + "native plant-cut work giver. Does not prove the brain's binding or the job (control/pawn-activity-cut-designated).",
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
        var trees = staged.Plants["trees"].Ids.ToList();
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

        if (answer.Clearing.Where(c => c.Kind == Observations.PlannedClearingKind.Cut).ToList() is not [var op] || op.Id != Site + "/cut")
        {
            problems.Add($"clearing: want one Cut op {Site}/cut, got [{string.Join(",", answer.Clearing.Select(c => c.Kind + ":" + c.Id))}]");
        }
        else
        {
            static long Number(string id) => long.Parse(new string(id.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()), System.Globalization.CultureInfo.InvariantCulture);
            if (op.Targets.Count != 2 || !trees.All(op.Targets.Contains) || Number(op.Targets[0]) >= Number(op.Targets[1]))
            {
                problems.Add($"targets: want the two oaks [{string.Join(",", trees)}] lowest id first, got [{string.Join(",", op.Targets)}]");
            }

            if (!op.Options.Any(r => r.WorkGiver.Length > 0 && r.Finish is Observations.ConstructionFinish.Finishes { Ticks: > 0 }))
            {
                problems.Add($"rows: want a pawn row with a work giver, a known route and finish, got [{string.Join(";", op.Options)}]");
            }
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException("the planned site over the oaks is not estimated as a cut clearing naming them: " + string.Join("; ", problems));
        }
    }
}
