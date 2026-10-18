using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/coupled-needs</c>: a hungry, tired, bored worker on a Work timetable beside ripe rice, under the served
/// brain. The native game harvests the rice and binds a delivery or meal to the harvested stack, the worker eats, sleeps,
/// wakes and plays horseshoes; then new hunger and independent rice lots need a second Method and a real meal.
/// test/portfolio_needs_prepare and _repeat prepare the facts (no task, bill, Method or assignment) and record the
/// native activities, which the primitives cannot express. A full natural rest takes about 20,000 ticks, so once sleep
/// is observed the case stages rested colonists in its place.
/// </summary>
internal static class ProductionCoupledNeeds
{
    private const string Prepare = "test/portfolio_needs_prepare";
    private const string RepeatTool = "test/portfolio_needs_repeat";
    private const string ReadTool = "test/portfolio_needs_read";

    private static readonly FixtureOp<Fx.StageReply, Fx.FixtureDone> Prepared = new(
        Prepare,
        r => ServedOps.Args(("worker", r.Pawns["worker"].Id), ("crop", r.Plants["crop"].Ids.Single())),
        ServedOps.Done(Prepare));
    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Repeat = ServedOps.Inject(RepeatTool);
    private static readonly FixtureOp<NoArgs, Fx.PortfolioNeedsRead> Read = ServedOps.Read<Fx.PortfolioNeedsRead>(ReadTool);

    // The hungry, tired, bored worker beside ripe rice with its stockpile, a bed and a horseshoes pin, on cleared ground
    // near the colonists; every colonist on a Work timetable with no work type enabled.
    private static readonly Fx.StageSpec Setup = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 10,
            Height = 3,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 10, 3), Kinds = [Fx.StageClearKind.Things] }],
        Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 1, 1, 1), Home = true }],
        Buildings =
        [
            new Fx.StageBuilding { Name = "bed", Def = "Bed", Cells = [new(9, 0)] },
            new Fx.StageBuilding { Name = "pin", Def = "HorseshoesPin", Cells = [new(7, 1)] },
        ],
        Plants = [new Fx.StagePlant { Name = "crop", Def = "Plant_Rice", Cells = [new(0, 1)] }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = "worker", Colonist = 0, Cell = new(2, 1), AllWork = 0, Timetable = Fx.StageTimeAssignment.Work,
                Needs = new Dictionary<string, float> { ["Food"] = .25f, ["Rest"] = .25f, ["Joy"] = .1f },
            },
            .. Enumerable.Range(1, 2).Select(i => new Fx.StagePawn
            {
                Name = "colonist" + i.ToString(CultureInfo.InvariantCulture), Colonist = i, AllWork = 0, Timetable = Fx.StageTimeAssignment.Work,
                Needs = new Dictionary<string, float> { ["Food"] = .95f, ["Rest"] = .95f, ["Joy"] = .95f },
            }),
        ],
        Zones = [new Fx.StageZone { Name = "rice", Cells = [new(4, 1)], Allow = ["RawRice"] }],
    };

    // Stands in for the hours of natural rest after the worker was seen asleep and recovering.
    private static readonly Fx.StageSpec Rested = new()
    {
        Pawns = [.. Enumerable.Range(0, 3).Select(i => new Fx.StagePawn { Name = "colonist" + i.ToString(CultureInfo.InvariantCulture), Colonist = i, Needs = new Dictionary<string, float> { ["Rest"] = 1f } })],
    };

    internal static TypedCase Case() => new()
    {
        Name = "production/coupled-needs",
        Scope = "Under the served brain a hungry, tired, bored worker on a Work timetable has ripe rice harvested and a delivery "
            + "or meal bound to the harvested stack, eats, sleeps, wakes and plays; then new hunger and independent rice lots "
            + "need a second Method and a real meal. Pure tests cannot prove real job completion, output identity or live authority.",
        Ticks = 20000,
        Tier = CaseTier.OnDemand,
        Ops = [Prepare, RepeatTool, ReadTool, FixtureStage.Tool],
        Stage = Setup,
        Keep = ["Food", "Rest", "Joy"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "coupled-needs" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("needs-prepare", Prepared, s.Staged!, cancellationToken).ConfigureAwait(false);
        var rested = false;
        var firstCycleMethods = -1;
        _ = await s.ServeUntilAsync("coupled-needs", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("needs-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("portfolio", ServedOps.Json(read));
            var signature = string.Create(CultureInfo.InvariantCulture, $"{read.Tick} cycle={(firstCycleMethods < 0 ? 1 : 2)}");
            if (firstCycleMethods < 0)
            {
                if (read.Slept && !rested)
                {
                    _ = await s.StageAsync("needs-rested", Rested, ct).ConfigureAwait(false);
                    rested = true;
                }

                if (read.Rebound && read.Ate && read.Slept && read.Woke && read.Played)
                {
                    s.Record("firstCycle", ServedOps.Json(read));
                    _ = await s.FixtureAsync("needs-repeat", Repeat, NoArgs.Value, ct).ConfigureAwait(false);
                    firstCycleMethods = read.Methods.Count;
                }

                return (signature, false);
            }

            return (signature, read.Ate && read.Methods.Count > firstCycleMethods);
        }, cancellationToken).ConfigureAwait(false);
    }
}
