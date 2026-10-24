using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whole coupled forecast reads for tests that set only the members they exercise.</summary>
internal static class CoupledFixtures
{
    internal static PracticeActivityRead NoPractice => new() { Workers = [] };
    internal static SowingActivityRead NoSowing => new() { Workers = [], Findings = [] };
    internal static SupportActivityRead NoSupport => new() { Demands = [], Workers = [], Deliveries = [], Findings = [] };
    internal static FoodActivityRead NoFood => new() { Options = [] };

    /// <summary>A support demand in a room that changes no infection or surgery chance, for a surgery with no outcome model.</summary>
    internal static SupportDemand Demand(string id, string kind, string target, Cell location, double poison, double bleed)
        => new(id, kind, target, location, poison, bleed)
        {
            InfectionChanceFactor = 1, SurgerySuccessFactor = 1, SurgeryOutcomes = [], SurgeryEnvironment = false,
            SurgeryQualityMin = 0, SurgeryQualityMax = float.MaxValue,
        };

    /// <summary>A food conversion its pawn can supply, cook (in workTicks, never poisoned, training no skill) and carry back in
    /// deliveryTicks, from a source that needs harvesting.</summary>
    internal static CoupledFoodConversion Conversion(string source, string pawn, string bench, string recipe, string ingredient,
        int ingredientCount, string product, int productCount, Cell sourceCell, Cell benchCell, Cell destination,
        double approachTicks, double supplyTicks, double workTicks, double deliveryTicks, int carryingCapacity, int availableInput)
        => new(source, pawn, bench, recipe, ingredient, ingredientCount, product, productCount, sourceCell, benchCell, destination,
            approachTicks, supplyTicks, carryingCapacity, availableInput)
        {
            CanSupply = true, RequiresHarvest = true, WorkGiver = "", Bill = "",
            Cooking = new FoodCooking.Cook(workTicks, 0, null), Delivery = new FoodDelivery.Carried(deliveryTicks),
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
        Tick = 0, Practice = NoPractice, Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt,
        Sowing = NoSowing, Support = NoSupport, Thermal = CoupledThermalTests.NoThermal, Food = NoFood,
        Construction = ConstructionFixtures.NoConstruction(), Clothing = ClothingFixtures.NoClothing,
        FoodConversions = [], FoodUses = [], FoodSources = [], Routes = [],
    };
}
