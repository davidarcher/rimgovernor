using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3825: a pawn's value is the work it adds against what the colony is short of, less its food, break and bed upkeep.
public sealed class PawnValueTests
{
    private const double Day = GameTime.TicksPerDay;

    private static readonly MoodBreakModel Breaks = new() { Threshold = 0.35, Tiers = [new(1, 2), new(1, 3), new(1, 5)] };

    private static readonly FoodWorkFacts Food = new()
    {
        Crop = "Plant_Rice", GrowTicksPerNutrition = 600, Meal = "CookMealSimple", CookTicksPerNutrition = 300, IngredientPerNutrition = 0.5,
        Harvest = new(0.05, 75, 1), HarvestRots = true, Dish = new(0.9, 10, 1), DishesPerRun = 1, Benches = ["Stove"],
        MoveStat = "MoveSpeed", CarryStat = "CarryingCapacity",
    };

    private static readonly FoodHaulLegs Legs = new() { Field = true, Bench = true, FieldToStore = 20, SourceToBench = 10, BenchToDish = 5 };

    private static PawnValueColony Colony(double shortTicks, bool vacant = true) => new()
    {
        Shortfall = new() { Rows = [new("Construction", "ConstructionSpeed", shortTicks)] },
        Food = Food, Breaks = Breaks, Bed = new(vacant, 2), Haul = Legs, HorizonDays = 10,
    };

    private static PawnValueFacts Pawn(string name, double speed) => new(name)
    {
        Speeds = new Dictionary<string, double> { ["ConstructionSpeed"] = speed, ["MoveSpeed"] = 4.6, ["CarryingCapacity"] = 75 },
        FreeTicksPerDay = 12 * GameTime.TicksPerHour, FoodPerDay = 1.6, Mood = new(0.6, 0.35),
    };

    [Fact]
    public void ASkilledPawnInANeededWorkTypeHasAPositiveValue()
    {
        var value = PawnValueRules.Of(Pawn("Anna", 1.5), Colony(30_000));
        // 12 free hours at 1.5 cover the 30,000 short ticks in 20,000 of them; the rest of its time adds nothing.
        Assert.Equal(30_000 / Day, value.WorkAdded, 9);
        // #3849: at 60/4.6 ticks a cell a nutrition's 10 rice ride 10/75 of a carry 20 cells and back, the cook fetches a run's 9
        // in one 10-cell round trip a 0.9-nutrition meal, and carries the meal 5 cells and back.
        var cell = 60 / 4.6;
        var haul = (10 / 75.0 * 2 * 20 * cell) + (1 / 0.9 * 2 * 10 * cell) + (1 / 0.9 * 2 * 5 * cell);
        Assert.Equal(1.6 * haul / Day, value.FoodHaul, 9);
        Assert.Equal(1.6 * (300 + (0.5 * 600) + haul) / Day, value.FoodUpkeep, 9);
        Assert.DoesNotContain("food_hauling", value.NotModelled);
        Assert.Equal(0, value.BreakUpkeep);
        Assert.Equal(0, value.BedUpkeep);
        Assert.True(value.PerDay > 0);
        Assert.Contains("comparative_advantage", value.NotModelled);
    }

    [Fact]
    public void AnIncapablePawnWithHighUpkeepHasANegativeValue()
    {
        var pawn = Pawn("Bert", 1.5) with { Incapable = ["Construction"], FoodPerDay = 3, Mood = new(0.1, 0.35) };
        var value = PawnValueRules.Of(pawn, Colony(30_000, vacant: false));
        Assert.Equal(0, value.WorkAdded);
        Assert.Empty(value.Work);
        Assert.True(value.BreakUpkeep > 0);
        Assert.Equal(2 / 10.0, value.BedUpkeep, 12);
        Assert.True(value.PerDay < 0);
    }

