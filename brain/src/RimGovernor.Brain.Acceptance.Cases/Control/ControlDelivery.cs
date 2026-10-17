using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// control/pawn-activity-delivery (#3398): a staged hauler, a ripe rice crop in the Home area and two stockpile cells.
/// Under Auto authority an exact harvest is installed, then an exact delivery whose destination is blocked after
/// admission, then an explicit revision onto the other cell after the source stack is replaced.
/// </summary>
internal static class ControlDelivery
{
    private const string PrepareTool = "test/pawn_delivery_prepare";
    private const string HarvestTool = "test/pawn_delivery_harvest";
    private const string SelectTool = "test/pawn_delivery_select";
    private const string ReadTool = "test/pawn_delivery_read";

    // Each stage's bound on reaching its terminal state.
    private const int StageTicks = 1800;

    private static readonly FixtureOp<Fx.StageReply, Fx.FixtureDone> Prepare = new(
        PrepareTool,
        r => ServedOps.Args(
            ("hauler", r.Pawns["hauler"].Id),
            ("crop", r.Plants["crop"].Ids.Single()),
            ("destination", Cell(r, 7)),
            ("revised", Cell(r, 8))),
        ServedOps.Done(PrepareTool));

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Harvest = ServedOps.Inject(HarvestTool);
    private static readonly FixtureOp<bool, Fx.FixtureDone> Select = new(SelectTool, revised => ServedOps.Args(("revised", revised)), ServedOps.Done(SelectTool));
    private static readonly FixtureOp<NoArgs, Fx.DeliveryRead> Read = ServedOps.Read<Fx.DeliveryRead>(ReadTool);

    internal static TypedCase Case() => new()
    {
        Name = "control/pawn-activity-delivery",
        Scope = "A harvest produces edible food; native route and storage observations bind current stacks, including a "
            + "replacement stack after an interrupted delivery. Exact delivery refuses a newly blocked destination, and a new "
            + "explicit revision delivers only the selected quantity. A unit test cannot prove native carrying, splitting or "
            + "destination behavior; fixture revisions are not autonomous forecast replanning.",
        Ticks = 3 * StageTicks,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 9,
                Height = 1,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 20,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 9, 1), Kinds = [Fx.StageClearKind.Things] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 1, 1), Home = true }],
            Plants = [new Fx.StagePlant { Name = "crop", Def = "Plant_Rice", Cells = [new(0, 0)] }],
            Zones = [new Fx.StageZone { Name = "delivery", Cells = [new(7, 0), new(8, 0)], Allow = ["RawRice"] }],
            Pawns = [new Fx.StagePawn { Name = "hauler", Colonist = 0, Needs = new Dictionary<string, float> { ["Food"] = 1f, ["Rest"] = 1f } }],
        },
        Ops = [PrepareTool, HarvestTool, SelectTool, ReadTool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            _ = await s.FixtureAsync("prepare", Prepare, s.Staged!, ct).ConfigureAwait(false);
            await TypedAuthority.GrantAutoAsync(s, ct).ConfigureAwait(false);
            _ = await s.FixtureAsync("harvest", Harvest, NoArgs.Value, ct).ConfigureAwait(false);
            _ = await WaitAsync(s, "harvest", "completed", ct).ConfigureAwait(false);

            _ = await s.FixtureAsync("blocked-delivery", Select, false, ct).ConfigureAwait(false);
            var blocked = await WaitAsync(s, "blocked", "failed", ct).ConfigureAwait(false);
            s.Record("blocked", ServedOps.Json(blocked));
            if (blocked.Execution?.Reason != "delivery_unavailable" || blocked.Execution.Revision != 2
                || blocked.Destination != 0 || blocked.RevisedDestination != 0 || blocked.Source != blocked.HarvestedCount)
            {
                throw new AcceptanceException("the blocked delivery moved food or silently chose another destination: " + ServedOps.Json(blocked));
            }

            _ = await s.FixtureAsync("revised-delivery", Select, true, ct).ConfigureAwait(false);
            var delivered = await WaitAsync(s, "delivered", "completed", ct).ConfigureAwait(false);
            s.Record("delivered", ServedOps.Json(delivered));
            if (delivered.Execution?.Revision != 3 || delivered.Destination != 0 || delivered.RevisedDestination != 1
                || delivered.Source != delivered.HarvestedCount - 1)
            {
                throw new AcceptanceException("no exact physical delivery with conserved quantity: " + ServedOps.Json(delivered));
            }
        },
    };

    // The staged row's cell at <paramref name="x"/>, absolute, as the fixture's "x,z".
    private static string Cell(Fx.StageReply staged, int x)
    {
        var site = staged.Site ?? throw new AcceptanceException("the delivery stage found no site");
        return string.Create(CultureInfo.InvariantCulture, $"{site.X + x},{site.Z}");
    }

    // Runs until the stage's terminal state; any other failure or a player-forced job fails the case.
    private static async Task<Fx.DeliveryRead> WaitAsync(ITypedSession s, string stage, string expected, CancellationToken cancellationToken)
    {
        Fx.DeliveryRead? last = null;
        _ = await s.RunUntilAsync(stage, StageTicks, async ct =>
        {
            last = await s.FixtureAsync(stage + "-read", Read, NoArgs.Value, ct).ConfigureAwait(false);
            var state = last.Execution?.State;
            if (last.Forced || (state == "failed" && expected != "failed"))
            {
                throw new AcceptanceException($"{stage}: the delivery became player-forced or failed: {ServedOps.Json(last)}");
            }

            return ($"{state} {last.Source} {last.Destination} {last.RevisedDestination}", state == expected);
        }, cancellationToken).ConfigureAwait(false);
        return last!.Execution?.State == expected
            ? last
            : throw new AcceptanceException($"{stage} did not reach {expected}: {ServedOps.Json(last)}");
    }
}
