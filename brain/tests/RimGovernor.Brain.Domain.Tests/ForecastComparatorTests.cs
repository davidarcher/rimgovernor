using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The coupled comparator's order (#3522): floors, then capability-days, then the unpriced tiebreakers.</summary>
public sealed class ForecastComparatorTests
{
    private static readonly double Tie = new ForecastValuePolicy().TieResolution;

    private static ForecastConsequenceVector Vector(double? hunger = 0, double zeroExcess = 0, double breakExcess = 0, double? wellbeing = 0,
        double? breaks = 0, double development = 0, double labor = 0, bool breakAssessed = true, double deathExcess = 0)
    {
        static LedgerTerm Of(double? value) => value is { } v ? new LedgerTerm.Priced(v) : new LedgerTerm.Unassessed();
        var ledger = new ForecastValueResult(new LedgerTerm.Unassessed(), [])
        {
            Hunger = Of(hunger), Rest = Of(0), FoodPoisoning = Of(0), Thermal = Of(null), MoodBreaks = Of(breaks), Wellbeing = Of(wellbeing),
            Harm = Of(0), Investment = 0,
        };
        var value = new ForecastValueExplanation(ledger, new())
        {
            UrgentZero = new(true, zeroExcess), BreakRisk = new(breakAssessed, breakExcess), Death = new(deathExcess > 0, deathExcess),
        };
        return new(EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 5).ToArray()), EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 6).ToArray()),
            new ForecastTerm.Assessed(0d), new ForecastTerm.Assessed(0d), development, labor, []) { Value = value };
    }

    [Fact]
    public void AnUnassessedTermIsLeftOutOfBothSidesAndAPricedOneCounts()
    {
        var priced = Vector(hunger: 2, wellbeing: 1).Value.Ledger;
        var unassessed = Vector(hunger: null, wellbeing: 0.5).Value.Ledger;
        Assert.Equal(new LedgerTerm.Priced(2), priced.Hunger);
        Assert.IsType<LedgerTerm.Unassessed>(unassessed.Hunger);
        // Only wellbeing is priced on both sides, so hunger's loss rewards neither.
        Assert.Equal(0.5, priced.LossAgainst(unassessed), 9);
        Assert.Equal(-0.5, unassessed.LossAgainst(priced), 9);
    }

    [Fact]
    public void AFoodZeroFloorBeatsTheCurrencyEvenWhenTheOtherPortfolioHasLessLoss()
    {
        var keepsFood = Vector(wellbeing: 5);       // loses more wellbeing, never reaches zero
        var starves = Vector(wellbeing: -5, zeroExcess: 40);
        Assert.True(ForecastCoupledSelection.CompareConsequences(keepsFood, starves) < 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(starves, keepsFood) > 0);
    }

    [Fact]
    public void WhenBothPortfoliosReachZeroTheLeastZeroTimeWins()
    {
        var shorter = Vector(zeroExcess: 75, hunger: 3);
        var longer = Vector(zeroExcess: 325, hunger: 1);
        Assert.True(ForecastCoupledSelection.CompareConsequences(shorter, longer) < 0);
    }

    [Fact]
    public void AnOverTheCapBreakRiskLosesAndAnUnassessedOneIsNeitherPassedNorFailed()
    {
        var over = Vector(breakExcess: .2, breaks: 0);
        var under = Vector(breaks: .3);
        Assert.True(ForecastCoupledSelection.CompareConsequences(under, over) < 0);
        // No threshold supports the risk: the floor holds nothing against it, so the ledger and tiebreakers decide.
        var unassessed = Vector(breakAssessed: false, breaks: null);
        Assert.False(unassessed.Value.BreakRisk.Assessed);
        Assert.True(unassessed.Value.BreakRisk.Passed);
        Assert.True(ForecastCoupledSelection.CompareConsequences(unassessed, Vector(breaks: .3)) == 0);
    }

    [Fact]
    public void ABrokenDeathFloorRanksLastBelowTheCurrencyAndTheSmallerExcessWinsBetweenTwo()
    {
        var safe = Vector(hunger: 5);                      // loses more capability-days, holds the floor
        var deadly = Vector(hunger: 0, deathExcess: .02);  // loses none, adds deaths
        Assert.True(ForecastCoupledSelection.CompareConsequences(safe, deadly) < 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(deadly, safe) > 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(Vector(deathExcess: .02), Vector(deathExcess: .05)) < 0);
        // A food-zero floor still outranks it.
        Assert.True(ForecastCoupledSelection.CompareConsequences(deadly, Vector(zeroExcess: 40)) < 0);
    }

    [Fact]
    public void ALowerLossWinsBeyondTheTieAndTiesWithinIt()
    {
        var better = Vector(hunger: .5);
        Assert.True(ForecastCoupledSelection.CompareConsequences(better, Vector(hunger: .5 + 2 * Tie)) < 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(Vector(hunger: .5 + 2 * Tie), better) > 0);
        Assert.Equal(0, ForecastCoupledSelection.CompareConsequences(better, Vector(hunger: .5 + Tie / 2)));
        // Wellbeing is a loss too: more mood than the baseline is a negative term.
        Assert.True(ForecastCoupledSelection.CompareConsequences(Vector(wellbeing: -1), Vector(wellbeing: 0)) < 0);
    }

    [Fact]
    public void ATermOneSideCannotAssessIsLeftOutOfBothAndNeverRewardsIt()
    {
        // The mood term is unknown on the left: only the shared hunger term is compared, so it cannot win by hiding mood.
        var hidden = Vector(hunger: 1, breaks: null, wellbeing: null);
        var assessed = Vector(hunger: .5, breaks: 9, wellbeing: 9);
        Assert.True(ForecastCoupledSelection.CompareConsequences(assessed, hidden) < 0);
        Assert.Equal(.5, hidden.Value.Ledger.LossAgainst(assessed.Value.Ledger), 6);
        // Equal on what both assess: the unassessed side does not win or lose.
        Assert.Equal(0, ForecastCoupledSelection.CompareConsequences(Vector(hunger: 1, breaks: null), Vector(hunger: 1, breaks: 9)));
    }

    [Fact]
    public void WithinTheTieARequestedDevelopmentUnitDecidesAndLaborIsLeftToTheCaller()
    {
        Assert.True(ForecastCoupledSelection.CompareConsequences(Vector(development: 2, hunger: .5), Vector(development: 1, hunger: .5 + Tie / 2)) < 0);
        // Equal on every ranked term, including development: zero, so the caller keeps the incumbent and then prefers less labor.
        Assert.Equal(0, ForecastCoupledSelection.CompareConsequences(Vector(labor: 10), Vector(labor: 50)));
        // A development unit never outweighs capability-days beyond the tie.
        Assert.True(ForecastCoupledSelection.CompareConsequences(Vector(development: 0, hunger: 0), Vector(development: 9, hunger: 1)) < 0);
    }
}
