using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using Identity = RimGovernor.Contracts.Wire.Common.Identity;
using Cell = RimGovernor.Brain.Domain.Cell;
using System.Text.Json.Serialization;

namespace RimGovernor.Contracts.Wire.Observations;

public sealed record FoodActivityOption(string Pawn, string Target, string Resource, Cell Origin, Cell Destination,
    string WorkGiver, int Count, double WorkTicks)
{
    public Fact<double> SourceCount { get; init; }
    public FoodCookingEstimate? Cooking { get; init; }
    public Fact<double> ApproachTicks { get; init; }
    public Fact<double> DeliveryTicks { get; init; }
    public Fact<bool> HarvestAllowed { get; init; }
}
/// <summary>One eligible recipe/source alternative, not a selected order. Route includes final product delivery.</summary>
public sealed record FoodCookingEstimate(ActivityRecipeContinuation Continuation, int IngredientCount)
{
}
public sealed record FoodActivityRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodActivityOption>))]
    public EquatableArray<FoodActivityOption> Options { get; init; }
}
