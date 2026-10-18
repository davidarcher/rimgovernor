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
        MalnutritionSeverity = Known(0d), FoodPoisoningSeverity = Known(0d), HypothermiaSeverity = Known(0d), HeatstrokeSeverity = Known(0d),
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

    // By default the pawn's whole day is assessed with no span outside its comfort range, so the thermal term is a known 0.
    private static ForecastCoupledOutcome Outcome(ForecastNeedsResult needs, double poisonCases = 0, ForecastThermalExposure? exposure = null) =>
        new(new([], 0, 0), Known(true), [], new Dictionary<string, ForecastNeedsResult> { ["pawn"] = needs }, [])
        { Support = new() { FoodPoisonExposure = Known(poisonCases) }, ThermalExposure = [exposure ?? new("pawn", Day, 0, 0, 0)] };

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
        Hypothermia = Thermal("Hypothermia"), Heatstroke = Thermal("Heatstroke"),
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
        Assert.Equal(known.Hunger.Value + known.Rest.Value + known.FoodPoisoning.Value + known.Thermal.Value + known.MoodBreaks.Value + known.Wellbeing.Value
            + known.Harm.Value + known.Investment.Value, known.Total.Value, 9);
        Assert.True(known.Total.Value > 0);
    }

    private static HediffPenaltyTable Thermal(string name, params PenaltyStage[] stages) => new()
    {
        Known = true, Hediff = name, LethalSeverity = 1, InitialSeverity = 0.001, Stages = stages.Length > 0 ? stages : [Stage(0, Half("Consciousness"))],
    };

    private static CapacityModifier Post(double factor) => new("Consciousness", 0, null, factor);

    private static CapacityPenaltyTables ThermalTables(HediffPenaltyTable cold, HediffPenaltyTable? hot = null) =>
        Tables() with { Hypothermia = cold, Heatstroke = hot ?? Thermal("Heatstroke") };

    // Comfort 10..30, so the safe range is 0..40.
    private static ForecastThermalSpan Span(double start, double end, double from, double to) => new(start, end, from, to, 10, 30);

    private static ForecastThermalExposure Exposure(params ForecastThermalSpan[] spans) => new("pawn", Day, 0, 0, 0) { Spans = EquatableArray.From(spans) };

    private static ForecastValueResult Thermally(NeedsForecastPawn pawn, CapacityPenaltyTables tables, params ForecastThermalSpan[] spans) =>
        ForecastValue.Of(Outcome(Calm(), exposure: Exposure(spans)), Context(pawn, tables), Day, Outcome(Calm()));

    // 20 degrees under the comfort minimum is 10 under the safe minimum: shallower than the floor's depth, so the gain is the floor.
    [Fact]
    public void SixHoursTwentyDegreesUnderComfortAccruesTheFloorSeverityAndLosesTheStagePath()
    {
        const double SixHours = 6 * GameTime.TicksPerHour;
        const double PerTick = 0.00075 / 60;
        var table = Thermal("Hypothermia", Stage(0, Post(0.8)), Stage(0.1, Post(0.5)));

        var cold = Thermally(Pawn(), ThermalTables(table), Span(0, SixHours, -10, -10));
        var comfortable = Thermally(Pawn(), ThermalTables(table), Span(0, SixHours, 20, 20));

        var inFirstStage = 0.1 / PerTick;
        Assert.Equal((inFirstStage * 0.2 + (SixHours - inFirstStage) * 0.5) / Day, cold.Thermal.Value, 9);
        Assert.Equal(0, comfortable.Thermal.Value);
        Assert.Equal(cold.Thermal.Value, cold.Total.Value, 9);
    }

    [Fact]
    public void ARampDeeperUnderTheSafeMinimumGainsAQuadraticSeverityCutAtTheStageBoundary()
    {
        // Depth under the safe minimum (0) ramps 20 -> 60 over 15,000 ticks: gain per tick is 6.45E-05 * depth / 60, so the
        // severity at u ticks is 6.45E-05 / 60 * (20 u + 40 u^2 / (2 * 15,000)). Stage 2 begins at severity 0.1.
        const double Length = 15000;
        const double Per = 6.45E-05 / 60;
        var table = Thermal("Hypothermia", Stage(0, Post(0.8)), Stage(0.1, Post(0.5)));
        var a = 20 / Length;
        var u = (-20 + Math.Sqrt(400 + 4 * a * 0.1 / Per)) / (2 * a);

        var value = Thermally(Pawn(), ThermalTables(table), Span(0, Length, -20, -60));

        Assert.Equal((u * 0.2 + (Length - u) * 0.5) / Day, value.Thermal.Value, 6);
    }

    [Fact]
    public void RecoveryInsideComfortLowersTheSeverityAtTheMirroredRateBeforeTheNextColdSpan()
    {
        // From severity 0.5 (the pawn already carries stage 1's 0.5 consciousness), 1,000 ticks inside comfort decay it by
        // exp(-0.027 / 60 * t) to 0.5 * exp(-0.45), then the floor gain of 0.00075 per 60 ticks reaches stage 2 (0.6, post 0.4: consciousness 0.8 of what it is, still awake).
        var table = Thermal("Hypothermia", Stage(0, Post(0.5)), Stage(0.6, Post(0.4)));
        var pawn = Pawn(consciousness: 0.5) with { HypothermiaSeverity = Known(0.5) };
        var recovered = 0.5 * Math.Exp(-0.027 / 60 * 1000);
        var reach = (0.6 - recovered) / (0.00075 / 60);

        var withRecovery = Thermally(pawn, ThermalTables(table), Span(0, 1000, 20, 20), Span(1000, 40000, -10, -10));
        var withoutRecovery = Thermally(pawn, ThermalTables(table), Span(1000, 40000, -10, -10));

        Assert.Equal((39000 - reach) * 0.2 / Day, withRecovery.Thermal.Value, 9);
        Assert.Equal((39000 - 0.1 / (0.00075 / 60)) * 0.2 / Day, withoutRecovery.Thermal.Value, 9);
    }

    [Fact]
    public void SeverityReachingLethalChargesTheRemainingHorizonAndTheTerminalValue()
    {
        // At 0.9 the floor gain reaches the lethal 1 in 8,000 ticks; the pawn already carries the only stage, so nothing is lost before.
        var table = Thermal("Hypothermia", Stage(0, Post(0.5)));
        var pawn = Pawn(consciousness: 0.5) with { HypothermiaSeverity = Known(0.9) };

        var value = Thermally(pawn, ThermalTables(table), Span(0, 20000, -10, -10));

        Assert.Equal((Day - 0.1 / (0.00075 / 60)) / Day + new ForecastValuePolicy().DeathTerminalValue, value.Thermal.Value, 9);
        Assert.Contains("lethal_severity_reached:Hypothermia:pawn", value.Findings);
    }

    [Fact]
    public void HeatAboveTheSafeMaximumGainsThroughTheOverageCurve()
    {
        // 65 is 25 over the safe maximum (40), where the curve is 25: 25 * 6.45E-05 per 60 ticks. Stage 2 begins at 0.05.
        var hot = Thermal("Heatstroke", Stage(0, Post(0.9)), Stage(0.05, Post(0.5)));
        const double Length = 4000;
        var perTick = 25 * 6.45E-05 / 60;
        var first = 0.05 / perTick;

        var value = Thermally(Pawn(), ThermalTables(Thermal("Hypothermia"), hot), Span(0, Length, 65, 65));

        Assert.Equal((first * 0.1 + (Length - first) * 0.5) / Day, value.Thermal.Value, 9);
    }

    [Fact]
    public void UnknownOccupancyLeavesTheThermalTermUnassessedWithItsFinding()
    {
        var value = ForecastValue.Of(Outcome(Calm(), exposure: new("pawn", Day / 2, Day / 2, 0, 0)), Context(Pawn()), Day, Outcome(Calm()));

        Assert.False(value.Thermal.IsKnown);
        Assert.False(value.Total.IsKnown);
        Assert.Contains("unassessed_thermal_exposure", value.Findings);
        Assert.True(value.Hunger.IsKnown);
    }

    [Fact]
    public void ATableTheCatalogLacksLeavesTheThermalTermUnknown()
    {
        var value = ForecastValue.Of(Outcome(Calm()), Context(Pawn(), Tables() with { Hypothermia = new() }), Day, Outcome(Calm()));

        Assert.False(value.Thermal.IsKnown);
        Assert.Contains("capacity_penalty_table_unknown:Hypothermia/Heatstroke", value.Findings);
    }
}