    [Fact]
    public void TwoPawnsDifferingOnlyInSkillRankBySkill()
    {
        var colony = Colony(100_000);
        var slow = PawnValueRules.Of(Pawn("Cara", 0.8), colony);
        var fast = PawnValueRules.Of(Pawn("Dale", 1.2), colony);
        Assert.Equal(30_000 * 0.8 / Day, slow.WorkAdded, 9);
        Assert.Equal(30_000 * 1.2 / Day, fast.WorkAdded, 9);
        Assert.True(fast.PerDay > slow.PerDay);
    }

    [Fact]
    public void ThePawnsFreeTimeFillsItsFastestShortWorkFirstAndStopsAtEachRow()
    {
        var colony = Colony(0) with { Shortfall = new() { Rows = [new("Construction", "ConstructionSpeed", 10_000), new("Hauling", "", 100_000)] } };
        var pawn = Pawn("Anna", 2);
        var value = PawnValueRules.Of(pawn, colony);
        // 5,000 ticks at 2 finish the construction row; the other 25,000 haul at 1.
        Assert.Equal(new PawnWorkShare[] { new("Construction", "ConstructionSpeed", 10_000), new("Hauling", "", 25_000) }, value.Work);
        Assert.Equal(35_000 / Day, value.WorkAdded, 9);
    }

    [Fact]
    public void AMissingFactThrowsRatherThanValuingAPawnAtNothing()
    {
        Assert.Throws<RequiredFactException>(() => PawnValueRules.Of(Pawn("Anna", 1) with { Speeds = new Dictionary<string, double>() }, Colony(1000)));
        Assert.Throws<RequiredFactException>(() => PawnValueRules.Of(Pawn("Anna", 1), Colony(1000) with { Bed = null }));
        Assert.Throws<RequiredFactException>(() => PawnValueRules.Of(Pawn("Anna", 1) with { Mood = new(0.1, 0.35) }, Colony(1000) with { Breaks = new() }));
    }

    [Fact]
    public void AColonyWithNoCropNamesItsFoodSource()
    {
        var value = PawnValueRules.Of(Pawn("Anna", 1), Colony(0) with { Food = new() { MoveStat = "MoveSpeed", CarryStat = "CarryingCapacity" } });
        Assert.Equal(0, value.FoodUpkeep);
        Assert.Contains("food_source_not_crop", value.NotModelled);
    }

    // #3849: a haul job lasts at least 30 ticks (JobDriver_HaulToCell.MinimumHaulingJobTicks); the cook's fetch has no floor.
    [Fact]
    public void AShortHaulTakesTheHaulJobsLeastTicks()
    {
        var legs = Legs with { FieldToStore = 1, SourceToBench = 1, BenchToDish = 1 };
        var cell = 60 / 4.6;
        var ticks = FoodHaulRules.TicksPerNutrition(Food, legs, cell, 75);
        Assert.Equal((10 / 75.0 * 30) + (1 / 0.9 * 2 * cell) + (1 / 0.9 * 30), ticks, 9);
    }

    // #3849: a slower, weaker pawn costs more to haul its food: Pawn.TicksPerMove over MoveSpeed, a carry of CarryingCapacity's units.
    [Fact]
    public void ASlowerWeakerPawnCostsMoreToHaulItsFood()
    {
        var strong = PawnValueRules.Of(Pawn("Anna", 1), Colony(0));
        var weak = PawnValueRules.Of(Pawn("Bert", 1) with { Speeds = new Dictionary<string, double> { ["ConstructionSpeed"] = 1, ["MoveSpeed"] = 2.3, ["CarryingCapacity"] = 30 } }, Colony(0));
        Assert.True(weak.FoodHaul > 2 * strong.FoodHaul);
        Assert.Equal(450, FoodHaulRules.TicksPerCell(0));
        Assert.Equal(1, FoodHaulRules.TicksPerCell(1000));
    }

