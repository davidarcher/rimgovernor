using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/unassigned-runs-vanilla</c> (#3669): a controlled colonist with no committed coupled order runs vanilla job
/// selection under its work priorities. Granting Auto authority to a staged colonist with only PlantCutting enabled and a
/// player's CutPlant designation on a tree starts native <c>CutPlantDesignated</c>; no brain runs, so no review installs
/// anything first. A unit test cannot drive the native think tree under authority.
/// </summary>
internal static class ControlUnassignedVanilla
{
    private const string DesignateTool = "test/vanilla_chop_designate";
    private const string ReadTool = "test/vanilla_chop_read";

    // Walking to the tree and starting the cut take well under a game hour.
    private const int Budget = 1200;

    private static readonly FixtureOp<string, Fx.FixtureDone> Designate = new(
        DesignateTool, plant => ServedOps.Args(("plant", plant)), ServedOps.Done(DesignateTool));

    private static readonly FixtureOp<string, Fx.VanillaJobRead> Read = new(
        ReadTool, pawn => ServedOps.Args(("pawn", pawn)), ServedOps.Reply<Fx.VanillaJobRead>(ReadTool));

    // A grown oak five cells from the colonist, which only cuts plants and works every hour.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 8,
            Height = 3,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 8, 3), Kinds = [Fx.StageClearKind.Things] }],
        Plants = [new Fx.StagePlant { Name = "tree", Def = "Plant_TreeOak", Cells = [new(6, 1)] }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = "chopper", Colonist = 0, Cell = new(1, 1), AllWork = 0,
                Work = new Dictionary<string, int> { ["PlantCutting"] = 1 }, Timetable = Fx.StageTimeAssignment.Work,
            },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "control/unassigned-runs-vanilla",
        Scope = "Issue #3669: a controlled colonist with no committed coupled order runs vanilla job selection. Granting Auto "
            + "authority to a staged colonist with a CutPlant designation and no coupled work starts CutPlantDesignated with no "
            + "brain review in between. A unit test cannot drive the native think tree under authority.",
        Ticks = Budget,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        Ops = [DesignateTool, ReadTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var chopper = s.Staged!.Pawns["chopper"].Id;
        _ = await s.FixtureAsync("designate", Designate, s.Staged.Plants["tree"].Ids.Single(), cancellationToken).ConfigureAwait(false);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("chop", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("job", Read, chopper, ct).ConfigureAwait(false);
            if (read.Owned)
            {
                throw new AcceptanceException($"a Hands execution owns the colonist, though no brain runs: {ServedOps.Json(read)}");
            }

            return (read.Job, read.Job == "CutPlantDesignated");
        }, cancellationToken).ConfigureAwait(false);
    }
}
