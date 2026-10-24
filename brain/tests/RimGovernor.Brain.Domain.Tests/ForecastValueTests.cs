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

    // The ground sleep a collapse takes: wake threshold 1 over the pawn's ground gain.
    private const double Asleep = 1500;

    internal static NeedsForecastPawn Pawn(double foodLevel = 1, double fall = 0, double consciousness = 1) => NeedsFixtures.Pawn() with
    {
        Pawn = "pawn", State = "eligible", Awake = true, SleepThreshold = .75, WakeThreshold = 1d, SleepBlockedUntilTicks = 0d,
        Mood = NeedsFixtures.Mood with { Level = .5, Target = .5, BreakThreshold = .3, Memories = [] },
        Needs = [NeedsFixtures.Rate("rest", 1d, 0d, .28) with { UnservedGainPerTick = 1 / Asleep }, NeedsFixtures.Rate("food", foodLevel, fall, .3)],
        Consciousness = consciousness, Moving = 1d, Manipulation = 1d, Sight = 1d, Talking = 1d,
        MalnutritionSeverity = 0d, FoodPoisoningSeverity = 0d, HypothermiaSeverity = 0d, HeatstrokeSeverity = 0d,
    };

    private static ForecastCapacityInput Input() => new()
    {
        Pawn = new() { Id = new("pawn"), Available = Known(true), Applies = Known(true), Job = Known(new PawnJob()),
            Schedule = Known(EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleSleep, 24))) },
        Location = "start", HorizonTicks = Day, LocalHour = Known(12.0), OpportunityCostPerTick = Known(1.0),
        CurrentJobRemainingTicks = Known(0.0), Occupancy = Known(default(EquatableArray<ForecastOccupancy>)),
        LocationAfterProtectedTime = Known("bed"), ProtectTimetable = false,
    };

    internal static ForecastNeedsResult Timeline(NeedsForecastPawn pawn) => ForecastNeeds.ForecastNeedsTimeline(pawn, Input(), [], mood: false);

    private static ForecastMoodResult Mood(double pressure, double[]? tiers) => new([], default, default, default, default, default, [])
    {
        PressureTicks = Known(pressure), BreakRisk = tiers is null ? default : Known(0.0), BreakTierTicks = tiers is null ? default : EquatableArray.From(tiers),
    };

    // By default the pawn's whole day is assessed with no span outside its comfort range, so the thermal term is a known 0.
    private static ForecastCoupledOutcome Outcome(ForecastNeedsResult needs, double poisonCases = 0, ForecastThermalExposure? exposure = null) =>
        new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult> { ["pawn"] = needs }, [])
        { Support = new() { FoodPoisonExposure = poisonCases }, ThermalExposure = [exposure ?? new("pawn", Day, 0, 0, 0)] };

    private static HediffPenaltyTable Malnutrition(params PenaltyStage[] stages) => new()
    {
        Known = true, Hediff = "Malnutrition", LethalSeverity = 1, InitialSeverity = 0.5, Stages = stages,
    };

    private static PenaltyStage Stage(double min, params CapacityModifier[] mods) => new(min, 0, mods);

    private static CapacityModifier Half(string capacity) => new(capacity, 0, null, 0.5);

    internal static CapacityPenaltyTables Tables(HediffPenaltyTable? malnutrition = null, HediffPenaltyTable? poisoning = null) => new()
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

    // By default the schedule expects no raids, so the harm term is a known 0 and every other term is read alone.
    internal static ForecastCoupledContext Context(NeedsForecastPawn pawn, CapacityPenaltyTables? tables = null, HarmInputs? harm = null,
        IReadOnlyDictionary<string, EquatableArray<GearOption>>? outfits = null, ForecastValuePolicy? policy = null) => new()
    {
        Activities = new(), Needs = NeedsFixtures.Empty with { Pawns = [pawn] }, Resources = Resources(),
        CapacityPenalties = tables ?? Tables(), MoodBreaks = Breaks(),
        Harm = harm ?? new() { Schedule = new() { Assessed = true, MajorThreatsPerDay = 0d, RaidFraction = 1 } },
        Outfits = outfits ?? new Dictionary<string, EquatableArray<GearOption>>(),
        Value = policy ?? new(),
    };

    // ----- the harm term (#3546): one raid per day on a colonist who takes 5 hits (3 to 20) of 10 sharp damage at ratio 1.

    private static readonly BodyCoverage Torso = new()
    {
        Shares = [new CoverageShare { Groups = ["Torso"], Probability = 1 }],
        Layers = ["OnSkin", "Middle", "Shell"],
    };

    internal static GearOption Duster(double sharp) => new() { ID = "duster" + sharp, Quality = 2, Condition = 1, Sharp = sharp, Layers = ["Shell"], Groups = ["Torso"] };

    internal static EquatableArray<GearOption> Cloth => [Duster(0.3)];

    private static EquatableArray<GearOption> Devilstrand => [Duster(0.8)];

    private static ExposureTable Exposure(int trials = 100) => new(RaidExposure.Schema,
        [new ExposureCell("1-2", ExposureRoles.Noncombatant, trials, 5, 3, 20, 0.5, 0.4, 0.6)]);

    internal static HarmInputs RaidDay(ExposureTable? exposure = null, double raidsPerDay = 1) => new()
    {
        Schedule = new() { Assessed = true, MajorThreatsPerDay = raidsPerDay, RaidFraction = 1 },
        Composition = new() { Pawns = Known(5d), Bins = [new() { Damage = 10, Category = ArmorCategory.Sharp, Weight = 1 }] },
        RaidPoints = Known(1000d), DefenseCapacity = Known(1000d),
        Exposure = exposure ?? Exposure(), Coverage = Torso, Natural = new(),
        // A colony the storyteller sends no disease (random diseases off): an assessed schedule without incidents.
        Illness = new() { Assessed = true },
    };

    internal static NeedsForecastPawn HarmPawn() => Pawn() with
    {
        PainShockThreshold = 0.8, HealthScale = 1d, IncomingDamageFactor = 1d, InjuryHealingFactor = 1d,
        Disease = NeedsFixtures.Disease,
    };

    internal static CapacityPenaltyTables HarmTables() => Tables() with
    {
        BloodLoss = new() { Known = true, Hediff = "BloodLoss", LethalSeverity = 1, Stages = [Stage(0, new CapacityModifier("Consciousness", 0, null, 0.9))] },
    };

    private static ForecastCoupledContext HarmContext(EquatableArray<GearOption> worn, HarmInputs? harm = null, ForecastValuePolicy? policy = null) =>
        Context(HarmPawn(), HarmTables(), harm ?? RaidDay(), new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = worn }, policy);

    private static ForecastCommitment Wears(EquatableArray<GearOption> items) => new("outfit", default) { OutfitChanges = [new("pawn", items)] };

    internal static WorkResourceView Resources()
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

    // A pawn whose rest is zero over the given spans, with nothing else wrong.
    private static ForecastNeedsResult Exhausted(params ForecastNeedSpan[] spans) => Calm() with
    {
        Needs = [new("rest", Known(0d), default, default, default) { ZeroTicks = Known(spans.Sum(s => s.End - s.Start)), ZeroSpans = EquatableArray.From(spans) },
            .. Calm().Needs.Where(n => n.Kind != "rest")],
    };

    [Fact]
    public void AnExhaustedPawnLosesTheGroundSleepOfItsExpectedCollapse()
    {
        // The timeline's own zero span: rest reaches zero 20000 ticks before the day ends.
        var pawn = Pawn() with { Needs = [Pawn().Needs[0] with { Level = .5, FallPerTick = .5 / (Day - 20000) }, Pawn().Needs[1]] };
        var timeline = Timeline(pawn) with { Mood = Mood(0, [0, 0, 0]) };
        var span = Assert.Single(Assert.Single(timeline.Needs, n => n.Kind == "rest").ZeroSpans);
        Assert.Equal(Day - 20000, span.Start, 3);

        // No collapse in the first 1000 ticks at zero, then 1% a need interval (MTB 0.25 days) to 15000 and 2% (0.125) after;
        // each collapse costs the 1500-tick ground sleep, clipped to the horizon in the last 1500 ticks. About 80% collapse.
        var value = ForecastValue.Of(Outcome(timeline), Context(pawn), Day, Outcome(timeline));

        double expected = 0, awake = 1;
        for (var atZero = 1050d; atZero <= 20000; atZero += 150)
        {
            var collapse = atZero < 15000 ? 0.01 : 0.02;
            expected += awake * collapse * Math.Min(Asleep, 20000 - atZero);
            awake *= 1 - collapse;
        }
        Assert.Equal(expected / Day, value.Rest.Value, 9);
        Assert.InRange(value.Rest.Value, Asleep / Day / 2, Asleep / Day);
        Assert.Equal(value.Rest.Value, value.Total.Value, 9);
        Assert.DoesNotContain(value.Findings, f => f.StartsWith("rest_", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTimeAtZeroRestartsWithEachSpan()
    {
        // Two 1600-tick spans, each with four draws at 1% after its first 1000 ticks, well inside the horizon.
        var value = ForecastValue.Of(Outcome(Exhausted(new ForecastNeedSpan(10000, 11600), new ForecastNeedSpan(20000, 21600))), Context(Pawn()), Day, Outcome(Calm()));

        Assert.Equal((1 - Math.Pow(0.99, 8)) * Asleep / Day, value.Rest.Value, 9);
    }

    [Fact]
    public void EatingACookableIngredientRawCostsTheMealNutritionItForgoes()
    {
        // #3626: ten rice (0.05 nutrition each) cook into a 0.9 simple meal, so a raw unit gives 1/1.8 of its cooked value.
        var ratios = ForecastValue.CookedNutritionRatios(
            [ForecastPortfolioChoiceTests.Conversion("rice", "RawRice", "CookMealSimple", 100) with { IngredientCount = 10, Product = "MealSimple", ProductCount = 1 }],
            new Dictionary<Resource, double> { [new("RawRice")] = 0.05, [new("MealSimple")] = 0.9 }, new SortedSet<string>());
        Assert.Equal(1.8, ratios[new("RawRice")], 9);
        // The pawn eats 0.25 level of rice raw; its food falls 0.5 a day, so the forgone 0.2 level is 0.4 food-days.
        var pawn = Pawn(fall: 0.5 / Day);
        var context = Context(pawn) with
        {
            CookedNutritionRatio = ratios,
            NeedTasks = [new("eat", "pawn", "food", "rice", new("RawRice"), 5, Known(0.25), []), new("meal", "pawn", "food", "cell/1/1", new("MealSimple"), 1, Known(0.9), [])],
        };
        ForecastCoupledOutcome Eating(string task) => Outcome(Calm()) with { Plan = new([new(task, "pawn", 0, 0, 0, 100, 0, 0, 100, 0, 0)], 100, 0) };

        var raw = ForecastValue.Of(Eating("eat"), context, Day, Outcome(Calm()));
        var meal = ForecastValue.Of(Eating("meal"), context, Day, Outcome(Calm()));

        Assert.Equal(0.4, raw.RawIngredients.Value, 9);
        Assert.Equal(0, meal.RawIngredients.Value);
        Assert.Equal(0.4, raw.LossAgainst(meal).Value, 9);
        // An eater whose food fall is unobserved leaves the term unknown.
        var unobserved = Pawn() with { Needs = [Pawn().Needs[0], Pawn().Needs[1] with { FallPerTick = default }] };
        var unknown = ForecastValue.Of(Eating("eat"), context with { Needs = NeedsFixtures.Empty with { Pawns = [unobserved] } }, Day, Outcome(Calm()));
        Assert.False(unknown.RawIngredients.IsKnown);
        Assert.Contains("raw_ingredient_loss_unassessed:pawn", unknown.Findings);
    }

    [Fact]
    public void AnExhaustedPawnWithNoObservedGroundGainLeavesTheRestTermUnknown()
    {
        var pawn = Pawn() with { Needs = [Pawn().Needs[0] with { UnservedGainPerTick = default }, Pawn().Needs[1]] };

        var value = ForecastValue.Of(Outcome(Exhausted(new ForecastNeedSpan(30000, 50000))), Context(pawn), Day, Outcome(Calm()));

        Assert.False(value.Rest.IsKnown);
        Assert.Contains("rest_collapse_unassessed:pawn", value.Findings);
        // A pawn whose rest never reaches zero needs no gain: its rest costs nothing.
        Assert.Equal(0, ForecastValue.Of(Outcome(Calm()), Context(pawn), Day, Outcome(Calm())).Rest.Value);
    }

    [Fact]
    public void SleepingAnExhaustedPawnBeatsWorkingWhenFoodZeroTimeIsUnavoidableAndEqual()
    {
        // Review 12245 (#3598): food is at zero for the same span in both portfolios, so the urgent-zero floor ties; the
        // portfolio that keeps working leaves rest at zero for 33068 ticks but has less joy exposure. The sleep wins on rest.
        var hungry = Calm().Needs.Single(n => n.Kind == "food") with { ZeroTicks = Known(47025d), ZeroSpans = [new(Day - 47025, Day)] };
        static ForecastNeedMetric Joy(double exposure) => new("joy", Known(.5), Known(0d), Known(exposure), Known(0d)) { ZeroTicks = Known(0d), ThresholdDepthTicks = Known(0d) };
        var exhausted = Exhausted(new ForecastNeedSpan(Day - 33068, Day)).Needs.Single(n => n.Kind == "rest");
        var rested = Calm().Needs.Single(n => n.Kind == "rest");
        var working = Calm() with { Needs = [exhausted, hungry, Joy(0)] };
        var sleeping = Calm() with { Needs = [rested, hungry, Joy(5000)] };
        var context = Context(Pawn());

        var work = ForecastCoupledSelection.CoupledConsequences(Outcome(working), [], context, Day, Outcome(sleeping));
        var sleep = ForecastCoupledSelection.CoupledConsequences(Outcome(sleeping), [], context, Day, Outcome(sleeping));

        Assert.Equal(47025, sleep.Value.UrgentZero.Excess, 3);
        Assert.Equal(work.Value.UrgentZero.Excess, sleep.Value.UrgentZero.Excess);
        Assert.Equal(work.Value.Ledger.Hunger.Value, sleep.Value.Ledger.Hunger.Value, 9);
        Assert.True(work.Value.Ledger.Rest.Value > sleep.Value.Ledger.Rest.Value + new ForecastValuePolicy().TieResolution);
        Assert.True(ForecastCoupledSelection.CompareConsequences(sleep, work) < 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(work, sleep) > 0);
    }

    [Fact]
    public void ASubTickUrgentZeroResidueDoesNotOutrankTheRestLoss()
    {
        // Review 0 of production/coupled-needs (#3598): the sleeping plan's food zero time summed to 39374.99978028882 ticks and the
        // working plan's to ...881, and that residue ranked the working plan first despite its 0.40 days of rest loss.
        static ForecastNeedMetric Hungry(double zero) => Calm().Needs.Single(n => n.Kind == "food") with { ZeroTicks = Known(zero), ZeroSpans = [new(Day - zero, Day)] };
        var exhausted = Exhausted(new ForecastNeedSpan(Day - 33068, Day)).Needs.Single(n => n.Kind == "rest");
        var rested = Calm().Needs.Single(n => n.Kind == "rest");
        var working = Calm() with { Needs = [exhausted, Hungry(39374.99978028881)] };
        var sleeping = Calm() with { Needs = [rested, Hungry(39374.99978028882)] };
        var context = Context(Pawn());

        var work = ForecastCoupledSelection.CoupledConsequences(Outcome(working), [], context, Day, Outcome(sleeping));
        var sleep = ForecastCoupledSelection.CoupledConsequences(Outcome(sleeping), [], context, Day, Outcome(sleeping));

        Assert.True(sleep.Value.UrgentZero.Excess > work.Value.UrgentZero.Excess);
        Assert.True(ForecastCoupledSelection.CompareConsequences(sleep, work) < 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(work, sleep) > 0);
        // A whole tick is a different floor.
        var later = ForecastCoupledSelection.CoupledConsequences(Outcome(sleeping with { Needs = [rested, Hungry(39376)] }), [], context, Day, Outcome(sleeping));
        Assert.True(ForecastCoupledSelection.CompareConsequences(later, work) > 0);
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
        Assert.DoesNotContain("harm_capability_unassessed", known.Findings);
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
        var pawn = Pawn(consciousness: 0.5) with { HypothermiaSeverity = 0.5 };
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
        var pawn = Pawn(consciousness: 0.5) with { HypothermiaSeverity = 0.9 };

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

    // ----- harm (#3546)

    private static ForecastValueResult Harmed(EquatableArray<GearOption> worn, HarmInputs? harm = null, ForecastValuePolicy? policy = null,
        ForecastCommitment? commitment = null)
    {
        var context = HarmContext(worn, harm, policy);
        return ForecastValue.Of(Outcome(Calm()), context, Day, Outcome(Calm()), commitment is null ? null : [commitment]);
    }

    [Fact]
    public void HarmIsPositiveAndFiniteAndNamesItsDetailWithoutTheCapabilityFinding()
    {
        var value = Harmed(Cloth);

        Assert.True(value.Harm.IsKnown);
        Assert.True(value.Harm.Value > 0);
        Assert.True(double.IsFinite(value.Harm.Value));
        Assert.True(value.Total.IsKnown);
        Assert.NotNull(value.HarmDetail);
        Assert.Equal(1, value.HarmDetail!.RaidsPerDay);
        Assert.Equal(3, value.HarmDetail.ExposureLo);
        Assert.Equal(20, value.HarmDetail.ExposureHi);
        Assert.DoesNotContain("harm_capability_unassessed", value.Findings);
        Assert.Contains("exposure_layout_unassessed", value.Findings);
    }

    [Fact]
    public void ADevilstrandOutfitInPlaceOfClothLowersHarmAndTheLedgerFavoursIt()
    {
        var cloth = Harmed(Cloth);
        var swapped = Harmed(Cloth, commitment: Wears(Devilstrand));

        Assert.True(swapped.Harm.Value < cloth.Harm.Value);
        Assert.True(swapped.LossAgainst(cloth).Value < 0);
        Assert.Equal(Harmed(Devilstrand).Harm.Value, swapped.Harm.Value, 9);
    }

    // A crafted devilstrand duster delivered at the given tick: an overlay of the worn outfit, timed by the outcome (#3621).
    private static ForecastValueResult Crafted(Fact<double>? delivered)
    {
        var craft = new ForecastCommitment("craft", default)
        { ActivityCompletions = ["deliver"], OutfitChanges = [new("pawn", Devilstrand) { Overlay = true }] };
        var outcome = Outcome(Calm()) with { OutfitTicks = delivered is { } at ? [new("craft", at)] : [] };
        return ForecastValue.Of(outcome, HarmContext(Cloth), Day, Outcome(Calm()), [craft]);
    }

    [Fact]
    public void ACraftedDustersArmorIsPricedOnlyFromItsDelivery()
    {
        var cloth = Harmed(Cloth).Harm.Value;
        var devilstrand = Harmed(Devilstrand).Harm.Value;

        Assert.Equal(devilstrand, Crafted(Known(0d)).Harm.Value, 9);
        // Delivered at midday: the cloth duster meets the morning's raids, the devilstrand one the afternoon's.
        Assert.Equal((cloth + devilstrand) / 2, Crafted(Known(Day / 2)).Harm.Value, 9);
        Assert.Equal(cloth, Crafted(Known(double.PositiveInfinity)).Harm.Value, 9);
        var unknown = Crafted(default(Fact<double>));
        Assert.False(unknown.Harm.IsKnown);
        Assert.Contains("outfit_change_timing_unknown:pawn", unknown.Findings);
        Assert.False(Crafted(null).Harm.IsKnown);
    }

    [Fact]
    public void AFlakVestIsWorthTheExpectedDamageItReduces()
    {
        // #3816 acceptance: a flak vest crafted over the cloth duster adds its armor to the torso; the harm term prices the
        // expected damage it saves from its delivery, and nothing else credits it.
        var vest = Duster(0.8) with { ID = "flakvest", Definition = new("Apparel_FlakVest"), Slot = GearSlot.MiddleTorso, Layers = ["Middle"] };
        var craft = new ForecastCommitment("craft", default) { ActivityCompletions = ["deliver"], OutfitChanges = [new("pawn", [vest]) { Overlay = true }] };
        var vested = ForecastValue.Of(Outcome(Calm()) with { OutfitTicks = [new("craft", Known(0d))] }, HarmContext(Cloth), Day, Outcome(Calm()), [craft]);
        var cloth = Harmed(Cloth);
        var both = Harmed([.. Cloth, vest]);

        Assert.True(vested.Harm.Value < cloth.Harm.Value);
        Assert.Equal(both.Harm.Value, vested.Harm.Value, 9);
        Assert.True(vested.LossAgainst(cloth).Value < 0);
        Assert.DoesNotContain(vested.InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOverlayReplacesOnlyTheItemsItConflictsWith()
    {
        // A swap to devilstrand pants at the start keeps them under the duster crafted later: one outfit holds both changes.
        var pants = new GearOption { ID = "pants", Quality = 2, Condition = 1, Sharp = 0.5, Slot = GearSlot.SkinLegs, Layers = ["OnSkin"], Groups = ["Legs"] };
        var duster = Duster(0.8) with { Slot = GearSlot.Outer };
        var worn = EquatableArray.From([Duster(0.3) with { Slot = GearSlot.Outer }]);
        var swap = new ForecastCommitment("swap", default) { OutfitChanges = [new("pawn", [.. worn, pants])] };
        var craft = new ForecastCommitment("craft", default) { ActivityCompletions = ["deliver"], OutfitChanges = [new("pawn", [duster]) { Overlay = true }] };

        var outfits = HarmLedger.OutfitsOf("pawn", worn, [craft, swap], [new("craft", Known(Day / 4))], Day)!;

        Assert.Equal([0.25, 0.75], outfits.Select(o => o.Share));
        Assert.Equal(["duster0.3", "pants"], outfits[0].Outfit.Select(o => o.ID));
        Assert.Equal(["pants", "duster0.8"], outfits[1].Outfit.Select(o => o.ID));
    }

    [Fact]
    public void AnUncalibratedExposureCellLeavesHarmUnknownWithTheCellFindingAndTheOtherTermsStillCompare()
    {
        var uncalibrated = Harmed(Cloth, RaidDay(Exposure(trials: 5)));

        Assert.False(uncalibrated.Harm.IsKnown);
        Assert.Null(uncalibrated.HarmDetail);
        Assert.Contains("exposure_cell_uncalibrated:1-2/noncombatant", uncalibrated.Findings);
        Assert.DoesNotContain("harm_capability_unassessed", uncalibrated.Findings);
        Assert.False(uncalibrated.Total.IsKnown);
        Assert.True(uncalibrated.Hunger.IsKnown);
        // The terms both sides assess are compared: a hungrier pawn still loses.
        var pawn = Pawn(foodLevel: 0.5, fall: 0.5 / (Day / 2));
        var starving = Timeline(pawn) with { Mood = Mood(0, [0, 0, 0]) };
        var worse = ForecastValue.Of(Outcome(starving), Context(HarmPawn(), HarmTables(), RaidDay(Exposure(trials: 5)),
            new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = Cloth }), Day, Outcome(Calm()));
        Assert.True(worse.LossAgainst(uncalibrated).Value > 0);
    }

    [Fact]
    public void AnUnobservedInputNamesItsOwnFinding()
    {
        Assert.Contains("raid_points_unobserved", Harmed(Cloth, RaidDay() with { RaidPoints = default }).Findings);
        Assert.Contains("defense_capacity_unobserved", Harmed(Cloth, RaidDay() with { DefenseCapacity = default }).Findings);
        Assert.Contains("threat_schedule_unknown", Harmed(Cloth, RaidDay() with { Schedule = new() }).Findings);
        Assert.Contains("raid_composition_unknown", Harmed(Cloth, RaidDay() with { Composition = new() }).Findings);
        var noOutfit = ForecastValue.Of(Outcome(Calm()), Context(HarmPawn(), HarmTables(), RaidDay()), Day, Outcome(Calm()));
        Assert.Contains("outfit_unobserved:pawn", noOutfit.Findings);
        Assert.False(noOutfit.Harm.IsKnown);
    }

    [Fact]
    public void ARiskWeightOfZeroPricesTheMeanAndOneThePolicysCvar()
    {
        var context = HarmContext(Cloth);
        var response = ArmorResponseRules.OfOutfit(Cloth, new NaturalArmor(), Torso, [new WeaponBin { Damage = 10, Category = ArmorCategory.Sharp, Weight = 1 }]);
        var distribution = HarmConsequences.Of(new HitRange(5, 3, 20), [new WeaponBin { Damage = 10, Category = ArmorCategory.Sharp, Weight = 1 }],
            response, HarmPawn(), HarmTables(), new ForecastSupportConsequences(), new ForecastValuePolicy(), Day);
        var (mean, cvar) = HarmLedger.Moments(distribution, context.Value.HarmRiskAlpha);

        Assert.True(cvar > mean);
        Assert.Equal(mean, Harmed(Cloth, policy: new() { HarmRiskWeight = 0 }).Harm.Value, 9);
        Assert.Equal(cvar, Harmed(Cloth, policy: new() { HarmRiskWeight = 1 }).Harm.Value, 9);
    }

    [Fact]
    public void NoExpectedRaidsIsAKnownZeroHarmWhateverElseIsUnobserved()
    {
        var value = Harmed(Cloth, new HarmInputs { Schedule = new() { Assessed = true, MajorThreatsPerDay = 0d, RaidFraction = 1 } });

        Assert.Equal(0, value.Harm.Value);
        Assert.True(value.Total.IsKnown);
    }

    [Fact]
    public void AnIllnessAddsItsTermToHarmAndLeavesTheRaidTermUnchanged()
    {
        // #3824: a disease a day at 0.1 that halves consciousness from the day it strikes; the raid day is the same in both.
        var course = new DiseaseCourse
        {
            Table = new() { Known = true, Hediff = "Flu", InitialSeverity = 0.2, LethalSeverity = 1, Immunizable = new(0.25, 0.2, -0.5), Stages = [Stage(0, Half("Consciousness"))] },
        };
        var illness = new IllnessSchedule { Assessed = true, Comps = [(0, 0.1)], Incidents = [new IllnessIncident("Disease_Flu", 1, 1, course)] };
        var pawn = HarmPawn() with { Disease = NeedsFixtures.Disease with { Contracts = [new("Disease_Flu", 1)] } };
        ForecastValueResult Value(IllnessSchedule schedule)
        {
            var context = Context(pawn, HarmTables(), RaidDay() with { Illness = schedule }, new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = Cloth });
            return ForecastValue.Of(Outcome(Calm()), context, Day, Outcome(Calm()), null);
        }

        // A storyteller the disease table does not model: no illness term, named (#3835).
        var without = Value(IllnessSchedule.Of(new DiseaseTable(), IllnessForecastTests.Facts()));
        var with = Value(illness);

        Assert.Null(without.HarmDetail!.Illness);
        Assert.Contains(IllnessForecast.Unmodelled, without.Findings);
        Assert.Contains("disease_storyteller_unknown:Cassandra", without.Findings);
        // Unread disease facts throw a required fact instead.
        Assert.Throws<RequiredFactException>(() => Value(IllnessSchedule.Of(new DiseaseTable(), null)));
        Assert.Equal(without.Harm.Value, without.HarmDetail.RaidLoss, 9);
        Assert.Equal(without.HarmDetail.RaidLoss, with.HarmDetail!.RaidLoss, 9);
        var term = with.HarmDetail.Illness;
        Assert.NotNull(term);
        Assert.Equal(0.1, term!.IncidentsPerDay, 12);
        Assert.True(term.Loss > 0);
        Assert.Equal(with.HarmDetail.RaidLoss + term.Loss, with.Harm.Value, 9);
        Assert.Equal("pawn", Assert.Single(term.Pawns).Pawn);
        Assert.DoesNotContain(IllnessForecast.Unmodelled, with.Findings);
    }

    [Fact]
    public void TheDeathFloorBreaksWhenTheOutfitRaisesDeathsOverTheReferenceAndHoldsOtherwise()
    {
        var context = HarmContext(Devilstrand);
        var reference = Outcome(Calm());

        var same = ForecastValue.Explain(reference, Known(0d), context, Day, reference);
        var stripped = ForecastValue.Explain(reference, Known(0d), context, Day, reference, [Wears([])]);

        Assert.True(same.Death.Assessed);
        Assert.True(same.Death.Passed);
        Assert.True(stripped.Death.Assessed);
        Assert.False(stripped.Death.Passed);
        Assert.Equal(stripped.Ledger.HarmDetail!.DeathsPerDay - same.Ledger.HarmDetail!.DeathsPerDay - context.Value.DeathRiskFloor, stripped.Death.Excess, 9);
    }

    [Fact]
    public void TheDeathFloorIsUnassessedAndPassesWhileHarmIsUnknown()
    {
        var context = HarmContext(Cloth, RaidDay(Exposure(trials: 5)));
        var reference = Outcome(Calm());

        var explained = ForecastValue.Explain(reference, Known(0d), context, Day, reference);

        Assert.False(explained.Death.Assessed);
        Assert.True(explained.Death.Passed);
    }

    // ----- lazy evaluation (#3723): the staged value and comparison price the floors first and the ledger only when a comparison reaches it.

    private static ForecastCoupledSelection.StagedConsequences Staged(ForecastCoupledOutcome outcome, ForecastCoupledContext context, ForecastCoupledOutcome reference,
        params ForecastCommitment[] commitments)
        => ForecastCoupledSelection.StagedConsequences.Of(outcome, commitments, context, Day, reference, ForecastValue.ReferenceHarm(reference, context, Day));

    private static ForecastNeedMetric Hungry(double zero) => Calm().Needs.Single(n => n.Kind == "food") with { ZeroTicks = Known(zero), ZeroSpans = [new(Day - zero, Day)] };

    private static ForecastNeedsResult Starving(double zero) => Calm() with { Needs = [Calm().Needs.Single(n => n.Kind == "rest"), Hungry(zero)] };

    [Fact]
    public void AStagedValuePricesTheFloorsFirstAndExplainsTheSameAsTheEagerLedger()
    {
        var context = Context(Pawn());
        var reference = Outcome(Calm());
        var staged = ForecastValue.Stage(Outcome(Starving(4000)), Known(4000d), context, Day, reference, ForecastValue.ReferenceHarm(reference, context, Day), null);

        Assert.False(staged.LedgerPriced);
        Assert.Equal(new ForecastFloor(true, 4000), staged.UrgentZero);
        Assert.True(staged.BreakRisk.Assessed);
        Assert.True(staged.Death.Assessed);
        Assert.Empty(staged.GoalContributions);

        Assert.Equal(ForecastValue.Explain(Outcome(Starving(4000)), Known(4000d), context, Day, reference), staged.Explanation);
        Assert.True(staged.LedgerPriced);
        Assert.Equal(ForecastValue.Of(Outcome(Starving(4000)), context, Day, reference), staged.Explanation.Ledger);
    }

    [Fact]
    public void TheUrgentZeroFloorDecidesAStagedComparisonWithoutPricingEitherLedgerAndTheLeastExcessWinsWhenBothBreakIt()
    {
        var context = Context(Pawn());
        var reference = Outcome(Calm());
        var calm = Staged(reference, context, reference);
        var starving = Staged(Outcome(Starving(4000)), context, reference);
        var starved = Staged(Outcome(Starving(6000)), context, reference);

        Assert.True(ForecastCoupledSelection.CompareConsequences(starving, calm, out var stage) > 0);
        Assert.Equal(ForecastCoupledSelection.UrgentZeroStage, stage);
        Assert.True(ForecastCoupledSelection.CompareConsequences(starving, starved, out stage) < 0);
        Assert.Equal(ForecastCoupledSelection.UrgentZeroStage, stage);
        Assert.All(new[] { calm, starving, starved }, s => Assert.False(s.LedgerPriced));
        // The same answers as the eager comparator over complete vectors, which prices every ledger.
        Assert.True(ForecastCoupledSelection.CompareConsequences(starving.Vector, calm.Vector) > 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(starving.Vector, starved.Vector) < 0);
        Assert.All(new[] { calm, starving, starved }, s => Assert.True(s.LedgerPriced));
        // Evidence of a loss at the floor carries no vector until the ledger is priced.
        Assert.Null(Staged(Outcome(Starving(4000)), context, reference).VectorIfPriced);
        Assert.NotNull(starving.VectorIfPriced);
    }

    [Fact]
    public void TheDeathAndBreakRiskFloorsDecideBeforeTheLedgerIsPriced()
    {
        // The death floor: the pawn stripped of its devilstrand duster dies more often than the reference wearing it (#3546).
        var harmContext = HarmContext(Devilstrand);
        var reference = Outcome(Calm());
        var worn = Staged(reference, harmContext, reference);
        var stripped = Staged(reference, harmContext, reference, Wears([]));
        Assert.Equal(ForecastValue.Explain(reference, Known(0d), harmContext, Day, reference, [Wears([])]).Death, stripped.Death);
        Assert.True(stripped.Death.Excess > 0, "stripped death excess " + stripped.Death);
        Assert.True(ForecastCoupledSelection.CompareConsequences(stripped, worn, out var stage) > 0, "stripped against worn at " + stage);
        Assert.Equal(ForecastCoupledSelection.DeathFloorStage, stage);
        Assert.False(stripped.LedgerPriced);
        Assert.False(worn.LedgerPriced);

        // The break-risk cap: five expected breaks a day against the calm pawn's none.
        var context = Context(Pawn());
        var calm = Staged(reference, context, reference);
        var breaking = Staged(Outcome(Calm() with { Mood = Mood(0, [0, 0, 0]) with { BreakRisk = Known(5d) } }), context, reference);
        Assert.True(breaking.BreakRisk.Excess > 0, "break excess " + breaking.BreakRisk);
        Assert.True(ForecastCoupledSelection.CompareConsequences(breaking, calm, out stage) > 0, "breaking against calm at " + stage);
        Assert.Equal(ForecastCoupledSelection.BreakRiskStage, stage);
        Assert.False(breaking.LedgerPriced);
        Assert.False(calm.LedgerPriced);
    }

    [Fact]
    public void ASubTickUrgentZeroTieReachesTheLedgerAndPricesBothSides()
    {
        // The #3598 residues: the floors tie within a tick, so the rest loss decides, and deciding it prices both ledgers.
        var exhausted = Exhausted(new ForecastNeedSpan(Day - 33068, Day)).Needs.Single(n => n.Kind == "rest");
        var rested = Calm().Needs.Single(n => n.Kind == "rest");
        var working = Outcome(Calm() with { Needs = [exhausted, Hungry(39374.99978028881)] });
        var sleeping = Outcome(Calm() with { Needs = [rested, Hungry(39374.99978028882)] });
        var context = Context(Pawn());
        var work = Staged(working, context, sleeping);
        var sleep = Staged(sleeping, context, sleeping);

        Assert.True(ForecastCoupledSelection.CompareConsequences(sleep, work, out var stage) < 0);
        Assert.Equal(ForecastCoupledSelection.LedgerStage, stage);
        Assert.True(work.LedgerPriced);
        Assert.True(sleep.LedgerPriced);
        Assert.True(ForecastCoupledSelection.CompareConsequences(work, sleep, out _) > 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(sleep.Vector, work.Vector) < 0);
    }

    [Fact]
    public void ATermOnlyOneSidePricesIsLeftOutOfTheLazyLedgerComparisonExactlyAsTheEagerOne()
    {
        // The wellbeing term is unknown for the outcome whose mood pressure is, and known for the other: the lazy comparison prices
        // both ledgers and compares the terms both assess, so the poisoning difference decides and the skipped term is never read as 0.
        var context = Context(Pawn());
        var reference = Outcome(Calm());
        var unpressured = Outcome(Calm() with { Mood = Mood(0, [0, 0, 0]) with { PressureTicks = default } }, poisonCases: 0);
        var poisoned = Outcome(Calm(), poisonCases: 2);
        var a = Staged(unpressured, context, reference);
        var b = Staged(poisoned, context, reference);

        var rank = ForecastCoupledSelection.CompareConsequences(a, b, out var stage);
        Assert.Equal(ForecastCoupledSelection.LedgerStage, stage);
        Assert.True(rank < 0);
        Assert.True(a.LedgerPriced);
        Assert.True(b.LedgerPriced);
        Assert.False(a.Vector.Value.Ledger.Wellbeing.IsKnown);
        Assert.True(b.Vector.Value.Ledger.Wellbeing.IsKnown);
        Assert.True(b.Vector.Value.Ledger.FoodPoisoning.Value > a.Vector.Value.Ledger.FoodPoisoning.Value);
        Assert.Equal(a.Vector.Value.Ledger.FoodPoisoning.Value - b.Vector.Value.Ledger.FoodPoisoning.Value, a.Vector.Value.Ledger.LossAgainst(b.Vector.Value.Ledger).Value, 9);
        Assert.Equal(rank, ForecastCoupledSelection.CompareConsequences(a.Vector, b.Vector));
        // Ledgers whose shared terms cannot separate the sides fall through to the later stages, still priced on both sides.
        var blind = Context(Pawn(), Tables() with { Malnutrition = new(), FoodPoisoning = new(), Hypothermia = new(), Heatstroke = new() });
        var x = Staged(Outcome(Calm() with { Mood = null }), blind, reference);
        var y = Staged(Outcome(Calm() with { Mood = null }), blind, reference);
        Assert.Equal(0, ForecastCoupledSelection.CompareConsequences(x, y, out stage));
        Assert.NotEqual(ForecastCoupledSelection.LedgerStage, stage);
        Assert.True(x.LedgerPriced && y.LedgerPriced);
    }
}
