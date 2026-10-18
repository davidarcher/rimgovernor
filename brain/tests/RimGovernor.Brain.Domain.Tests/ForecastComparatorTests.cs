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
        double? breaks = 0, double development = 0, double labor = 0, bool breakAssessed = true)
    {
        static Fact<double> Of(double? value) => value is { } v ? Known(v) : default;
        var ledger = new ForecastValueResult(default, [])
        {
            Hunger = Of(hunger), Rest = Known(0d), FoodPoisoning = Known(0d), Thermal = default, MoodBreaks = Of(breaks), Wellbeing = Of(wellbeing),
            Harm = Known(0d), Investment = Known(0d),
        };
        var value = new ForecastValueExplanation(ledger, new())
        {
            UrgentZero = new(true, zeroExcess), BreakRisk = new(breakAssessed, breakExcess), Death = new(false, 0),
        };
        return new(EquatableArray.From(Enumerable.Repeat(Known(0d), 5).ToArray()), EquatableArray.From(Enumerable.Repeat(Known(0d), 6).ToArray()),
            Known(0d), Known(0d), development, labor, []) { Value = value };
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
        Assert.Equal(.5, hidden.Value.Ledger.LossAgainst(assessed.Value.Ledger).Value, 6);
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
