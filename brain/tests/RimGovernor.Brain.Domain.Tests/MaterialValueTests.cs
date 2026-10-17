using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class MaterialValueTests
{
    private static readonly Resource Metal = new("Metal");
    private static readonly Resource Wood = new("Wood");
    private static Fact<IReadOnlyDictionary<Resource, Fact<double>>> Stock => Known<IReadOnlyDictionary<Resource, Fact<double>>>(
        new Dictionary<Resource, Fact<double>> { [Metal] = Known(10.0), [Wood] = Known(10.0) });
    private static ForecastMaterialValuation Values(double urgent = 20, double workRate = 0.1) => new(
        new Dictionary<string, ForecastNeedValue> { ["urgent"] = new(Known(1.0), Known(urgent)), ["beauty"] = new(Known(10.0), Known(1.0)) },
        new Dictionary<Resource, Fact<double>> { [Metal] = Known(0.1), [Wood] = Known(0.1) }, Known(workRate));
    private static ForecastMaterialOption Option(string demand, string id, string need, double units, Resource material, long count, double work = 1) =>
        new(demand, id, default, [new(material, count)]) { Estimate = new([new(need, Known(units))], Known(work)) };

    [Fact]
    public void UrgentNeedBeatsTwoOptionalJobsAndChangesWhenTheNeedIsSatisfied()
    {
        ForecastMaterialOption[] options = [Option("a", "art-a", "beauty", 3, Metal, 5), Option("b", "art-b", "beauty", 3, Metal, 5),
            Option("c", "machine", "urgent", 1, Metal, 10)];
        var urgent = ForecastMaterialChoices.CompareMaterialPlans(options, Stock, valuation: Values());
        Assert.Equal("machine", Assert.Single(urgent.Selected).Id);
        Assert.Equal(new ForecastMaterialValue(20, 1, 0.1), urgent.Valuation);
        Assert.Equal(18.9, urgent.Value, 8);
        var satisfied = Values() with { Needs = new Dictionary<string, ForecastNeedValue> {
            ["urgent"] = new(Known(0.0), Known(20.0)), ["beauty"] = new(Known(10.0), Known(1.0)) } };
        Assert.Equal(2, ForecastMaterialChoices.CompareMaterialPlans(options, Stock, valuation: satisfied).Selected.Count);
        Assert.Equal(urgent.Selected, ForecastMaterialChoices.CompareMaterialPlans(options.Reverse(), Stock, valuation: Values()).Selected);
    }

    [Fact]
    public void NeedReliefIsCappedAcrossJobsAndUnusedMaterialsStayFree()
    {
        var options = new[] { Option("a", "a", "urgent", 1, Metal, 5), Option("b", "b", "urgent", 1, Metal, 5) };
        var result = ForecastMaterialChoices.CompareMaterialPlans(options, Stock, valuation: Values());
        Assert.Single(result.Selected);
        Assert.Equal(20, result.Valuation!.Benefit);
        Assert.Equal(0.5, result.Valuation.MaterialCost);
    }

    [Fact]
    public void BetterMaterialMustJustifyItsOpportunityCostAndExtraWork()
    {
        var cheap = Option("art", "wood", "beauty", 4, Wood, 5);
        var better = Option("art", "metal", "beauty", 8, Metal, 5);
        var expensive = Values() with { MaterialUtilityPerUnit = new Dictionary<Resource, Fact<double>> { [Metal] = Known(1.0), [Wood] = Known(0.1) } };
        Assert.Equal("wood", Assert.Single(ForecastMaterialChoices.CompareMaterialPlans([cheap, better], Stock, valuation: expensive).Selected).Id);
        Assert.Equal("metal", Assert.Single(ForecastMaterialChoices.CompareMaterialPlans([cheap, better], Stock, valuation: Values()).Selected).Id);
        var slow = better with { Estimate = better.Estimate! with { WorkTicks = Known(100.0) } };
        Assert.Equal("wood", Assert.Single(ForecastMaterialChoices.CompareMaterialPlans([cheap, slow], Stock, valuation: Values()).Selected).Id);
    }

    [Fact]
    public void PostponeWinsForZeroOrNegativeNetBenefitEvenWhenStockIsAvailable()
    {
        var option = Option("job", "costly", "beauty", 1, Wood, 5, work: 5);
        var zero = ForecastMaterialChoices.CompareMaterialPlans([option], Stock, valuation: Values());
        Assert.Empty(zero.Selected);
        Assert.Equal(0, zero.Value);
        Assert.Equal(new ForecastMaterialValue(0, 0, 0), zero.Valuation);
        Assert.Empty(ForecastMaterialChoices.CompareMaterialPlans([option], Stock, valuation: Values(workRate: 1)).Selected);
    }

    [Fact]
    public void UnknownCostsAndEffectsCannotFallBackToLegacyValue()
    {
        var option = Option("job", "job", "urgent", 1, Metal, 1) with { Value = Known(999.0) };
        var missing = new[] {
            (option with { Estimate = null }, Values(), "unknown_estimate:"),
            (option with { Estimate = option.Estimate! with { WorkTicks = default } }, Values(), "unknown_work:"),
            (option, Values() with { WorkUtilityPerTick = default }, "unknown_work_value"),
            (option, Values() with { MaterialUtilityPerUnit = new Dictionary<Resource, Fact<double>>() }, "unknown_material_value:"),
            (option, Values() with { Needs = new Dictionary<string, ForecastNeedValue>() }, "unknown_need:") };
        foreach (var (candidate, values, reason) in missing)
        {
            var result = ForecastMaterialChoices.CompareMaterialPlans([candidate], Stock, valuation: values);
            Assert.Empty(result.Selected);
            Assert.Contains(result.Findings, f => f.StartsWith(reason, StringComparison.Ordinal));
        }
        Assert.Throws<DomainException>(() => ForecastMaterialChoices.CompareMaterialPlans([option], Stock, valuation: Values(workRate: -1)));
        Assert.Throws<DomainException>(() => ForecastMaterialChoices.CompareMaterialPlans([option], Stock, valuation: Values(workRate: double.PositiveInfinity)));
    }

    [Fact]
    public void ValuationUsesTheSameBoundedSearchAndHardMaterialBudget()
    {
        var a = Option("a", "a", "beauty", 1, Wood, 1);
        var b = Option("b", "b", "urgent", 1, Metal, 11);
        var bounded = ForecastMaterialChoices.CompareMaterialPlans([a, b], Stock, expansionBudget: 1, valuation: Values());
        Assert.True(bounded.SearchTruncated);
        Assert.Equal(1, bounded.Expansions);
        Assert.Equal("a", Assert.Single(bounded.Selected).Id);
        Assert.Equal("a", Assert.Single(ForecastMaterialChoices.CompareMaterialPlans([a, b], Stock, valuation: Values()).Selected).Id);
    }

    [Fact]
    public void GeneratedBillVariantsReceiveExactBatchEstimatesBeforeJointSelection()
    {
        var art = new LedgerPlacement("a", new() { Recipe = "Art", Mode = BillMode.GearBatch, Target = 1 });
        var machine = new LedgerPlacement("b", new() { Recipe = "Machine", Mode = BillMode.GearBatch, Target = 1 });
        var view = new ProductionMaterialView(Known<EquatableArray<ProductionRecipeInput>>([
            new("Art", Known<EquatableArray<EquatableArray<Amount>>>([[new(Metal, 10), new(Wood, 10)]]), Known(false)),
            new("Machine", Known<EquatableArray<EquatableArray<Amount>>>([[new(Metal, 10)]]), Known(false))]), Stock);
        string Key(LedgerPlacement p, Resource r) => (p.Spec with { Ingredients = [r.Value] }).Key();
        var estimates = new Dictionary<string, ForecastProductionEstimate> {
            [Key(art, Metal)] = new([new("beauty", Known(8.0))], Known(1.0)),
            [Key(art, Wood)] = new([new("beauty", Known(4.0))], Known(1.0)),
            [Key(machine, Metal)] = new([new("urgent", Known(1.0))], Known(1.0)) };
        var valuation = new ForecastProductionValuation(Values(), estimates);
        Assert.Equal(Metal.Value, Assert.Single(LedgerMaterialChoicesRules.SelectLedgerMaterials([art], view, valuation: valuation).Selected).Spec.Ingredients[0]);
        var together = LedgerMaterialChoicesRules.SelectLedgerMaterials([art, machine], view, valuation: valuation);
        Assert.Equal(2, together.Selected.Count);
        Assert.Equal(Wood.Value, together.Selected.Single(p => p.Spec.Recipe == "Art").Spec.Ingredients[0]);
        Assert.Equal(24, together.Comparison.Valuation!.Benefit);
        estimates.Remove(Key(art, Wood));
        var unknown = LedgerMaterialChoicesRules.SelectLedgerMaterials([art, machine], view, valuation: valuation);
        Assert.Equal("Machine", Assert.Single(unknown.Selected).Spec.Recipe);
        Assert.Single(unknown.Postponed);
        Assert.Contains(unknown.Comparison.Findings, f => f.StartsWith("unknown_estimate:", StringComparison.Ordinal));
    }
}
