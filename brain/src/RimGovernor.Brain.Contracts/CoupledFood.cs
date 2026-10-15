using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

public sealed record CoupledFoodConversion(string Source, string Pawn, string Bench, string Recipe,
    string Ingredient, int IngredientCount, string Product, int ProductCount,
    Cell SourceCell, Cell BenchCell, Cell Destination,
    Fact<double> ApproachTicks, Fact<double> SupplyTicks, Fact<double> WorkTicks, Fact<double> DeliveryTicks,
    int CarryingCapacity, int AvailableInput)
{
    public bool CanSupply { get; init; } = true;
    public bool CanCook { get; init; } = true;
    public bool CanDeliver { get; init; } = true;
    public bool RequiresHarvest { get; init; } = true;
    public string WorkGiver { get; init; } = ""; public string Bill { get; init; } = "";
    public ProductiveSkillWork? SkillWork { get; init; }
}
public sealed record CoupledFoodUse(string Pawn, string Resource, Cell Location, Fact<bool> Eligible,
    Fact<double> Nutrition, Fact<double> ChewTicks, int MaxCount, Fact<double> FreshTicks);


public sealed record CoupledFoodSource(string Target, string Resource, Cell Location, int Count, Fact<double> FreshTicks);
