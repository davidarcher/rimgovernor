using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The tier-2 colony model's worked case (forecast value, "Devilstrand worked through"): 800 research points, Plants 10, 400 sow work
/// per plant on soil of at least 0.5 fertility, 22.5 grow days, 6 fibre per plant and a duster of 80 fabric and 10,000 work.
/// <see cref="Reading"/> is a review frame carrying it, so <c>ReviewColonyModel.InputOf</c> is tested on the same numbers.
/// </summary>
internal static class ColonyModelFixtures
{
    internal const string Crop = "Plant_Devilstrand";

    internal const string Project = "Devilstrand";

    internal const string Prerequisite = "ComplexClothing";

    internal const string Cloth = "DevilstrandCloth";

    internal const string Recipe = "Make_Apparel_Duster";

    internal static GearClimate Climate() => new()
    {
        Temperatures = EquatableArray.From(new[] { -10.0, -8, -2, 5, 12, 18, 22, 21, 15, 8, 0, -7 }),
        CurrentTwelfth = 4,
        TicksToNextTwelfth = 12_000,
        DaylightHours = EquatableArray.From(new[] { 9.0, 10, 11.5, 13, 14.5, 15.5, 15, 14, 12.5, 11, 9.5, 8.5 }),
    };

    /// <summary>A gear census of no colonist under the fixture climate.</summary>
    internal static GearObservation NoGear() => new() { Climate = Climate(), Threat = NoThreat };

    /// <summary>A raid threat with no composition: the armor term falls back to the nominal bins.</summary>
    internal static readonly RaidThreat NoThreat = new RaidThreat.Uncomposed();

    internal static PlanningDefinition CropRow() => new()
    {
        Name = Crop,        Research = [Project],        Available = false,Plant=new PlanningPlant {        GrowDays = 22.5,        SowMinSkill = 10,        SowWork = 400.0,        FertilityMin = 0.5,        FertilitySensitivity = 0.7,        HarvestMinGrowth = 0.65,        HarvestWork = 200.0,        MinGrowthTemperature = 0.0,        MinOptimalGrowthTemperature = 10.0,        MaxOptimalGrowthTemperature = 42.0,        MaxGrowthTemperature = 58.0,        RestingDayFraction = PlantResting.RestingDayFraction},Harvest=new PlanningHarvest {        HarvestYield = 6.0,        Edible = false,        HarvestedThingDef = Cloth}    };

    internal static ColonyReading Reading() => ColonyFactsCoreTests.Reading() with
    {
        Facts = ColonyFactsCoreTests.Reading().Facts with
            {
                Census = ColonyFactsCoreTests.Reading().Snapshot with
                {
                    FoodClimate = new RimGovernor.Brain.GameFacts.Observations.FoodClimate
                    {
                        GrowingYear = EquatableArray.From(Enumerable.Repeat(true, CalendarRules.YearDays)),
                        Season = "Spring",
                        SowingNow = true,
                        DayOfYear = 12,
                        GrowingDays = 40,
                        GrowingDaysRemaining = 25,
                        GrowingDaysUntil = 0,
                        NonGrowingDays = 0,
                    },
                    ResourceThings = ["m1"],
                    Resources = EquatableArray.From((new Dictionary<Resource, long> { [new Resource("Steel")] = 120, [new Resource(Cloth)] = 0 }).Select(pair => new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = pair.Key.Value, Units = pair.Value }))
                },
            Gear = NoGear() with
            {
                Pawns = [new GearPawn { Observed = GearFixtures.Loadout("") with { Token = "" }, LoadoutModel = new GearModel.NoOutfit() }
                ]
            },
            Research = ResearchTestFacts.Read([Prerequisite, Project], [Prerequisite], Project,
                definitions: new Dictionary<string, ResearchProjectDefinition>
                {
                    [Prerequisite] = new()
                    {
                        Cost = 650
                    },
                    [Project] = new()
                    {
                        Cost = 800,
                        Prerequisites = [new ResearchProjectId(Prerequisite)],
                    },
                }, progress: new Dictionary<string, double> { [Project] = .25 }),
            MedicalReserve = new MedicalReserveObservation
            {
                Items = EquatableArray.From(new[] { new RimGovernor.Brain.GameFacts.Observations.UpkeepItem { Id = "m1", Definition = "MedicineHerbal", Count = 4, Forbidden = false, Rot = new RimGovernor.Brain.GameFacts.Observations.ItemRot.Never() }, new RimGovernor.Brain.GameFacts.Observations.UpkeepItem { Id = "m2", Definition = "MedicineIndustrial", Count = 9, Forbidden = true, Rot = new RimGovernor.Brain.GameFacts.Observations.ItemRot.Never() }, }),
            },
            NeedsAlternatives = new ForecastNeedsReview(NeedsFixtures.Empty with { Pawns = [Pawn()], ResearchBenches = [new("bench", "SimpleResearchBench", new(0, 0), 1.0)], }, new Dictionary<string, ForecastNeedsCandidates>()),
            WorkAdmission = new WorkAdmissionObservation
            {
                Recipes = EquatableArray.From(new[] { new ProductionRecipeInput(Recipe, EquatableArray.From(new[] { EquatableArray.From(new[] { new Amount(new Resource(Cloth), 80) }) }), false) { Products = EquatableArray.From(new[] { new Amount(new Resource("Apparel_Duster"), 1) }), WorkAmount = 10_000.0, }, }),
            },
            CurrentConstruction = CurrentConstruction.Empty,
            PlannedSites = new PlannedSiteFacts(),
            CombinedFoodSupply = new FoodSupply
            {
                Larder = new()
                {
                    RawMeatNutrition = 0,
                    CookDemandNutrition = 0,
                    Corpses = []
                },
                Consumers = [new FoodConsumer
                {
                    Id = new("Anna"),
                    NutritionPerDay = 1d,
                    HumanMeatAcceptable = false
                }
                ],
                Stocks = [new FoodStock
                {
                    Id = "meal",
                    Nutrition = 12.5,
                    Place = new FoodPlace.Stands(true, ""),
                    Form = new FoodForm.Item(),
                    Rot = new FoodRot.Keeps(),
                    Eaters = [new("Anna")],
                    IsHumanMeat = false,
                    RawMeat = false,
                    IsHumanlike = false,
                    Vegetable = false,
                    RawClass = FoodIngredientClass.None,
                    Holder = new PawnId(string.Empty),
                    DefName = string.Empty,
                    Count = 0,
                    UnitMass = 0,
                    TemperatureC = 0,
                    Forbidden = false
                }
                ]
            },
            Definitions = [CropRow()],
            Acquisition = [],
            ForecastObserved = new ColonyForecastObserved
            {
                WorkPawns = new WorkPawnsRead(EquatableArray.From(new[] { PawnFixtures.Worker("Anna") with { Observed = PawnFixtures.Row("Anna") with { Biography = PawnFixtures.Row("Anna").Biography with { Skills = [new RimGovernor.Brain.GameFacts.Observations.Skill { Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None, DefName = "Plants", Level = 10 }] } } } }))
            }
        },

