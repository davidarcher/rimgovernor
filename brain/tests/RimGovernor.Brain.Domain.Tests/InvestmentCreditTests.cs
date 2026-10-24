using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3636: tier 1 credits the skill XP an allocation's learning callback applies at tier 2's value per XP, and charges the medicine it
// consumes at tier 2's value per unit, in the Investment term.
public sealed class InvestmentCreditTests
{
    private const string Medicine = "MedicineIndustrial";
    private const double PerXp = 0.01;
    private const double PerMedicine = 0.3;
    private static readonly string SkillKey = ProjectGraph.SkillKey("doctor", "Medicine");

    private static readonly Dictionary<string, double> Marginals = new(StringComparer.Ordinal)
    {
        [SkillKey] = PerXp, [ProjectGraph.MedicineKey(new Resource(Medicine))] = PerMedicine,
    };

    private static ForecastCoupledContext Context(IReadOnlyDictionary<string, double> investment) => new()
    {
        Activities = new() { Activities = [new("tend/1", "bed", "bed", []) { Inputs = [new(new Resource(Medicine), Known(1d))] }] },
        Needs = NeedsFixtures.Empty, Resources = ForecastValueTests.Resources(), Investment = investment,
    };

    /// <summary>The doctor's tend, its learning projected by <see cref="ForecastProductiveWorkPolicy"/> from <paramref name="todayXp"/> already learned today.</summary>
    private static ForecastValueResult Tend(IReadOnlyDictionary<string, double> investment, double todayXp = 0)
    {
        var tend = ProductiveLearningTests.Tend() with { Learning = ProductiveLearningTests.Work().Learning with { XpSinceMidnight = todayXp } };
        var option = new ForecastActivityOption("tend/1", "doctor", Known(400d), Known(0d)) { TendLearning = tend };
        var learning = ForecastProductiveWorkPolicy.ForecastProductiveWork(option, [], 0).Evidence!;
        var plan = new ForecastActivityPlan([new("tend/1", "doctor", 0, 0, 0, 400, 0, 0, 400, 400, 0) { Learning = learning }], 400, 400);
        return ForecastValue.Of(new(plan, Known(true), new Dictionary<string, ForecastNeedsResult>(), []), Context(investment), GameTime.TicksPerDay,
            new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>(), []));
    }

    private static ForecastConsequenceVector Vector(ForecastValueResult ledger) => new(
        EquatableArray.From(Enumerable.Repeat(Known(0d), 5).ToArray()), EquatableArray.From(Enumerable.Repeat(Known(0d), 6).ToArray()),
        Known(0d), Known(0d), 0, 0, [])
    { Value = new(ledger, new()) { UrgentZero = new(true, 0) } };

    [Fact]
    public void AMedicineCallbackIsCreditedItsAppliedXpAtTheMarginalAndTheMedicineIsCharged()
    {
        var ledger = Tend(Marginals);

        // The native 250 raw XP of a completed tend, unsaturated, at a unit learning factor.
        var xp = Assert.Single(ledger.InvestmentContributions, c => c.Key == SkillKey);
        Assert.Equal(("tend/1", "doctor", 250d), (xp.Activity, xp.Pawn, xp.Units));
        Assert.Equal(-250 * PerXp, xp.Value, 12);
        var medicine = Assert.Single(ledger.InvestmentContributions, c => c.Key == ProjectGraph.MedicineKey(new Resource(Medicine)));
        Assert.Equal((1d, PerMedicine), (medicine.Units, medicine.Value));
        Assert.Equal((-250 * PerXp) + PerMedicine, ledger.Investment.Value, 12);
    }

    [Fact]
    public void SaturationReducesTheCredit()
    {
        // Past today's saturation threshold (1000) the tracker applies the saturated factor 0.2: 50 of the 250 raw XP.
        var saturated = Tend(Marginals, todayXp: 2000);

        Assert.Equal(50, saturated.InvestmentContributions.Single(c => c.Key == SkillKey).Units, 12);
        Assert.Equal((-50 * PerXp) + PerMedicine, saturated.Investment.Value, 12);
    }

    [Fact]
    public void WithoutMarginalsNeitherTermChangesTheRanking()
    {
        var fresh = Tend(new Dictionary<string, double>());
        var saturated = Tend(new Dictionary<string, double>(), todayXp: 2000);

        Assert.Equal(0, fresh.Investment.Value);
        Assert.Empty(fresh.InvestmentContributions);
        Assert.Equal(0, fresh.LossAgainst(saturated).Value);
        Assert.Equal(0, ForecastCoupledSelection.CompareConsequences(Vector(fresh), Vector(saturated)));
    }

    [Fact]
    public void OtherThingsEqualTheComparatorPrefersTheTendWhoseDoctorLearnsMorePerUnitOfMedicine()
    {
        var learns = Tend(Marginals);
        var saturated = Tend(Marginals, todayXp: 2000);

        Assert.True(ForecastCoupledSelection.CompareConsequences(Vector(learns), Vector(saturated)) < 0);
    }
}
