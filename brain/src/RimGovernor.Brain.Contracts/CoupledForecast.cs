using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>One game-thread capture for the coupled consequence model. Derived graph/intent never lives here.</summary>
public sealed record CoupledForecastRead
{
    public required long Tick { get; init; }
    public required PracticeActivityRead Practice { get; init; }
    public required ResearchActivityRead Research { get; init; }
    public required HuntActivityRead Hunt { get; init; }
    public required SowingActivityRead Sowing { get; init; }
    public required SupportActivityRead Support { get; init; }
    public required CoupledThermalRead Thermal { get; init; }
    public required FoodActivityRead Food { get; init; }
    public required ConstructionActivityRead Construction { get; init; }
    public required RecipeActivityRead Clothing { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledFoodConversion>))]
    public required EquatableArray<CoupledFoodConversion> FoodConversions { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledFoodUse>))]
    public required EquatableArray<CoupledFoodUse> FoodUses { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledFoodSource>))]
    public required EquatableArray<CoupledFoodSource> FoodSources { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledRoute>))]
    public required EquatableArray<CoupledRoute> Routes { get; init; }
}
/// <summary>One pawn's walk between two cells, priced by native pathing for that pawn (<see cref="RouteTicks"/>).
/// <see cref="FromStart"/> marks the walk from where the pawn stands now.</summary>
public sealed record CoupledRoute(string Pawn, Cell From, Cell To, RouteTicks Ticks) { public required bool FromStart { get; init; } }

/// <summary>A walk's price: the native path's ticks for the pawn (Reachable), or Unreachable when native finds no path.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Reachable), "reachable")]
[JsonDerivedType(typeof(Unreachable), "unreachable")]
public abstract record RouteTicks
{
    private protected RouteTicks() { }

    /// <summary>The one Unreachable value.</summary>
    public static RouteTicks None { get; } = new Unreachable();

    public sealed record Reachable(double Ticks) : RouteTicks;

    public sealed record Unreachable : RouteTicks;
}
