using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;

// The per-cell thing records the cell grid (Mirror.CellGrid) carries: declared here, in the contracts assembly, so the
// native read builds the very records the policy reads; they keep the policy namespace their readers use.
namespace RimGovernor.Brain.Policy;

/// <summary>What a Thing is. Spawned pawns are never listed.</summary>
public enum ThingCategory : byte
{
    Item = 1,
    Building,
    Plant,
    Filth,
    Corpse,
    Other,
}

/// <summary>Whose a Thing is.</summary>
public enum ThingFaction : byte
{
    None,
    Player,
    Neutral,
    Hostile,
}

/// <summary>A Thing's predicate bitset.</summary>
[Flags]
public enum ThingMarks : uint
{
    None = 0,
    Edifice = 1 << 0,
    Blueprint = 1 << 1,
    Frame = 1 << 2,
    Impassable = 1 << 3,
    HoldsRoof = 1 << 4,
    Deconstructible = 1 << 5,
    Minifiable = 1 << 6,
    Claimable = 1 << 7,
    Designated = 1 << 8,
    Forbidden = 1 << 9,
    Haulable = 1 << 10,
    AncientDanger = 1 << 11,
    NaturalRock = 1 << 12,
}

/// <summary>What a corpse was.</summary>
public enum CorpseClass : byte
{
    Other,
    Humanlike,
    Animal,
}

/// <summary>A count of one def: a blueprint's or frame's owed materials.</summary>
public sealed record Material(string Def, uint Count);

/// <summary>A plant's state.</summary>
public sealed record PlantState
{
    public float Growth { get; init; }

    public bool Blighted { get; init; }
}

/// <summary>A corpse's state.</summary>
public sealed record CorpseState
{
    public CorpseClass Class { get; init; }

    public float Rot { get; init; }
}

/// <summary>
/// A building, blueprint or frame's state. Stuff is the stuff def it is made of (a blueprint or frame: the stuff it
/// builds with), empty for a stuffless def; Needed is the materials still owed; Casket the defs a casket holds.
/// </summary>
public sealed record BuildingState
{
    public uint HitPoints { get; init; }

    public string Stuff { get; init; } = "";

    public bool Burning { get; init; }

    public bool Reserved { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Material>))]
    public EquatableArray<Material> Needed { get; init; }

    public EquatableArray<string> Casket { get; init; }
}

/// <summary>
/// One thing on a cell: a header every category shares and the typed state of its category. Only the state
/// matching Category is meaningful (Building is non-null exactly for ThingCategory.Building). A blueprint or frame
/// lists the def it builds; a thing spanning cells is listed on each, as the same record. Fractions are held to
/// 1/100, so an unchanged thing keeps its record across captures.
/// </summary>
public sealed record Thing
{
    public string Def { get; init; } = "";

    public ThingCategory Category { get; init; }

    public ThingFaction Faction { get; init; }

    public ThingMarks Flags { get; init; }

    public ulong Id { get; init; }

    public uint Count { get; init; }

    public PlantState Plant { get; init; } = new();

    public CorpseState Corpse { get; init; } = new();

    public uint FilthThickness { get; init; }

    public float ItemDeterioration { get; init; }

    public BuildingState? Building { get; init; }

    /// <summary>
    /// The native entity id of a listed thing: vanilla's Thing.GetUniqueLoadID, "Thing_" + defName +
    /// thingIDNumber. The one builder of the form; census rows and the mirror's things resolve through
    /// RefIndex.Thing by it.
    /// </summary>
    public string LoadId() => "Thing_" + Def + Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reports whether every flag in f is set.</summary>
    public bool Has(ThingMarks f) => (Flags & f) == f;
}
