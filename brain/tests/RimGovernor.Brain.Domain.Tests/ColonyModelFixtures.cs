using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The tier-2 colony model's worked case (forecast value, "Devilstrand worked through"): 800 research points, Plants 10, 400 sow work
/// per plant on soil of at least 0.5 fertility, 22.5 grow days, 6 fibre per plant and a duster of 80 fabric and 10,000 work.
/// <see cref="Projection"/> is a review frame carrying it, so <c>RoundsColonyModel.InputOf</c> is tested on the same numbers.
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
        Name = Crop,
        Research = [Project],
        Available = Known(false),
        GrowDays = Known(22.5),
        SowMinSkill = Known(10),
        SowWork = Known(400.0),
        FertilityMin = Known(0.5),
        FertilitySensitivity = Known(0.7),
        HarvestYield = Known(6.0),
        HarvestMinGrowth = Known(0.65),
        HarvestWork = Known(200.0),
        Edible = Known(false),
        HarvestedThingDef = Known(Cloth),
        MinGrowthTemperature = Known(0.0),
        MinOptimalGrowthTemperature = Known(10.0),
        MaxOptimalGrowthTemperature = Known(42.0),
        MaxGrowthTemperature = Known(58.0),
        RestingDayFraction = Known(PlantResting.RestingDayFraction),
    };

    internal static ColonyProjection Projection() => ColonyFactsCoreTests.Projection() with
    {
        Facts = new RoundsFacts
        {
            ResourceThings = Known<EquatableArray<string>>(["m1"]),
            Gear = Known(NoGear() with { Pawns = [new GearPawn { LoadoutModel = new GearModel.NoOutfit() }] }),
            Calendar = Known(new Calendar { GrowingYear = EquatableArray.From(Enumerable.Repeat(true, CalendarRules.YearDays)), Season = "Spring", DayOfYear = 12, GrowingDays = 40, GrowingDaysRemaining = 25, GrowingDaysUntil = 0, NonGrowingDays = 0 }),
            Research = Known(new ResearchFacts
            {
                Current = new ResearchProjectId(Project),
                Finished = [new ResearchProjectId(Prerequisite)],
                Projects = [new ResearchProjectId(Prerequisite), new ResearchProjectId(Project)],
                Details = new Dictionary<string, ResearchProjectFacts>
                {
                    [Prerequisite] = new() { Cost = 650 },
                    [Project] = new() { Cost = 800, Prerequisites = [new ResearchProjectId(Prerequisite)], Progress = 0.25 },
                },
            }),
            MedicalReserve = new MedicalReserveObservation
            {
                Items = Known(EquatableArray.From(new[]
                {
                    new MedicineStackRow { Id = "m1", Definition = new Resource("MedicineHerbal"), Count = 4 },
                    new MedicineStackRow { Id = "m2", Definition = new Resource("MedicineIndustrial"), Count = 9, Forbidden = true },
                })),
            },
            NeedsAlternatives = new ForecastNeedsReview(NeedsFixtures.Empty with {
                Pawns = [Pawn()],
                ResearchBenches = [new("bench", "SimpleResearchBench", new(0, 0), 1.0)],
            }, new Dictionary<string, ForecastNeedsCandidates>()),
            WorkAdmission = new WorkAdmissionObservation
            {
                Recipes = Known(EquatableArray.From(new[]
                {
                    new ProductionRecipeInput(Recipe, EquatableArray.From(new[] { EquatableArray.From(new[] { new Amount(new Resource(Cloth), 80) }) }), false)
                    {
                        Products = EquatableArray.From(new[] { new Amount(new Resource("Apparel_Duster"), 1) }),
                        WorkAmount = 10_000.0,
                    },
                })),
            },
            CurrentConstruction = CurrentConstruction.Empty,
            PlannedSites = Known(new PlannedSiteFacts()),
        },
        CombinedFoodSupply = Known(new FoodSupply { Larder = new(), Consumers = [new() { Id = new("Anna"), NutritionPerDay = 1d }], Stocks = [new RimGovernor.Brain.Policy.FoodStock { Id = "meal", Nutrition = 12.5, Place = new FoodPlace.Stands(true, ""), Form = new FoodForm.Item(), Rot = new FoodRot.Keeps(), Eaters = [new("Anna")] }] }),
        Resources = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [new Resource("Steel")] = 120, [new Resource(Cloth)] = 0 }),
        Definitions = [CropRow()],
        Farms = [ZoneFixtures.Growing("zone1", Crop)],
        Cells = [.. Enumerable.Range(0, 4).Select(x => new SiteCell { Cell = new Cell(x, 0), Zone = Known(true), ZoneId = Known("zone1"), Fertility = Known(0.5 + (0.1 * x)) })],
        WorkPawns = Known(EquatableArray.From(new[] { new WorkPawn { Id = new PawnId("Anna"), Skills = Known(EquatableArray.From(new[] { new WorkSkill { Name = "Plants", Level = 10 } })) } })),
        Acquisition = Known(EquatableArray.From(Array.Empty<AcquisitionSource>())),
    };

    /// <summary>The model input of the worked case.</summary>
    internal static ColonyModelInput Devilstrand() => RoundsColonyModel.InputOf(Projection()).Input;

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
