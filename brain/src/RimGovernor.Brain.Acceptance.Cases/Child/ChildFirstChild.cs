using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>child/first-child</c> (#1691) proves a Biotech colony feeds its first baby with no breastfeeder: the served brain's
/// food/baby-feeding Need bills the stove for baby-edible food, the colony cooks it and bottle-feeds the baby, which reaches
/// the Child stage fed and not malnourished. The fixture stages the baby half a game day short of the Child stage, so the
/// old case's two game days of native ageing are not run.
/// </summary>
internal static class ChildFirstChild
{
    // Half a game day (12000 ticks) to the Child stage: inside the cap with room for the bill, the cooking and a feed.
    private const double DaysToChild = 0.5;

    private static readonly FixtureOp<NoArgs, Fx.BabyCarePrepared> Prepare = new(
        "test/baby_care_prepare",
        _ => ServedOps.Args(("daysToChild", DaysToChild)),
        ServedOps.Reply<Fx.BabyCarePrepared>("test/baby_care_prepare"));

    private sealed record Baby(string BabyId, string BenchId);

    private static readonly FixtureOp<Baby, Fx.BabyCareRead> Inspect = new(
        "test/baby_care_inspect",
        b => ServedOps.Args(("babyId", b.BabyId), ("benchId", b.BenchId)),
        ServedOps.Reply<Fx.BabyCareRead>("test/baby_care_inspect"));

    internal static TypedCase Case() => new()
    {
        Name = "child/first-child",
        Scope = "Issue #1691: with no breastfeeder in the colony, the served brain bills the staged stove for a baby-edible recipe, "
            + "and the baby, staged half a day short of the Child stage at a food level only ingestion raises, is fed above that "
            + "level and reaches the Child stage alive with no malnutrition. The chain is vanilla's own (bottle-feeding on "
            + "Childcare work, the baby's food need, the stage change) behind the brain's bill, asserted on the live pawn.",
        Ticks = TypedCase.StagedTickCap,
        Tier = CaseTier.Nightly,
        Start = new LabStart(),
        Expansions = ["ludeon.rimworld.biotech"],
        NoKeep = true,
        Ops = [Prepare.Tool, Inspect.Tool],

        // The shelter family keeps supervised windows running (the bill planner alone never advances the clock); dialog
        // answers the letters.
        Serve = new ServeSpec { Families = ["bill", "shelter", "dialog"], Prefix = "child-first-child" },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("fixture", ServedOps.Json(prepared));
        if (!prepared.Success || prepared.BabyId.Length == 0 || prepared.BenchId.Length == 0 || prepared.Recipes.Count == 0 || prepared.FoodLevel <= 0)
        {
            throw new AcceptanceException($"fixture: unexpected first-child staging ({prepared.Reason}): {ServedOps.Json(prepared)?.ToJsonString()}");
        }

        var baby = new Baby(prepared.BabyId, prepared.BenchId);
        var last = new Fx.BabyCareRead();
        bool fed = false, billed = false;
        var ticks = await s.ServeUntilAsync("raise", s.TicksLeft, async ct =>
        {
            last = await s.FixtureAsync("inspect", Inspect, baby, ct).ConfigureAwait(false);
            if (!last.Success)
            {
                throw new AcceptanceException($"baby inspect refused: {last.Reason}");
            }

            if (last.Dead)
            {
                throw new AcceptanceException($"the baby died: {ServedOps.Json(last)?.ToJsonString()}");
            }

            fed |= last.FoodLevel > prepared.FoodLevel;
            billed |= last.Bills.Any(b => b.BabyEdible && prepared.Recipes.Contains(b.Recipe, StringComparer.Ordinal));
            return (Wait.Signature(last.Stage, last.FoodLevel, fed, billed), fed && billed && last.Stage == "Child");
        }, cancellationToken).ConfigureAwait(false);
        s.Record("ticks", ticks);
        s.Record("inspect", ServedOps.Json(last));
        s.Record("fed", fed);
        if (!billed)
        {
            throw new AcceptanceException($"no baby food bill ({string.Join(", ", prepared.Recipes)}) stood on bench {prepared.BenchId}");
        }

        if (!fed)
        {
            throw new AcceptanceException($"the baby's food level never rose above its staged {prepared.FoodLevel}, so it was never fed");
        }

        if (last.Stage != "Child")
        {
            throw new AcceptanceException($"the baby did not reach the Child stage: {last.Stage}");
        }

        if (last.Malnutrition > 0)
        {
            throw new AcceptanceException($"the baby reached the Child stage malnourished (severity {last.Malnutrition})");
        }
    }
}
