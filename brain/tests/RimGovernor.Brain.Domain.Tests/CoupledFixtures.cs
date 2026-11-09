using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whole coupled forecast reads for tests that set only the members they exercise.</summary>
internal static class CoupledFixtures
{
    internal static PracticeActivityRead NoPractice => new() { Workers = [] };
    internal static RitualActivityRead NoRituals => new() { Sites = [] };
    internal static SowingActivityRead NoSowing => new() { Workers = [], Findings = [] };
    internal static SupportActivityRead NoSupport => new() { Demands = [], Workers = [], Deliveries = [], Findings = [], Patients = [] };
    internal static FoodActivityRead NoFood => new() { Options = [] };

    /// <summary>The native servicing mechanics (JobDriver_Repair 80/20 at ConstructionSpeed x 1.7, FixBrokenDownBuilding 1000, Refuel 240).</summary>
    internal static ServicingMechanics ServicingMechanics => new(1.7, 80, 20, 1000, 240, "ComponentIndustrial");
    internal static ServicingActivityRead NoServicing => new() { Mechanics = ServicingMechanics, Targets = [], Workers = [], Stock = [], Transitions = [], Findings = [] };

    /// <summary>A comfortable room's temperature (21) for a patient comfortable from 16 to 26: no exposure either side.</summary>
    internal static CareTemperature Comfortable => new CareTemperature.Read(21);

    /// <summary>A tendable care patient that does not bleed, at a comfortable temperature, with no field spot read.</summary>
    internal static SupportCarePatient CarePatient(string demand, string pawn, BedOccupant who, CarePosture posture, CareConduct conduct, params SupportCareBed[] beds) =>
        new(demand, pawn, who, posture, conduct)
        {
            Beds = [.. beds], Tendable = true, Bleeding = new CareBleeding.Stable(), Temperature = Comfortable, ComfortMin = 16, ComfortMax = 26,
            FieldSpot = new CareFieldSpot.None("in_bed"),
        };

    /// <summary>A support demand in a room that changes no infection or surgery chance, for a surgery with no outcome model.</summary>
    internal static SupportDemand Demand(string id, string kind, string target, Cell location, double poison, double bleed)
        => new(id, kind, target, location, poison, bleed)
        {
            InfectionChanceFactor = 1, SurgerySuccessFactor = 1, SurgeryBed = "surgery-bed", Served = false, SurgeryOutcomes = [], SurgeryEnvironment = false,
            SurgeryQualityMin = 0, SurgeryQualityMax = float.MaxValue,
        };

    /// <summary>A food conversion its pawn can supply, cook (in workTicks, training no skill) and carry back in
    /// deliveryTicks, from a source that needs harvesting.</summary>
    internal static CoupledFoodConversion Conversion(string source, string pawn, string bench, string recipe, string ingredient,
        int ingredientCount, string product, int productCount, Cell sourceCell, Cell benchCell, Cell destination,
        double approachTicks, double supplyTicks, double workTicks, double deliveryTicks, int carryingCapacity, int availableInput)
        => new(source, pawn, bench, recipe, ingredient, ingredientCount, product, productCount, sourceCell, benchCell, destination,
            approachTicks, supplyTicks, carryingCapacity, availableInput)
        {
            CanSupply = true, RequiresHarvest = true, WorkGiver = "", Bill = "",
            Cooking = new FoodCooking.Cook(workTicks, null), Delivery = new FoodDelivery.Carried(deliveryTicks),
        };

    /// <summary>A tend of this base quality whose batch has no pending infection check unless one is given.</summary>
    internal static SupportTreatment.Tend Tend(double quality = 0, InfectionCheck? infection = null, TendLearning? learning = null)
        => new(quality, infection ?? new InfectionCheck.None(), learning ?? ProductiveLearningTests.Tend());

    /// <summary>A surgery of the recipe at quality 1 that trains no skill unless work is given.</summary>
    internal static SupportTreatment.Surgery Surgery(PawnActivityRecipe recipe, ProductiveSkillWork? work = null, double quality = 1)
        => new(recipe, quality, work);

    /// <summary>A coupled capture at tick 0 with no activity of any family, no food and no route, over an empty thermal read.</summary>
    internal static CoupledForecastRead Empty => new()
    {
        Tick = 0, Practice = NoPractice, Rituals = NoRituals, Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt,
        Sowing = NoSowing, Support = NoSupport, Servicing = NoServicing, Thermal = CoupledThermalTests.NoThermal, Food = NoFood,
        Construction = ConstructionFixtures.NoConstruction(), Clothing = ClothingFixtures.NoClothing,
        FoodConversions = [], FoodUses = [], FoodSources = [], Routes = [],
    };
}
