namespace RimGovernor.Brain.Policy;

/// <summary>What a Thing is (the mirror wire's ThingCategory). Spawned pawns are never listed.</summary>
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

    /// <summary>Every defined bit; a wire value with any other is refused.</summary>
    Known = (NaturalRock << 1) - 1,
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

/// <summary>A building, blueprint or frame's state. Needed is the materials still owed; Casket the defs a casket holds.</summary>
public sealed record BuildingState
{
    public uint HitPoints { get; init; }

    public bool Burning { get; init; }

    public bool Reserved { get; init; }

    public EquatableArray<Material> Needed { get; init; }

    public EquatableArray<string> Casket { get; init; }
}

/// <summary>
/// One thing on a cell: a header every category shares and the typed state of its category. Only the state
/// matching Category is meaningful (Building is non-null exactly for ThingCategory.Building).
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

public static partial class PolicyFunctions
{
    private const ThingMarks OccupyingFlags = ThingMarks.Edifice | ThingMarks.Blueprint | ThingMarks.Frame;

    internal static Thing? EdificeOf(this SiteCell c)
    {
        foreach (var t in c.Things)
        {
            if (t.Has(ThingMarks.Edifice))
            {
                return t;
            }
        }
        return null;
    }

    /// <summary>Reports a building, blueprint or frame on the cell: the native CellOccupied. Plants, items and filth never occupy a cell.</summary>
    public static bool Occupied(this SiteCell c)
    {
        foreach (var t in c.Things)
        {
            if ((t.Flags & OccupyingFlags) != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Reports the cell's edifice is natural rock: a planned ring on it keeps it as wall; inside the room plan dig mines it.</summary>
    public static bool NaturalRock(this SiteCell c) => c.EdificeOf() is { } t && t.Has(ThingMarks.NaturalRock);

    /// <summary>The cell with its occupying things (edifice, blueprints, frames) removed, for offering ground as unoccupied whatever the census says of it.</summary>
    public static SiteCell Cleared(this SiteCell c) =>
        c with { Things = EquatableArray.From(c.Things.Where(t => (t.Flags & OccupyingFlags) == 0).ToList()) };

    /// <summary>Names the definition of the player-owned edifice on the cell, empty for none: a ring of that wall kind stands on it.</summary>
    public static string PlayerEdifice(this SiteCell c) =>
        c.EdificeOf() is { } t && t.Faction == ThingFaction.Player ? t.Def : "";

    /// <summary>Reports an unowned edifice the player may deconstruct, outside any ancient danger.</summary>
    public static bool Ruin(this SiteCell c) =>
        c.EdificeOf() is { } t && t.Faction != ThingFaction.Player && t.Has(ThingMarks.Deconstructible) && !t.Has(ThingMarks.AncientDanger);

    /// <summary>Names the definition of a ruin the player may claim, empty for none: a ring of that wall kind claims it and keeps it as wall instead of clearing it.</summary>
    public static string ClaimableRuin(this SiteCell c) =>
        c.EdificeOf() is { } t && c.Ruin() && t.Has(ThingMarks.Claimable) ? t.Def : "";

    /// <summary>
    /// The hold on the unowned building covering the cell that a claim honours, empty for none:
    /// "ancient_danger", or "casket" for a casket still holding contents. A ring claims a ruin only where the
    /// claim would act on it.
    /// </summary>
    public static string RuinHold(this SiteCell c)
    {
        var hold = "";
        foreach (var t in c.Things)
        {
            if (t.Category != ThingCategory.Building || t.Faction == ThingFaction.Player || t.Building is null
                || t.Has(ThingMarks.Blueprint) || t.Has(ThingMarks.Frame))
            {
                continue;
            }
            if (t.Has(ThingMarks.AncientDanger))
            {
                return "ancient_danger";
            }
            if (t.Building.Casket.Count > 0)
            {
                hold = "casket";
            }
        }
        return hold;
    }
}
