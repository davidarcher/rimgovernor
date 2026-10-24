using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastPortfolioChoiceTests
{
    private static readonly ObservationContext Context = new()
        { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };
    private static readonly Cell Source = new(1, 1), Bench = new(2, 1), Destination = new(3, 1);
    private static readonly Cell[] Cells = [Source, Bench, Destination];
    private static (ForecastCoupledSelection Selection, PawnActivities Batch) Select(RoundsFacts facts)
    {
        facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 150)?.Priced() };
        var generated = Assert.IsType<ForecastCoupledCandidates>(facts.CoupledAlternatives);
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 150d / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        Assert.Equal(Known(true), selected.Selected.Feasible);
        Assert.NotEmpty(selected.Selection.Selected);
        // The hungry pawn's food reaches zero without the portfolio: it loses fewer capability-days with it.
        Assert.True(selected.SelectedVector.Value.Ledger.LossAgainst(selected.BaselineVector.Value.Ledger).Value < 0);
        return (selected, Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "portfolio-choice", 100)));
    }
    internal static RoundsFacts Observed(CoupledFoodConversion[] conversions, CoupledFoodSource[] sources, bool helper = false)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .1, fall: .001);
        var roster = helper ? WorkResourceTestFacts.Create("pawn", "helper") : WorkResourceTestFacts.Create("pawn");
        var needs = facts.ForecastObserved.Needs!;
        facts = facts with
        {
            Resources = Known(EquatableArray.From(sources.GroupBy(s => s.Resource).Select(g => new RimGovernor.Brain.Policy.Amount(new(g.Key), g.Sum(s => s.Count))))),
            WorkResources = roster.WorkResources,
            ForecastObserved = facts.ForecastObserved with
            {
                WorkPawns = roster.ForecastObserved.WorkPawns,
                Needs = needs with { Pawns = helper ? [needs.Pawns[0], needs.Pawns[0] with { Pawn = "helper",
                    Needs = EquatableArray.From(needs.Pawns[0].Needs.Select(n => n with { Level = Known(1d), FallPerTick = Known(0d) })) }] : needs.Pawns },
                Coupled = new()
                {
                    Research = ActivityContinuationFixtures.NoResearch,
                    Tick = 100, Food = new(), FoodSources = EquatableArray.From(sources),
                    FoodConversions = EquatableArray.From(conversions),
                    FoodUses = [new("pawn", "Meal", Destination, Known(true), Known(.8), Known(10d), 1, Known(1000d))],
                    Routes = EquatableArray.From(roster.ForecastObserved.WorkPawns.Value.SelectMany(p => Cells.SelectMany(a => Cells.Where(b => b != a)
                        .Select(b => new CoupledRoute(p.Id.Value, a, b, Known(1d))))
                        .Concat(Cells.Select(c => new CoupledRoute(p.Id.Value, new(0, 0), c, Known(0d)) { FromStart = true })))),
                },
            },
        };
        return WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
    }
    internal static CoupledFoodConversion Conversion(string source, string resource, string recipe, double work) =>
        new(source, "pawn", "bench", recipe, resource, 4, "Meal", 1, Source, Bench, Destination,
            Known(0d), Known(1d), Known(work), Known(1d), 4, 4)
        { RequiresHarvest = false, Bill = "bill/" + recipe, WorkGiver = "Cook" };

    [Theory]
    [InlineData(false, "Rice", "CookRice", "rice")]
    [InlineData(true, "Corn", "CookCorn", "corn")]
    public void WholePortfolioChangesRecipeAndIngredientWhenObservedWorkReverses(bool reverse, string ingredient, string recipe, string source)
    {
        var facts = Observed([Conversion("rice", "Rice", "CookRice", reverse ? 80 : 10),
            Conversion("corn", "Corn", "CookCorn", reverse ? 10 : 80)],
            [new("rice", "Rice", Source, 4, Known(1000d)), new("corn", "Corn", Source, 4, Known(1000d))]);
        var (_, batch) = Select(facts);
        var cooking = Assert.Single(batch.Tasks, t => t.Step.Kind == PawnActivityKind.Recipe);
        Assert.Equal(recipe, cooking.Step.Recipe!.Recipe);
        Assert.Equal(ingredient, cooking.Resource);
        var supply = Assert.Single(batch.Tasks, t => t.Step.Kind == PawnActivityKind.Deliver && t.Resource == ingredient);
        Assert.Equal(source, supply.Step.Target); Assert.Equal(4, supply.Step.Count);
        Assert.Single(batch.Tasks, t => t.Step.Kind == PawnActivityKind.Eat);
    }

    [Theory]
    [InlineData(5, false, "helper")]
    [InlineData(80, false, "pawn")]
    [InlineData(5, true, "pawn")]
    public void WholePortfolioChangesDelegationWithTravelAndExistingObligation(double helperTravel, bool obligated, string supplier)
    {
        var cook = Conversion("rice", "Rice", "CookMeal", 10) with { SupplyTicks = Known(40d), DeliveryTicks = Known(10d) };
        var facts = Observed([cook, cook with { Pawn = "helper", CanCook = false, CanDeliver = false, SupplyTicks = Known(helperTravel) }],
            [new("rice", "Rice", Source, 4, Known(1000d))], helper: true);
        if (obligated) facts = facts with { WorkView = facts.WorkView! with { Claims = facts.WorkView!.Claims with { Pawns = ["helper"] } } };
        var (_, batch) = Select(facts);
        Assert.Equal(supplier, Assert.Single(batch.Tasks, t => t.Step.Kind == PawnActivityKind.Deliver && t.Resource == "Rice").Pawn);
        Assert.Equal("pawn", Assert.Single(batch.Tasks, t => t.Step.Kind == PawnActivityKind.Recipe).Pawn);
        if (obligated) Assert.DoesNotContain(batch.Tasks, t => t.Pawn == "helper");
    }
    [Fact]
    public void GeneratedTwoSourcePortfolioMatchesExhaustiveSubsetAndScheduleOracle()
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .1, fall: .003);
        var generated = Assert.IsType<ForecastCoupledCandidates>(facts.CoupledAlternatives);
        var roots = generated.Alternatives.Where(c => c.ActivityCompletions.Count == 1
                && generated.Context.NeedTasks.Any(n => n.Task == c.ActivityCompletions[0] && n.Kind == "food"))
            .GroupBy(c => generated.Context.NeedTasks.Single(n => n.Task == c.ActivityCompletions[0]).FreshFrom).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(c => c.Id, StringComparer.Ordinal).First()).Take(2).ToArray();
        Assert.Equal(2, roots.Length);
        generated = generated with { Alternatives = EquatableArray.From(roots) };
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 300d / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new()) with { Coupled = generated.Context };
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        // The oracle ranks every subset's schedules by the comparator's two leading levels: the zero-time floor, then capability-days.
        ForecastValueExplanation? best = null;
        var horizon = 300d;
        for (var mask = 0; mask < 1 << roots.Length; mask++)
        {
            var subset = roots.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            var result = ColonyForecastRules.ForecastColony(request with { Proposed = new() { Add = EquatableArray.From(subset) } });
            foreach (var outcome in result.Proposed.Coupled!.Alternatives.Where(p => p.Feasible == Known(true)))
            {
                var vector = ForecastCoupledSelection.CoupledConsequences(outcome, subset);
                Assert.All(vector.Urgent, value => Assert.True(value.IsKnown));
                var value = ForecastValue.Explain(outcome, vector.Urgent[0], generated.Context, horizon, selected.Baseline);
                if (best is null || value.UrgentZero.Excess < best.UrgentZero.Excess
                    || value.UrgentZero.Excess == best.UrgentZero.Excess && value.Ledger.LossAgainst(best.Ledger).Value < -value.Policy.TieResolution) best = value;
                var tasks = outcome.Plan.Allocations.Select(a => generated.Context.Activities.Activities.Single(t => t.Id == a.Activity)).ToArray();
                var produced = tasks.SelectMany(t => t.Outputs).Sum(o => o.Units.Value);
                var consumed = tasks.SelectMany(t => t.Inputs).Sum(i => i.Units.Value);
                Assert.True(consumed <= produced);
            }
        }
        Assert.NotNull(best);
        Assert.Equal(best.UrgentZero, selected.SelectedVector.Value.UrgentZero);
        Assert.InRange(selected.SelectedVector.Value.Ledger.LossAgainst(best.Ledger).Value, -best.Policy.TieResolution, best.Policy.TieResolution);
    }
}
