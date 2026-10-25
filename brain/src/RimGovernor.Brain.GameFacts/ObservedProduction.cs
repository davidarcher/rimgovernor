using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>One native bill giver, read once even when it supports both cooking and butchering.</summary>
public sealed record ProductionBenchFacts
{
    public required BillStackRow Bench { get; init; }
    public required CookingProduction Cooking { get; init; }
    public required ButcheringProduction Butchering { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CookingProduction.NotSupported), "not_supported")]
[JsonDerivedType(typeof(CookingProduction.Supported), "supported")]
public abstract record CookingProduction
{
    private protected CookingProduction() { }
    public sealed record NotSupported : CookingProduction;
    public sealed record Supported : CookingProduction
    {
        /// <summary>Not burning, powered when drawing power and fuelled when burning fuel.</summary>
        public required bool Usable { get; init; }
        [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeState>))]
        public required EquatableArray<RecipeState> Recipes { get; init; }
        [JsonConverter(typeof(EquatableArrayJsonConverter<FoodProduction>))]
        public required EquatableArray<FoodProduction> Production { get; init; }
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ButcheringProduction.NotSupported), "not_supported")]
[JsonDerivedType(typeof(ButcheringProduction.Supported), "supported")]
public abstract record ButcheringProduction
{
    private protected ButcheringProduction() { }
    public sealed record NotSupported : ButcheringProduction;
    public sealed record Supported : ButcheringProduction
    {
        /// <summary>The native bill-giver legality and usable-for-bills check.</summary>
        public required bool Usable { get; init; }
        [JsonConverter(typeof(EquatableArrayJsonConverter<RecipeState>))]
        public required EquatableArray<RecipeState> Recipes { get; init; }
        [JsonConverter(typeof(EquatableArrayJsonConverter<HumanButcherCandidate>))]
        public required EquatableArray<HumanButcherCandidate> HumanButchers { get; init; }
        [JsonConverter(typeof(EquatableArrayJsonConverter<HumanCorpseMeat>))]
        public required EquatableArray<HumanCorpseMeat> HumanCorpseMeat { get; init; }
    }
}