        Window = new Held<PlanningCells>
        {
            Value = new PlanningCells
            {
                Cells = [.. Enumerable.Range(0, 4).Select(x => SiteCellFixtures.At(new Cell(x, 0), zoneId: "zone1", fertility: 0.5 + (0.1 * x)))]
            }
        },
        Tables = ColonyFactsCoreTests.Reading().Tables with
        {
            Zones = new RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot
            {
                Context = ColonyFactsCoreTests.Reading().Snapshot.Context,
                Zones = [ZoneFixtures.Growing("zone1", Crop)]
            }
        }
    };

    /// <summary>The model input of the worked case.</summary>
    internal static ColonyModelInput Devilstrand() => ReadyOf(Reading());

    /// <summary>The input a frame yields; the test fails when the frame left it unready.</summary>
    internal static ColonyModelInput ReadyOf(ColonyReading projection) => ReviewColonyModel.InputOf(projection) switch
    {
        ColonyModelInputRead.Ready ready => ready.Input,
        ColonyModelInputRead.Unready unready => throw new InvalidOperationException("the frame left the input unready: " + string.Join("; ", unready.Gaps)),
        _ => throw new InvalidOperationException("an unnamed input read"),
    };

    /// <summary>The gaps a frame leaves; empty when the input is ready.</summary>
    internal static IReadOnlyList<string> GapsOf(ColonyReading projection) =>
        ReviewColonyModel.InputOf(projection) is ColonyModelInputRead.Unready unready ? unready.Gaps : [];

    /// <summary>
    /// A temperate year's share of each twelfth in which outdoor recreation is enjoyable (NeedRate.OutdoorShareByTwelfth, #3825 D7):
    /// none in the two coldest twelfths, rising to 0.8 in summer, the rest of each lost to rain.
    /// </summary>
    internal static EquatableArray<double> OutdoorShares => EquatableArray.From(new[] { 0, 0, 0.2, 0.5, 0.7, 0.8, 0.8, 0.8, 0.7, 0.5, 0.2, 0 });

    private static NeedsForecastPawn Pawn() => NeedsFixtures.Pawn() with
    {
        Pawn = "Anna",
        Needs =
        [
            NeedsFixtures.Rate("food", 0.6, 0.00002, 0.3),
            NeedsFixtures.Rate("rest", 0.8, 0.00001, 0.28),
            NeedsFixtures.Rate("joy", 0.5, 0.00001, 0.3) with { OutdoorShareByTwelfth = OutdoorShares },
        ],
        Targets =
        [
            NeedsFixtures.Food("meal", "MealSimple", 1, 40.0, 300.0, 0.9),
            NeedsFixtures.Service("bed", "rest", 60.0, 0.00002),
            NeedsFixtures.Service("tv", "joy", 80.0, 0.00015),
        ],
        ComfortableMin = 16.0,
        ComfortableMax = 26.0,
        Consciousness = 1.0,
        Moving = 1.0,
        Manipulation = 1.0,
        Sight = 1.0,
        Talking = 1.0,
        ResearchSpeed = 1.0,
        PlantWorkSpeed = 1.0,
    };
}
