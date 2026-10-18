using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3520: the capability-days ledger over injected outcomes (no search). Each claim is one term.
public sealed class ForecastValueTests
{
    private const double Day = GameTime.TicksPerDay;

    private static NeedsForecastPawn Pawn(double foodLevel = 1, double fall = 0, double consciousness = 1) => new()
    {
        Pawn = "pawn", State = "eligible", Awake = Known(true), SleepThreshold = Known(.75), WakeThreshold = Known(1d), SleepBlockedUntilTicks = Known(0d),
        Mood = Known(.5), MoodTarget = Known(.5), BreakThreshold = Known(.3),
        Needs = [new("rest", Known(1d), Known(0d), Known(.28)), new("food", Known(foodLevel), Known(fall), Known(.3))],
        Memories = Known<EquatableArray<NeedMemory>>([]),
        Consciousness = Known(consciousness), Moving = Known(1d), Manipulation = Known(1d), Sight = Known(1d), Talking = Known(1d),
        MalnutritionSeverity = Known(0d), FoodPoisoningSeverity = Known(0d),
    };

    private static ForecastCapacityInput Input() => new()
    {
        Pawn = new() { Id = new("pawn"), Available = Known(true), Applies = Known(true), Job = Known(new PawnJob()),
            Schedule = Known(EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleSleep, 24))) },
        Location = "start", HorizonTicks = Day, LocalHour = Known(12.0), OpportunityCostPerTick = Known(1.0),
        CurrentJobRemainingTicks = Known(0.0), Occupancy = Known(default(EquatableArray<ForecastOccupancy>)),
        LocationAfterProtectedTime = Known("bed"), ProtectTimetable = false,
    };

    private static ForecastNeedsResult Timeline(NeedsForecastPawn pawn) => ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [], mood: false);

    private static ForecastMoodResult Mood(double pressure, double[]? tiers) => new([], default, default, default, default, default, [])
    {
        PressureTicks = Known(pressure), BreakRisk = tiers is null ? default : Known(0.0), BreakTierTicks = tiers is null ? default : EquatableArray.From(tiers),
    };

    private static ForecastCoupledOutcome Outcome(ForecastNeedsResult needs, double poisonCases = 0) =>
        new(new([], 0, 0), Known(true), [], new Dictionary<string, ForecastNeedsResult> { ["pawn"] = needs }, [])
        { Support = new() { FoodPoisonExposure = Known(poisonCases) } };

    private static HediffPenaltyTable Malnutrition(params PenaltyStage[] stages) => new()
    {
        Known = true, Hediff = "Malnutrition", LethalSeverity = 1, InitialSeverity = 0.5, Stages = stages,
    };

    private static PenaltyStage Stage(double min, params CapacityModifier[] mods) => new(min, 0, mods);

    private static CapacityModifier Half(string capacity) => new(capacity, 0, null, 0.5);

    private static CapacityPenaltyTables Tables(HediffPenaltyTable? malnutrition = null, HediffPenaltyTable? poisoning = null) => new()
    {
        Malnutrition = malnutrition ?? Malnutrition(Stage(0, Half("Consciousness"))),
        FoodPoisoning = poisoning ?? new() { Known = true, Hediff = "FoodPoisoning", InitialSeverity = 1, SeverityPerDay = -1, Stages = [Stage(0, Half("Consciousness"))] },
        MovingMinForCapable = 0.15,
    };

    private static MoodBreakModel Breaks() => new()
    {
        Threshold = 0.3, Tiers = [new(1, 2), new(1, 3), new(1, 5)],
    };

    private static ForecastCoupledContext Context(NeedsForecastPawn pawn, CapacityPenaltyTables? tables = null) => new()
    {
        Activities = new(), Needs = new() { Pawns = [pawn] }, Resources = Resources(),
        CapacityPenalties = tables ?? Tables(), MoodBreaks = Breaks(),
    };

    private static WorkResourceView Resources()
    {
        var facts = WorkResourceTestFacts.Create("pawn") with
        { Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([]) };
        return WorkRules.WorkResourcesOf(ProductionRules.WorkAdmissionEvidenceOf(facts, Known(default(EquatableArray<ProductionBatch>))),
            Known(default(EquatableArray<ProductionBatch>)), []);
    }

    // A pawn with a mood, a known break tier mix and no food or poisoning trouble: every term but the one under test is 0.
    private static ForecastNeedsResult Calm(double pressure = 0, double[]? tiers = null) =>
        Timeline(Pawn()) with { Mood = Mood(pressure, tiers ?? [0, 0, 0]) };

    [Fact]
    public void AStarvingPawnLosesTheMalnutritionStagesCapabilityForThatSpanAndNothingElse()
    {
        // Food reaches zero at half the day, so the pawn starves for the second half at the stage's half consciousness.
        var pawn = Pawn(foodLevel: 0.5, fall: 0.5 / (Day / 2));
        var starving = Timeline(pawn) with { Mood = Mood(0, [0, 0, 0]) };
        var span = Assert.Single(Assert.Single(starving.Needs, n => n.Kind == "food").ZeroSpans);
        Assert.Equal(Day / 2, span.Start, 3);
        Assert.Equal(Day, span.End, 3);

        var value = ForecastValue.Of(Outcome(starving), Context(pawn), Day, baseline: Outcome(starving));

        Assert.Equal(0.25, value.Hunger.Value, 9);
        Assert.Equal(0, value.Rest.Value);
        Assert.Equal(0, value.FoodPoisoning.Value);
        Assert.Equal(0, value.MoodBreaks.Value);
        Assert.Equal(0, value.Wellbeing.Value);
        Assert.Equal(0.25, value.Total.Value, 9);
    }

    [Fact]
    public void AStarvingPawnTheStageDownsLosesItsWholeSpan()
    {
        var table = Malnutrition(Stage(0, new CapacityModifier("Consciousness", 0, 0.1, 1)));
        var pawn = Pawn(foodLevel: 0.5, fall: 0.5 / (Day / 2));
        var starving = Timeline(pawn) with { Mood = Mood(0, [0, 0, 0]) };

        var value = ForecastValue.Of(Outcome(starving), Context(pawn, Tables(table)), Day, Outcome(starving));

        Assert.Equal(0.5, value.Hunger.Value, 9);
    }

    [Fact]
    public void ExhaustionCostsNoCapacityBecauseTheGameGivesNoRestCategoryAnEffect()
    {
        // Rest reaches zero three hours before the day ends; the tables carry no rest stage, so the term is a known 0.
        var threeHours = 3 * GameTime.TicksPerHour;
        var pawn = Pawn() with { Needs = [new("rest", Known(.5), Known(.5 / (Day - threeHours)), Known(.28)), new("food", Known(1d), Known(0d), Known(.3))] };
        var exhausted = Timeline(pawn) with { Mood = Mood(0, [0, 0, 0]) };
        Assert.Equal(threeHours, Assert.Single(exhausted.Needs, n => n.Kind == "rest").ZeroTicks.Value, 3);

        var value = ForecastValue.Of(Outcome(exhausted), Context(pawn), Day, Outcome(exhausted));

        Assert.Equal(0, value.Rest.Value);
        Assert.Equal(0, value.Total.Value);
    }

    [Fact]
    public void AnUnassessedRestTimelineLeavesTheRestTermUnknown()
    {
        var pawn = Pawn();
        var unknown = Calm() with { Needs = [new("rest", default, default, default, default), .. Calm().Needs.Where(n => n.Kind != "rest")] };

        var value = ForecastValue.Of(Outcome(unknown), Context(pawn), Day, Outcome(unknown));

        Assert.False(value.Rest.IsKnown);
        Assert.False(value.Total.IsKnown);
        Assert.Contains("rest_unassessed:pawn", value.Findings);
    }

    [Fact]
    public void BreakTermIsTierRiskTimesTierDays()
    {
        // A day at the minor tier and half a day at the major: breaks per day are 1 / (MTB + cooldown) of the tier.
        var game = LayoutMoodRules.MoodGame();
        var expected = 1 / (game.MTBDays[0] + game.CooldownDays) * 2 + 0.5 / (game.MTBDays[1] + game.CooldownDays) * 3;
        var needs = Calm(tiers: [Day, Day / 2, 0]);

        var value = ForecastValue.Of(Outcome(needs), Context(Pawn()), Day, Outcome(needs));

        Assert.Equal(expected, value.MoodBreaks.Value, 9);
    }

    [Fact]
    public void APawnWhoseBreakThresholdIsUnknownLeavesTheBreakTermUnassessed()
    {
        var needs = Calm() with { Mood = Mood(0, null) };

        var value = ForecastValue.Of(Outcome(needs), Context(Pawn()), Day, Outcome(needs));

        Assert.False(value.MoodBreaks.IsKnown);
        Assert.False(value.Total.IsKnown);
        Assert.Contains("mental_break_probability_unassessed", value.Findings);
    }

    [Fact]
    public void WellbeingIsTheWeightTimesTheMoodDaysLostAgainstTheBaseline()
    {
        // A quarter day more mood pressure than the baseline is a quarter mood-day lost.
        var baseline = Calm(pressure: 0);
        var worse = Calm(pressure: Day / 4);

        var value = ForecastValue.Of(Outcome(worse), Context(Pawn()), Day, Outcome(baseline));

        Assert.Equal(0.1 * 0.25, value.Wellbeing.Value, 9);
        Assert.False(ForecastValue.Of(Outcome(worse), Context(Pawn()), Day).Wellbeing.IsKnown);
    }

    [Fact]
    public void FoodPoisoningIsTheExpectedCasesTimesTheCapabilityOneCaseLosesOverItsCourse()
    {
        // Severity 1 falls a day: 0.2 day in the top stage (0.6 consciousness), 0.6 in the middle (0.5), 0.2 in the last (0.6 again).
        var poisoning = new HediffPenaltyTable
        {
            Known = true, Hediff = "FoodPoisoning", InitialSeverity = 1, SeverityPerDay = -1,
            Stages =
            [
                Stage(0, new CapacityModifier("Consciousness", 0, null, 0.6)),
                Stage(0.2, new CapacityModifier("Consciousness", 0, null, 0.5)),
                Stage(0.8, new CapacityModifier("Consciousness", 0, null, 0.6)),
            ],
        };
        var needs = Calm();

        var value = ForecastValue.Of(Outcome(needs, poisonCases: 2), Context(Pawn(), Tables(poisoning: poisoning)), Day, Outcome(needs));

        Assert.Equal(2 * (0.2 * 0.4 + 0.6 * 0.5 + 0.2 * 0.4), value.FoodPoisoning.Value, 9);
    }

    [Fact]
    public void UnknownTablesLeaveTheTotalUnassessedAndKnownOnesSumTheirTerms()
    {
        var needs = Calm(pressure: Day / 4, tiers: [Day, 0, 0]);
        var baseline = Calm();

        var unknown = ForecastValue.Of(Outcome(needs), Context(Pawn(), new CapacityPenaltyTables()), Day, Outcome(baseline));
        var known = ForecastValue.Of(Outcome(needs, poisonCases: 1), Context(Pawn()), Day, Outcome(baseline));

        Assert.False(unknown.Hunger.IsKnown);
        Assert.False(unknown.Total.IsKnown);
        Assert.Contains("capacity_penalty_table_unknown:Malnutrition", unknown.Findings);
        Assert.Contains("harm_capability_unassessed", known.Findings);
        Assert.Equal(known.Hunger.Value + known.Rest.Value + known.FoodPoisoning.Value + known.MoodBreaks.Value + known.Wellbeing.Value
            + known.Harm.Value + known.Investment.Value, known.Total.Value, 9);
        Assert.True(known.Total.Value > 0);
    }
}
