using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ColonyForecastTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static readonly Fact<EquatableArray<Amount>> Stock = Known(EquatableArray.From(new[] { new Amount(Material, 10) }));
    private static ForecastCommitment Change(string id, double day, double units) =>
        new(id, EquatableArray.From(new[] { new ForecastStockChange(Material, day, units) }));

    /// <summary>The stock findings of a three-day projection: what a coupled outcome carries of the inventory walk.</summary>
    private static List<ForecastFinding> Findings(Fact<EquatableArray<Amount>> stock, params ForecastCommitment[] commitments)
    {
        var findings = new List<ForecastFinding>();
        new ColonyForecastRules.InventoryWalk().Run(new(stock, ColonyForecastRules.ForecastCommitmentsById(commitments), 3), default, findings);
        return findings;
    }

    [Fact]
    public void EarlierDeliveryChangesShortfallWithoutChangingObservedStock()
    {
        Assert.Equal("build", Assert.Single(Findings(Stock, Change("delivery", 2, 10), Change("build", 1, -15)), f => f.Reason == "stock_shortfall").Commitment);
        Assert.Empty(Findings(Stock, Change("delivery", 0.5, 10), Change("build", 1, -15)));
    }

    [Fact]
    public void InputOrderingDoesNotChangeTheFindings()
    {
        var a = Change("use", 1, -4);
        var b = Change("delivery", 2, 2);
        Assert.Empty(Findings(Stock, a, b));
        Assert.Empty(Findings(Stock, b, a));
    }

    [Fact]
    public void UnknownStockIsNotKnownEmpty()
    {
        var use = Change("use", 1, -4);
        Assert.Contains(Findings(default, use), f => f.Reason == "unknown_initial_stock");
        Assert.DoesNotContain(Findings(default, use), f => f.Reason == "stock_shortfall");
        Assert.Equal("stock_shortfall", Assert.Single(Findings(Known(default(EquatableArray<Amount>)), use)).Reason);
    }

    [Fact]
    public void HorizonDoesNotTurnLaterBenefitsIntoZeroOrCurrentStock()
    {
        var late = Change("late", 4, 20);
        Assert.Equal("beyond_horizon", Assert.Single(Findings(Stock, late)).Reason);
        Assert.Equal(["beyond_horizon", "stock_shortfall"], Findings(Stock, late, Change("use", 3, -15)).Select(f => f.Reason));    }

    [Fact]
    public void SameTimeOutputDoesNotFinanceItsOwnInput()
    {
        var finding = Assert.Single(Findings(Stock, Change("output", 1, 20), Change("input", 1, -15)));
        Assert.Equal("stock_shortfall", finding.Reason);
        Assert.Equal("input", finding.Commitment);
    }

    [Fact]
    public void InvalidChangesAndDuplicateCommitmentsAreRejected()
    {
        Assert.Throws<DomainException>(() => ColonyForecastRules.ForecastCommitmentsById([Change("a", 0, 1), Change("a", 1, 1)]));
        Assert.Throws<DomainException>(() => ColonyForecastRules.ForecastCommitmentsById([Change("bad", -1, 1)]));
    }

    [Fact]
    public void ScheduleOwnOutputLandsAtItsEventAndUnsupportedWorkStaysExplicit()
    {
        var commitment = new ForecastCommitment("work", [])
        {
            UnsupportedEffects = EquatableArray.From(new List<string> { "work_completion" }),
        };
        // A schedule's own output of 4 on day 2: a use of 12 before it is short; one after it is covered.
        List<ForecastFinding> Walk(params ForecastCommitment[] commitments)
        {
            var findings = new List<ForecastFinding>();
            new ColonyForecastRules.InventoryWalk().Run(new(Stock, ColonyForecastRules.ForecastCommitmentsById(commitments), 3),
                new[] { (Material, 2.0, 4.0) }, findings);
            return findings;
        }
        var before = Walk(Change("use", 1, -12), commitment);
        Assert.Equal("use", Assert.Single(before, f => f.Reason == "stock_shortfall").Commitment);
        Assert.Contains(before, f => f.Reason == "unsupported:work_completion");
        Assert.DoesNotContain(Walk(Change("use", 3, -12), commitment), f => f.Reason == "stock_shortfall");
    }
}
