using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// control/pawn-activity-cooking (#3398): a staged fueled stove with a competing bulk bill and the selected simple-meal
/// bill, two selected raw stacks holding one iteration plus spares, an unselected stack and a meal stockpile. Exact
/// recipe executions are installed under Auto authority; the first loses a selected stack and the second a fueled bench
/// after admission, and the explicit third revision cooks.
/// </summary>
internal static class ControlCooking
{
    private const string PrepareTool = "test/pawn_cooking_prepare";
    private const string SelectTool = "test/pawn_cooking_select";
    private const string ReadTool = "test/pawn_cooking_read";

    // Each attempt's bound on reaching its terminal state.
    private const int AttemptTicks = 2400;

    // The unselected stack's units and each bill's repeat count, which the executions must leave alone.
    private const int Unselected = 60;
    private const int Repeats = 2;

    private static readonly FixtureOp<Fx.StageReply, Fx.FixtureDone> Prepare = new(
        PrepareTool,
        r => ServedOps.Args(
            ("cook", r.Pawns["cook"].Id),
            ("bench", r.Buildings["stove"].Ids.Single()),
            ("selected", r.Bills["selected"]),
            ("other", r.Bills["bulk"]),
            ("first", r.Things["first"].Ids.Single()),
            ("second", r.Things["second"].Ids.Single()),
            ("unselected", r.Things["unselected"].Ids.Single())),
        ServedOps.Done(PrepareTool));

    private static readonly FixtureOp<int, Fx.FixtureDone> Select = new(SelectTool, attempt => ServedOps.Args(("attempt", attempt)), ServedOps.Done(SelectTool));
    private static readonly FixtureOp<NoArgs, Fx.CookingRead> Read = ServedOps.Read<Fx.CookingRead>(ReadTool);

    internal static TypedCase Case() => new()
    {
        Name = "control/pawn-activity-cooking",
        Scope = "Exact cooking consumes only authorized quantities from selected stacks and completes one selected recipe "
            + "iteration despite competing bill and ingredient options; a missing ingredient and a blocked bench fail visibly "
            + "with the inputs kept. A unit test cannot prove native consumption or food creation; fixture revisions are not "
            + "autonomous forecast replanning.",
        Ticks = 1 + (3 * AttemptTicks),
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 11,
                Height = 7,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 11, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Buildings = [new Fx.StageBuilding { Name = "stove", Def = "FueledStove", Cells = [new(5, 3)], Fuel = 50 }],
            Bills =
            [
                new Fx.StageBill { Name = "bulk", Building = "stove", Recipe = "CookMealSimpleBulk", RepeatCount = Repeats, DropOnFloor = true },
                new Fx.StageBill { Name = "selected", Building = "stove", Recipe = "CookMealSimple", RepeatCount = Repeats, DropOnFloor = true },
            ],
            Things =
            [
                new Fx.StageThing { Name = "first", Def = "RawRice", Count = 7, Cell = new(8, 3) },
                new Fx.StageThing { Name = "second", Def = "RawRice", Count = 7, Cell = new(2, 3) },
                new Fx.StageThing { Name = "unselected", Def = "RawRice", Count = Unselected, Cell = new(5, 5) },
            ],
            Zones = [new Fx.StageZone { Name = "meals", Cells = [new(0, 0)], Allow = ["MealSimple"] }],
            Pawns = [new Fx.StagePawn { Name = "cook", Colonist = 0 }],
        },
        Ops = [PrepareTool, SelectTool, ReadTool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            _ = await s.FixtureAsync("prepare", Prepare, s.Staged!, ct).ConfigureAwait(false);
            await TypedAuthority.GrantAutoAsync(s, ct).ConfigureAwait(false);
            // Native path grids gather at most once per game tick; let the staged stove settle.
            await s.RunTicksAsync("settle", 1, ct).ConfigureAwait(false);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                _ = await s.FixtureAsync("select-" + attempt, Select, attempt, ct).ConfigureAwait(false);
                var read = await WaitAsync(s, attempt, ct).ConfigureAwait(false);
                s.Record("attempt-" + attempt, ServedOps.Json(read));
                if (read.Execution?.Revision != attempt || read.Unselected != Unselected || read.OtherRemaining != Repeats)
                {
                    throw new AcceptanceException("cooking substituted authorization: " + ServedOps.Json(read));
                }

                if (attempt < 3)
                {
                    var reason = read.Execution.Reason;
                    var expected = attempt == 1
                        ? reason == "cooking_ingredient_unavailable"
                        : !read.BenchUsable && reason is "target_unreachable_or_reserved" or "cooking_bench_unavailable" or "Incompletable";
                    if (!expected || read.Meals != 0 || read.Raw != read.BeforeRaw || read.SelectedRemaining != Repeats)
                    {
                        throw new AcceptanceException("cooking did not keep its inputs and report the expected refusal: " + ServedOps.Json(read));
                    }
                }
                else if (read.Meals != read.ExpectedMeals || read.Raw != read.BeforeRaw - read.Required || read.SelectedRemaining != Repeats - 1)
                {
                    throw new AcceptanceException("missing exact physical recipe conversion: " + ServedOps.Json(read));
                }
            }
        },
    };

    // Runs until the attempt's terminal state: failed for the two injected refusals, completed for the revision.
    private static async Task<Fx.CookingRead> WaitAsync(TypedSession s, int attempt, CancellationToken cancellationToken)
    {
        var expected = attempt < 3 ? "failed" : "completed";
        Fx.CookingRead? last = null;
        _ = await s.RunUntilAsync("cook-" + attempt, AttemptTicks, async ct =>
        {
            last = await s.FixtureAsync("cooking", Read, NoArgs.Value, ct).ConfigureAwait(false);
            var state = last.Execution?.State;
            if (last.Forced || (state == "failed" && expected != "failed"))
            {
                throw new AcceptanceException($"cooking attempt {attempt} became player-forced or failed: {ServedOps.Json(last)}");
            }

            return ($"{state} {last.Raw} {last.Meals}", state == expected);
        }, cancellationToken).ConfigureAwait(false);
        return last!.Execution?.State == expected
            ? last
            : throw new AcceptanceException($"cooking attempt {attempt} did not reach {expected}: {ServedOps.Json(last)}");
    }
}
