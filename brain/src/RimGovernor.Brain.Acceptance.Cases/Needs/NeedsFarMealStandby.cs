using System.Globalization;
using RimGovernor.Brain.Telemetry;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>needs/far-meal-standby</c> (#3649): under the served brain a colonist whose next meal departs well in the future is
/// not held waiting for it. It is left to vanilla job selection (#3669); once its hunger is near the meal's departure, a
/// coupled meal order takes it and it eats before it turns hungry. The live hunger fall and the native
/// meal are what a unit test cannot drive.
/// </summary>
internal static class NeedsFarMealStandby
{
    private const int Budget = 8000;

    // Fed enough that a meal now would waste most of its nutrition: the forecast meal departs hours ahead.
    private const float Fed = .6f;

    // Stands in for the hours of hunger fall until the meal is due: about 0.027 per 1000 ticks leaves some 3700 ticks
    // before the colonist turns hungry.
    private const float Due = .35f;

    // Eaten on time: before the colonist turned hungry (RimWorld's Hungry band starts at 0.25).
    private const double Late = .25;

    // A 16x5 site with a meal and the colonist at its east end.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 8 },
        Site = new Fx.StageSite
        {
            Width = 16,
            Height = 5,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 16, 5), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Things =
        [
            new Fx.StageThing { Name = "meal", Def = "MealSimple", Count = 1, Cell = new(12, 3) },
        ],
        Pawns = [new Fx.StagePawn { Name = "eater", Colonist = 0, Cell = new(13, 2), Needs = new Dictionary<string, float> { ["Food"] = Fed } }],
    };

    private static readonly Fx.StageSpec Hungrier = new()
    {
        Pawns = [new Fx.StagePawn { Name = "eater", Colonist = 0, Needs = new Dictionary<string, float> { ["Food"] = Due } }],
    };

    private static readonly FixtureOp<string, Fx.FrozenNeedsRead> Levels = new(
        Needs.FreezeNeedsTool,
        action => ServedOps.Args(("action", action)),
        ServedOps.Reply<Fx.FrozenNeedsRead>(Needs.FreezeNeedsTool));

    internal static TypedCase Case() => new()
    {
        Name = "needs/far-meal-standby",
        Scope = "Issue #3649: under the served brain a colonist whose meal departs hours ahead is left to vanilla "
            + "job selection (#3669) instead of being held, and once its hunger nears the meal's departure it eats before turning hungry. A unit test "
            + "cannot drive the live hunger fall or the native meal.",
        Ticks = Budget,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        Ops = [FixtureStage.Tool, Needs.FreezeNeedsTool],
        Keep = ["Food"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "far-meal-standby" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var eater = s.Staged!.Pawns["eater"].Id;
        double? last = null;
        var lowest = double.MaxValue;
        double? ateFrom = null;
        async Task<double> FoodAsync(CancellationToken ct)
        {
            var read = await s.FixtureAsync("levels", Levels, "inspect", ct).ConfigureAwait(false);
            var food = read.Needs.Single(p => p.Pawn == eater).Needs.Single(n => n.Def == "Food").Level;
            if (last is { } before && food > before + .05) ateFrom ??= lowest;
            lowest = Math.Min(lowest, food);
            last = food;
            return food;
        }

        // The meal is hours ahead: the colonist runs vanilla work rather than being held for it.
        _ = await s.ServeUntilAsync("released", s.TicksLeft, async ct =>
        {
            var food = await FoodAsync(ct).ConfigureAwait(false);
            var released = Released(s, eater);
            return (string.Create(CultureInfo.InvariantCulture, $"food={food:0.000} released={released} ate={ateFrom is not null}"), released || ateFrom is not null);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("releasedBy", Budget - s.TicksLeft);
        if (ateFrom is { } early)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the colonist ate at food {early:0.000} before it ran vanilla work: its meal was not far ahead"));
        }

        if (!Released(s, eater))
        {
            throw new AcceptanceException("the colonist ran no vanilla job after a review while its meal was far ahead");
        }

        // Its meal is now due: a coupled meal order takes the colonist.
        _ = await s.StageAsync("due", Hungrier, cancellationToken).ConfigureAwait(false);
        last = null;
        lowest = double.MaxValue;
        var from = Budget - s.TicksLeft;
        _ = await s.ServeUntilAsync("meal", s.TicksLeft, async ct =>
        {
            var food = await FoodAsync(ct).ConfigureAwait(false);
            return (string.Create(CultureInfo.InvariantCulture, $"food={food:0.000} ate={ateFrom is not null}"), ateFrom is not null);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("ateAfter", Budget - s.TicksLeft - from);
        s.Record("ateFrom", ateFrom?.ToString("0.000", CultureInfo.InvariantCulture) ?? "never");
        if (ateFrom is not { } level)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the colonist did not eat once its meal was due (food fell to {lowest:0.000})"));
        }

        if (level < Late)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the colonist ate late: food fell to {level:0.000}, below {Late}"));
        }
    }

    // Whether the colonist started a vanilla job (no Hands assignment, not the governor's hold) after the brain's first
    // review: the review left it unheld (#3669).
    private static bool Released(TypedSession s, string pawn)
    {
        var reviews = s.Telemetry(TelemetryKinds.RoundsReview).Where(r => r.HasTick).Select(r => r.Tick).ToList();
        if (reviews.Count == 0) return false;
        var first = reviews.Min();
        return s.Telemetry(TelemetryKinds.PawnActivityInterval).Any(r =>
            Text(r, PawnActivityTelemetry.PawnKey) == pawn && Text(r, PawnActivityTelemetry.AssignmentKey).Length == 0
            && Text(r, "job") is { Length: > 0 } job && job != "RimGovernorWait"
            && r.Payload.GetValueOrDefault(PawnActivityTelemetry.StartTickKey) is { } start
            && Convert.ToInt64(start, CultureInfo.InvariantCulture) > first);
    }

    private static string Text(LogRow row, string key) => row.Payload.GetValueOrDefault(key) as string ?? "";
}