    // #3849: a plan with no field hauls no harvest and names it; a meal with no planned bench, or a leg no route joins, throws.
    [Fact]
    public void AnUnplannedFieldIsNamedAndAnUnplannedBenchThrows()
    {
        var fieldless = PawnValueRules.Of(Pawn("Anna", 1), Colony(0) with { Haul = Legs with { Field = false, FieldToStore = 0 } });
        Assert.Contains("food_field_unplanned", fieldless.NotModelled);
        Assert.True(fieldless.FoodHaul > 0);
        Assert.Throws<RequiredFactException>(() => PawnValueRules.Of(Pawn("Anna", 1), Colony(0) with { Haul = Legs with { Bench = false } }));
        Assert.Throws<RequiredFactException>(() => PawnValueRules.Of(Pawn("Anna", 1), Colony(0) with { Haul = Legs with { FieldToStore = double.PositiveInfinity } }));
        Assert.Throws<RequiredFactException>(() => PawnValueRules.Of(Pawn("Anna", 1) with { Speeds = new Dictionary<string, double> { ["ConstructionSpeed"] = 1 } }, Colony(0)));
    }

    /// <summary>
    /// The production path on a fresh colony (#3822, #3849): the first layout plan the layout derives for three colonists with a
    /// rotting raw stock holds a field, a cold store, the catalog's stove and a table, and its walks are the legs a colonist's food hauls.
    /// </summary>
    [Fact]
    public void AFreshColonysFirstPlanHaulsItsFoodFromFieldToStoreToStoveToTable()
    {
        var legs = FreshLegs();
        Assert.True(legs.Field && legs.Bench);
        Assert.InRange(legs.FieldToStore, 1, 200);
        Assert.InRange(legs.SourceToBench, 1, 200);
        Assert.InRange(legs.BenchToDish, 1, 200);

        var value = Assert.Single(Inspected(sites: 1, stock: 100, finishTicks: 3 * Day).PawnValues);
        var food = Game.WorkSpeedFactsTests.Catalog().FoodWork(new HashSet<string>(StringComparer.Ordinal));
        var haul = FoodHaulRules.TicksPerNutrition(food, legs, FoodHaulRules.TicksPerCell(4.6), 75);
        Assert.True(haul > 0);
        Assert.Equal(1.6 * haul / Day, value.FoodHaul, 9);
        Assert.DoesNotContain("food_hauling", value.NotModelled);
    }

