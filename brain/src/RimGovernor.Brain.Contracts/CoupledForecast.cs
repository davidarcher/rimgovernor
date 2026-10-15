using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>One game-thread capture for the coupled consequence model. Derived graph/intent never lives here.</summary>
public sealed record CoupledForecastRead
{
    public long Tick { get; init; }
    public SupportActivityRead Support { get; init; } = new();
    public CoupledThermalRead? Thermal { get; init; }
    public FoodActivityRead Food { get; init; } = new();
    public ConstructionActivityRead Construction { get; init; } = new();
    public RecipeActivityRead Clothing { get; init; } = new();
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledFoodConversion>))]
    public EquatableArray<CoupledFoodConversion> FoodConversions { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledFoodUse>))]
    public EquatableArray<CoupledFoodUse> FoodUses { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledFoodSource>))]
    public EquatableArray<CoupledFoodSource> FoodSources { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledRoute>))]
    public EquatableArray<CoupledRoute> Routes { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Findings { get; init; }
    public bool Truncated { get; init; }
}
public sealed record CoupledRoute(string Pawn, Cell From, Cell To, Fact<double> Ticks) { public bool FromStart { get; init; } }
