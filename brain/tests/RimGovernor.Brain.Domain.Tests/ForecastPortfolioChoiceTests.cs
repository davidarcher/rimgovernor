using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastPortfolioChoiceTests
{
    private static readonly ObservationContext Context = new()
        { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };
    private static readonly Cell Source = new(1, 1), Bench = new(2, 1), Destination = new(3, 1);
    private static readonly Cell[] Cells = [Source, Bench, Destination];
    private static (ForecastCoupledSelection Selection, PawnActivities Batch) Select(RoundsFacts facts)
    {
        facts = facts with { CoupledAlternatives = GenerateCoupledActivities(facts, 150) };
        var generated = Assert.IsType<ForecastCoupledCandidates>(facts.CoupledAlternatives);
        var request = CoupledForecastRequestOf(new("world/map/load", 100), 150d / GameTime.TicksPerDay,
            facts, new(), new() { Work = ForecastWorkInputsOf(facts) }, [], new());
        var selected = CompareActivityOutcomes(request, generated);
        Assert.Equal(Known(true), selected.Selected.Feasible);
        Assert.NotEmpty(selected.Selection.Selected);
        Assert.True(selected.SelectedVector.Urgent[1].Value < selected.BaselineVector.Urgent[1].Value);
        return (selected, Assert.IsType<PawnActivities>(StartCoupledActivity(selected, generated, 1, "portfolio-choice", 100)));
    }
    private static RoundsFacts Observed(CoupledFoodConversion[] conversions, CoupledFoodSource[] sources, bool helper = false)
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
                    Tick = 100, Food = new() { Context = Context, Known = true }, FoodSources = EquatableArray.From(sources),
                    FoodConversions = EquatableArray.From(conversions),
                    FoodUses = [new("pawn", "Meal", Destination, Known(true), Known(.8), Known(10d), 1, Known(1000d))],
                    Routes = EquatableArray.From(roster.ForecastObserved.WorkPawns.Value.SelectMany(p => Cells.SelectMany(a => Cells.Where(b => b != a)
                        .Select(b => new CoupledRoute(p.Id.Value, a, b, Known(1d))))
                        .Concat(Cells.Select(c => new CoupledRoute(p.Id.Value, new(0, 0), c, Known(0d)) { FromStart = true })))),
                },
            },
        };
        return ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
    }
    private static CoupledFoodConversion Conversion(string source, string resource, string recipe, double work) =>
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
            .GroupBy(c => c.ExclusiveOutcome).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(c => c.Id, StringComparer.Ordinal).First()).Take(2).ToArray();
        Assert.Equal(2, roots.Length);
        generated = generated with { Alternatives = EquatableArray.From(roots) };
        var request = CoupledForecastRequestOf(new("world/map/load", 100), 300d / GameTime.TicksPerDay,
            facts, new(), new() { Work = ForecastWorkInputsOf(facts) }, [], new()) with { Coupled = generated.Context };
        var selected = CompareActivityOutcomes(request, generated);
        ForecastConsequenceVector? best = null;
        for (var mask = 0; mask < 1 << roots.Length; mask++)
        {
            var subset = roots.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            var result = ForecastColony(request with { Proposed = new() { Add = EquatableArray.From(subset) } });
            foreach (var outcome in result.Proposed.Coupled!.Alternatives.Where(p => p.Feasible == Known(true)))
            {
                var vector = CoupledConsequences(outcome, subset);
                Assert.All(vector.Urgent, value => Assert.True(value.IsKnown));
                if (best is null || Lexicographic(vector.Urgent, best.Urgent) < 0) best = vector;
                var tasks = outcome.Plan.Allocations.Select(a => generated.Context.Activities.Activities.Single(t => t.Id == a.Activity)).ToArray();
                var produced = tasks.SelectMany(t => t.Outputs).Sum(o => o.Units.Value);
                var consumed = tasks.SelectMany(t => t.Inputs).Sum(i => i.Units.Value);
                Assert.True(consumed <= produced);
                Assert.All(outcome.Stocks.SelectMany(stock => stock.Points), point => Assert.True(point.BalanceUnits.Value >= 0));
            }
        }
        Assert.NotNull(best);
        Assert.Equal(best.Urgent, selected.SelectedVector.Urgent);
        Assert.Equal(2, selected.Selection.Selected.Length);
    }

    private static int Lexicographic(EquatableArray<Fact<double>> left, EquatableArray<Fact<double>> right)
    {
        for (var i = 0; i < left.Count; i++)
        {
            var order = left[i].Value.CompareTo(right[i].Value);
            if (order != 0) return order;
        }
        return 0;
    }
}