    // The first plan's food walks, the layout derived the production way (FirstLayoutPlanTests) with a rotting raw stock to store.
    private static FoodHaulLegs FreshLegs()
    {
        var survey = FirstLayoutPlanTests.Survey("mild");
        var raw = new Resource("TestRaw");
        var steel = new Resource("Steel");
        var facts = FirstLayoutPlanTests.Priced(FirstLayoutPlanTests.Facts(survey, 3), "mild") with
        {
            FoodReserveDays = 5,
            Stock = new Dictionary<Resource, long> { [raw] = 300, [steel] = 300 },
            StackLimit = new Dictionary<Resource, long> { [raw] = 75, [steel] = 75 },
            Perishable = new Dictionary<Resource, bool> { [raw] = true },
        };
        var (plan, _) = LayoutDeriveRules.DeriveLayoutPlan(survey, facts, []);
        return FoodHaulRules.Legs(plan!, Game.WorkSpeedFactsTests.Catalog().FoodWork(new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void AVacantColonistBedIsOneNoOneOwns()
    {
        var sleeping = new SleepingObservation
        {
            Beds =
            [
                new() { Id = "b2", Humanlike = true }, new() { Id = "b1", Humanlike = true },
                new() { Id = "owned", Humanlike = true, Owners = [new("Anna")] }, new() { Id = "medical", Humanlike = true, Medical = true },
                new() { Id = "prison", Humanlike = true, Prisoners = true }, new() { Id = "pet", Humanlike = false },
            ],
        };
        Assert.Equal(["b1", "b2"], PawnValueRules.VacantColonistBeds(sleeping));
    }

    /// <summary>
    /// The production path (#3822): a fresh colony's construction review, the catalog's work map and food work, the needs read's
    /// upkeep and tier 2's input; a frame of more work than the horizon holds is short, and the builder's value takes it on.
    /// </summary>
    [Fact]
    public void TheReviewValuesEachColonistAgainstTheWorkItsSelectionLeftShort()
    {
        var findings = Inspected(sites: 1, stock: 100, finishTicks: 3 * Day);
        var row = Assert.Single(Assert.IsType<WorkShortfall>(findings.WorkShortfall).Rows);
        Assert.Equal(("Construction", "ConstructionSpeed"), (row.WorkType, row.Stat));
        Assert.True(row.TicksPerDay > 0);
        var value = Assert.Single(findings.PawnValues);
        Assert.Equal("pawn", value.Pawn);
        Assert.True(value.WorkAdded > 0);
        Assert.True(value.FoodUpkeep > 0);
    }

    [Fact]
    public void AFrameTheStockCannotSupplyIsNotWorkTheColonyIsShortOf()
    {
        // Two sites and the stock of one: the second waits on material, and the builder's idle time would do its work.
        var findings = Inspected(sites: 2, stock: 100, finishTicks: 30);
        Assert.Empty(Assert.IsType<WorkShortfall>(findings.WorkShortfall).Rows);
        Assert.Equal(0, Assert.Single(findings.PawnValues).WorkAdded);
    }

    /// <summary>
    /// The production path on a fresh colony with no recreation building (#3822): tier 2's input built by RoundsColonyModel.InputOf
    /// from a needs read with no bed or recreation target, so the colonist's recreation is the walk the native read gives
    /// (NeedRate.UnservedGainPerTick), its free hours are known and the review values it instead of throwing. The walk serves only
    /// the weather that allows it (NeedRate.OutdoorShareByTwelfth, #3825 D7): a winter twelfth with none leaves the joy unserved and
    /// the horizon's free hours lie between a year of walks and a year without.
    /// </summary>
    [Fact]
    public void AFreshColonyWithNoRecreationBuildingValuesItsColonistOverTheHoursAWalkLeaves()
    {
        var projection = ColonyModelInputTests.NoBedNoRecreation(ColonyModelFixtures.Projection(), ColonyModelInputTests.Walk);
        var input = ColonyModelFixtures.ReadyOf(projection);
        var pawn = input.Pawns[0];
        var free = PawnCycle.FreeHoursByPawn(input, 30)[pawn.Pawn];
        var joy = pawn.Needs.Single(n => n.Kind == "joy");
        Assert.Equal(ColonyModelInputTests.Walk, joy.Gain);
        Assert.Equal(ColonyModelFixtures.OutdoorShares, joy.OutdoorShares);
        Assert.Equal(0, joy.ShelteredGain);
        var walk = 24 * joy.Fall / (joy.Fall + joy.Gain);
        Assert.Equal(0.8 * walk, Assert.IsType<PawnCycle.Scheduled>(PawnCycle.Of(input, pawn, 6)).RecreationHours, 9);
        Assert.Contains("joy_unserved:" + pawn.Pawn, PawnCycle.Of(input, pawn, 0).Findings);
        double Free(double share) => PawnCycle.FreeHoursByPawn(input with { Pawns = EquatableArray.From(new[] { pawn with
        {
            Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "joy" ? n with { OutdoorShares = EquatableArray.From(Enumerable.Repeat(share, 12)) } : n)),
        } }) }, 30)[pawn.Pawn];
        Assert.InRange(free, Free(1), Free(0));
        Assert.Equal(walk, Free(0) - Free(1), 9);

        var findings = Inspected(sites: 1, stock: 100, finishTicks: 3 * Day, input);
        var value = Assert.Single(findings.PawnValues);
        Assert.True(value.WorkAdded > 0);
        Assert.True(value.FoodUpkeep > 0);
    }

    /// <summary>
    /// The production path before the first layout plan (#3822, #3857): the Rounder reviews a colony with no saved plan, the first
    /// plan deriving only after the review (ColonyPlan.ReviewFirstPlanAsync), and sets the context's food walks to none. The review
    /// does not throw: the shortfall stands, no colonist is valued and the values name the plan they await, on the rounds_review row too.
    /// </summary>
    [Fact]
    public void AReviewBeforeTheFirstLayoutPlanValuesNoColonistAndNamesThePlanItAwaits()
    {
        var findings = Inspected(sites: 1, stock: 100, finishTicks: 3 * Day, haul: null);
        Assert.True(Assert.Single(Assert.IsType<WorkShortfall>(findings.WorkShortfall).Rows).TicksPerDay > 0);
        Assert.Empty(findings.PawnValues);
        Assert.Equal(PawnValueRules.AwaitingLayoutPlan, findings.PawnValuesAwaitingPlan);
        var attrs = RimGovernor.Brain.BuildingRuntime.Rounder.PawnValueAttrs(findings).ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal("layout_plan", attrs[RimGovernor.Brain.Telemetry.RoundsReviewTelemetry.PawnValuesAwaitingPlanKey]);
        Assert.Null(attrs[RimGovernor.Brain.Telemetry.RoundsReviewTelemetry.PawnValuesKey]);
        Assert.NotNull(attrs[RimGovernor.Brain.Telemetry.RoundsReviewTelemetry.WorkShortfallKey]);

        var planned = Inspected(sites: 1, stock: 100, finishTicks: 3 * Day);
        Assert.Equal("", planned.PawnValuesAwaitingPlan);
        Assert.Null(RimGovernor.Brain.BuildingRuntime.Rounder.PawnValueAttrs(planned).Single(a => a.Key == RimGovernor.Brain.Telemetry.RoundsReviewTelemetry.PawnValuesAwaitingPlanKey).Value);
    }

    private static RoundsFindings Inspected(int sites, int stock, double finishTicks, ColonyModelInput? model = null) =>
        Inspected(sites, stock, finishTicks, FreshLegs(), model);

    private static RoundsFindings Inspected(int sites, int stock, double finishTicks, FoodHaulLegs? haul, ColonyModelInput? model = null)
    {
        var catalog = Game.WorkSpeedFactsTests.Catalog();
        var facts = ForecastCoupledTests.ConstructionReview(sites, stock);
        var observed = facts.ForecastObserved.Coupled!;
        var needs = facts.ForecastObserved.Needs!;
        facts = facts with
        {
            MoodBreak = Breaks, WorkSpeeds = catalog.WorkSpeedFacts(), FoodWork = catalog.FoodWork(new HashSet<string>(StringComparer.Ordinal)),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = needs with
                {
                    Pawns = [needs.Pawns[0] with { Upkeep = NeedsFixtures.Upkeep with { Speeds = [new("ConstructionSpeed", 1.0), new("CookSpeed", 1.0), new("MoveSpeed", 4.6), new("CarryingCapacity", 75)], FoodPerDay = 1.6, Mood = new(0.6, 0.35) } }],
                },
                Coupled = observed with
                {
                    Construction = observed.Construction with
                    {
                        Options = [.. observed.Construction.Options.Select(o => o with { WorkGiver = "ConstructFinishFrames", Finish = new ConstructionFinish.Finishes(finishTicks, (o.Finish as ConstructionFinish.Finishes)?.SkillWork) })],
                    },
                },
            },
        };
        facts = ForecastCoupledCandidates.ReviewCoupledActivities(facts);
        var input = model ?? ColonyModelFixtures.Devilstrand();
        input = input with { Pawns = EquatableArray.From(input.Pawns.Select(p => p with { Pawn = "pawn" })) };
        var coupled = facts.CoupledAlternatives!;
        facts = facts with
        {
            CoupledAlternatives = coupled with
            {
                Context = coupled.Context with { ModelBasis = new(input, new() { MedicineTendsPerDay = 0 }), Bed = new(true, 0), FoodHaul = haul, InvestmentHorizonDays = 30 },
            },
        };
        return RoundsRules.InspectRounds(facts, new(), RoundsRules.DefaultRoundsPolicy());
    }
}
