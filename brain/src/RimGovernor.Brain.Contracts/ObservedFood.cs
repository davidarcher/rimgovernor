using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
namespace RimGovernor.Contracts.Wire.Observations;
/// <summary>
/// Every slot is required; alternatives within a slot are interchangeable. A fine
/// meal needs (meat OR animalProduct) AND vegetable, not all three.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<FoodIngredientClass>))]
public enum FoodIngredientClass
{
    [WireName("")]
    None = 0,
    Meat,
    Vegetable,

    [WireName("animalProduct")]
    AnimalProduct,
    Any,
}

public sealed record FoodConsumer
{
    public required PawnId Id { get; init; }

    public required double NutritionPerDay { get; init; }

    /// <summary>Includes animals whose native diet accepts it.</summary>
    public required bool HumanMeatAcceptable { get; init; }
}

/// <summary>What a food stock is: an item stack, or a corpse whose food is its meat yield.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FoodForm.Item), "item")]
[JsonDerivedType(typeof(FoodForm.Corpse), "corpse")]
public abstract record FoodForm
{
    private protected FoodForm() { }

    public sealed record Item : FoodForm;

    /// <summary>A corpse: MeatAmount is its native meat yield before butcher efficiency, BodySize its inner pawn's body size.</summary>
    public sealed record Corpse(double MeatAmount, double BodySize) : FoodForm;
}

/// <summary>Where a food stock is: standing on the map (roofed or not, in Room, "" outside any room) or carried, unspawned.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FoodPlace.Stands), "stands")]
[JsonDerivedType(typeof(FoodPlace.Carried), "carried")]
public abstract record FoodPlace
{
    private protected FoodPlace() { }

    public sealed record Stands(bool Roofed, string Room) : FoodPlace;

    public sealed record Carried : FoodPlace;
}

/// <summary>How a food stock spoils: a thing rots exactly while it has a rot deadline.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FoodRot.Keeps), "keeps")]
[JsonDerivedType(typeof(FoodRot.Rots), "rots")]
public abstract record FoodRot
{
    private protected FoodRot() { }

    /// <summary>Never rots: a survival meal, pemmican, or a stock whose def has no rot clock.</summary>
    public sealed record Keeps : FoodRot;

    /// <summary>Rots in Ticks at its current temperature; zero or less has already rotted.</summary>
    public sealed record Rots(long Ticks) : FoodRot;
}

public sealed record FoodStock
{
    public required string Id { get; init; }

    /// <summary>Includes meals containing humanlike meat, not just raw meat.</summary>
    public required bool IsHumanMeat { get; init; }

    public required bool RawMeat { get; init; }

    public required bool IsHumanlike { get; init; }

    public required bool Vegetable { get; init; }

    public required FoodIngredientClass RawClass { get; init; }


    /// <summary>The pawn whose inventory holds the stock; empty for a shared stock.</summary>
    public required PawnId Holder { get; init; }

    /// <summary>The nutrition the stock gives its eaters (a corpse's: its meat yield's).</summary>
    public required double Nutrition { get; init; }

    public required IReadOnlyList<PawnId> Eaters { get; init; }

    /// <summary>Whether the stock keeps or rots, and in how many ticks at its current temperature.</summary>
    public required FoodRot Rot { get; init; }

    /// <summary>DefName and Count select a specific feed resource (the animal feed runway's item choice); ForecastFood never reads them.</summary>
    public required string DefName { get; init; }

    public required long Count { get; init; }

    /// <summary>The catalog mass used only for caravan packing.</summary>
    public required double UnitMass { get; init; }

    /// <summary>Where the stock is at observation time; ForecastFood ignores it, storage upkeep and refrigeration policy read it.</summary>
    public required FoodPlace Place { get; init; }

    /// <summary>The ambient temperature where the stock is.</summary>
    public required double TemperatureC { get; init; }

    /// <summary>An item stack or a corpse. Forbidden corpses remain observable reserves but do not extend the food runway.</summary>
    public required FoodForm Form { get; init; }


    public required bool Forbidden { get; init; }
}

public sealed record CorpseHandling
{
    public required string Id { get; init; }

    public required Cell Cell { get; init; }

    public required PawnId Hauler { get; init; }

    public required bool FrozenDestination { get; init; }
}

/// <summary>
/// CookDemandNutrition is one batch per active cook bill, measured using its native ingredient filters.
/// Released corpses fund the same window so subsequent reviews do not release the entire larder before butchering.
/// </summary>
public sealed record FoodLarderFacts
{
    public required double RawMeatNutrition { get; init; }

    public required double CookDemandNutrition { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CorpseHandling>))]
    public required EquatableArray<CorpseHandling> Corpses { get; init; }
}

