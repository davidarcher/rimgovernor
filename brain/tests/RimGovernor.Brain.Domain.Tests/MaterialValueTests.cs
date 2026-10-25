using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class MaterialValueTests
{
    private static readonly Resource Metal = new("Metal");
    private static readonly Resource Wood = new("Wood");
    private static MaterialAvailability Stock => new MaterialAvailability.Assessed(
        new Dictionary<Resource, MaterialUnits> { [Metal] = new MaterialUnits.Exact(10.0), [Wood] = new MaterialUnits.Exact(10.0) });
    private static ForecastMaterialValuation Values(double urgent = 20, double workRate = 0.1) => new(
        new Dictionary<string, ForecastNeedValue> { ["urgent"] = new(1, urgent), ["beauty"] = new(10, 1) },
        new Dictionary<Resource, double> { [Metal] = 0.1, [Wood] = 0.1 }, workRate);
    private static ForecastMaterialOption Option(string demand, string id, string need, double units, Resource material, long count, double work = 1) =>
        new(demand, id, 0, [new(material, count)]) { Estimate = new([new(need, units)], work) };

    [Fact]
    public void UrgentNeedBeatsTwoOptionalJobsAndChangesWhenTheNeedIsSatisfied()
    {
        ForecastMaterialOption[] options = [Option("a", "art-a", "beauty", 3, Metal, 5), Option("b", "art-b", "beauty", 3, Metal, 5),
            Option("c", "machine", "urgent", 1, Metal, 10)];
        var urgent = ForecastMaterialChoices.CompareMaterialPlans(options, Stock, valuation: Values());
        Assert.Equal("machine", Assert.Single(urgent.Selected).Id);
        var urgentValue = ForecastMaterialValue.ValueMaterialPlan(urgent.Selected, Values(), new SortedSet<string>(StringComparer.Ordinal));
        Assert.Equal(new ForecastMaterialValue(20, 1, 0.1), urgentValue);
        Assert.Equal(18.9, urgentValue!.Net, 8);
        var satisfied = Values() with { Needs = new Dictionary<string, ForecastNeedValue> {
            ["urgent"] = new(0, 20), ["beauty"] = new(10, 1) } };
        Assert.Equal(2, ForecastMaterialChoices.CompareMaterialPlans(options, Stock, valuation: satisfied).Selected.Count);
        Assert.Equal(urgent.Selected, ForecastMaterialChoices.CompareMaterialPlans(options.Reverse(), Stock, valuation: Values()).Selected);
    }

    [Fact]
    public void NeedReliefIsCappedAcrossJobsAndUnusedMaterialsStayFree()
    {
        var options = new[] { Option("a", "a", "urgent", 1, Metal, 5), Option("b", "b", "urgent", 1, Metal, 5) };
        var result = ForecastMaterialChoices.CompareMaterialPlans(options, Stock, valuation: Values());
        Assert.Single(result.Selected);
        var value = ForecastMaterialValue.ValueMaterialPlan(result.Selected, Values(), new SortedSet<string>(StringComparer.Ordinal));
        Assert.Equal(20, value!.Benefit);
        Assert.Equal(0.5, value.MaterialCost);
    }

    [Fact]
    public void BetterMaterialMustJustifyItsOpportunityCostAndExtraWork()
    {
        var cheap = Option("art", "wood", "beauty", 4, Wood, 5);
        var better = Option("art", "metal", "beauty", 8, Metal, 5);
        var expensive = Values() with { MaterialUtilityPerUnit = new Dictionary<Resource, double> { [Metal] = 1, [Wood] = 0.1 } };
        Assert.Equal("wood", Assert.Single(ForecastMaterialChoices.CompareMaterialPlans([cheap, better], Stock, valuation: expensive).Selected).Id);
        Assert.Equal("metal", Assert.Single(ForecastMaterialChoices.CompareMaterialPlans([cheap, better], Stock, valuation: Values()).Selected).Id);
        var slow = better with { Estimate = better.Estimate! with { WorkTicks = 100 } };
        Assert.Equal("wood", Assert.Single(ForecastMaterialChoices.CompareMaterialPlans([cheap, slow], Stock, valuation: Values()).Selected).Id);
    }

    [Fact]
    public void PostponeWinsForZeroOrNegativeNetBenefitEvenWhenStockIsAvailable()
    {
        var option = Option("job", "costly", "beauty", 1, Wood, 5, work: 5);
        var zero = ForecastMaterialChoices.CompareMaterialPlans([option], Stock, valuation: Values());
        Assert.Empty(zero.Selected);
        Assert.Empty(ForecastMaterialChoices.CompareMaterialPlans([option], Stock, valuation: Values(workRate: 1)).Selected);
    }

    [Fact]
    public void UnestimatedBatchesUnpricedMaterialsAndUnvaluedNeedsCannotFallBackToLegacyValue()
    {
        var option = Option("job", "job", "urgent", 1, Metal, 1) with { Value = 999 };
        var missing = new[] {
            (option with { Estimate = null }, Values(), "unestimated_batch:"),
            (option, Values() with { MaterialUtilityPerUnit = new Dictionary<Resource, double>() }, "material_unpriced:"),
            (option, Values() with { Needs = new Dictionary<string, ForecastNeedValue>() }, "need_unvalued:") };
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
        var view = new ProductionMaterialView([
            new("Art", [[new(Metal, 10), new(Wood, 10)]], false) { Products = [], WorkAmount = 0 },
            new("Machine", [[new(Metal, 10)]], false) { Products = [], WorkAmount = 0 }], Stock);
        string Key(LedgerPlacement p, Resource r) => (p.Spec with { Ingredients = [r.Value] }).Key();
        var estimates = new Dictionary<string, ForecastProductionEstimate> {
            [Key(art, Metal)] = new([new("beauty", 8)], 1),
            [Key(art, Wood)] = new([new("beauty", 4)], 1),
            [Key(machine, Metal)] = new([new("urgent", 1)], 1) };
        var valuation = new ForecastProductionValuation(Values(), estimates);
        Assert.Equal(Metal.Value, Assert.Single(LedgerMaterialChoicesRules.SelectLedgerMaterials([art], view, valuation: valuation).Selected).Spec.Ingredients[0]);
        var together = LedgerMaterialChoicesRules.SelectLedgerMaterials([art, machine], view, valuation: valuation);
        Assert.Equal(2, together.Selected.Count);
        Assert.Equal(Wood.Value, together.Selected.Single(p => p.Spec.Recipe == "Art").Spec.Ingredients[0]);
        Assert.Equal(24, ForecastMaterialValue.ValueMaterialPlan(together.Comparison.Selected, Values(), new SortedSet<string>(StringComparer.Ordinal))!.Benefit);
        estimates.Remove(Key(art, Wood));
        var unknown = LedgerMaterialChoicesRules.SelectLedgerMaterials([art, machine], view, valuation: valuation);
        Assert.Equal("Machine", Assert.Single(unknown.Selected).Spec.Recipe);
        Assert.Contains(unknown.Comparison.Findings, f => f.StartsWith("unestimated_batch:", StringComparison.Ordinal));
    }
}
