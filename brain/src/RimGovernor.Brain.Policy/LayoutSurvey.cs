namespace RimGovernor.Brain.Policy;

/// <summary>The heaviest structure a cell's terrain holds.</summary>
public enum Footing
{
    /// <summary>Takes any building, a stone wall included.</summary>
    Firm,

    /// <summary>Takes light structures only, a wooden wall among them: marshy soil, soft sand, a plain bridge.</summary>
    Light,

    /// <summary>Takes nothing without a bridge: marsh, mud, water.</summary>
    None,
}

/// <summary>One map cell of the settle-time survey. Absent cells are unknown and score as unbuildable.</summary>
public sealed record SurveyCell
{
    public Cell Cell { get; init; }

    /// <summary>Ground a pawn crosses (deep water is not).</summary>
    public bool Walkable { get; init; }

    /// <summary>Natural rock a module is mined out of.</summary>
    public bool Rock { get; init; }

    /// <summary>A player edifice on the cell (a wall, a door): the perimeter plans as though the colony's own buildings were not there.</summary>
    public bool Built { get; init; }

    /// <summary>What the natural ground holds, under any floor or bridge; walkable ground short of firm is soft and carries no module.</summary>
    public Footing Footing { get; init; }

    /// <summary>Takes a bridge.</summary>
    public bool Bridgeable { get; init; }

    /// <summary>Turns firm under a moisture pump (not moving or deep water).</summary>
    public bool Dries { get; init; }

    /// <summary>Terrain that hurts or contaminates by its own def flags (lava): nothing is built, farmed or walled on it.</summary>
    public bool Hazard { get; init; }

    /// <summary>Overhead mountain: no drop pods, no roof collapse from mining, cold storage.</summary>
    public bool ThickRoof { get; init; }

    /// <summary>The soil's growing multiplier (0 for rock and floors).</summary>
    public double Fertility { get; init; }

    /// <summary>The def of the terrain on the cell (a floor included); empty where unread.</summary>
    public string Terrain { get; init; } = "";

    /// <summary>Rock holding a mineable resource.</summary>
    public bool Ore { get; init; }

    /// <summary>A cell under a tree.</summary>
    public bool Tree { get; init; }

    /// <summary>A structure the colony neither owns nor clears as a ruin: no core room or hallway is sited over it.</summary>
    public bool Prop { get; init; }

    /// <summary>A clearable ruin: not walkable yet, but home clearance deconstructs it, so core rooms may be sited over it.</summary>
    public bool Ruin { get; init; }

    /// <summary>Walkable ground no module or stone wall stands on.</summary>
    public bool Soft() => (Walkable || Built) && !Rock && Footing != Footing.Firm;
}

/// <summary>The whole map, scored once at settle time.</summary>
public sealed record MapSurvey
{
    public Bounds Bounds { get; init; }
    public IReadOnlyList<SurveyCell> Cells { get; init; } = [];

    /// <summary>The map's climate (ColdMapCurve), set by the caller; a fresh plan latches it.</summary>
    public bool Cold { get; init; }

    /// <summary>The map's other climate end (HotMapCurve), latched the same way.</summary>
    public bool Hot { get; init; }
}

public static partial class PolicyFunctions
{
    public static PlannedRole PlannedHospital => PlannedRole.Hospital;
    public static PlannedRole PlannedPrison => PlannedRole.Prison;
    public static PlannedRole PlannedKitchen => PlannedRole.Kitchen;
    public static PlannedRole PlannedFreezer => PlannedRole.Freezer;

    /// <summary>The butcher room (butchering is filthy, so it is kept out of the kitchen): beside the freezer behind a Link door when a side is free, else on the hallway.</summary>
    public static PlannedRole PlannedButchery => PlannedRole.Butchery;

    public static PlannedRole PlannedStorage => PlannedRole.Storage;
    public static PlannedRole PlannedWorkshop => PlannedRole.Workshop;

    /// <summary>Sound ground held for growth.</summary>
    public static PlannedRole PlannedReserve => PlannedRole.Reserve;

    /// <summary>How far from the map edge nothing is built: raiders and the map edge's fog make the band useless.</summary>
    public const int LayoutEdgeMargin = 10;

    public static PlannedRole PlannedBedroom => PlannedRole.Bedroom;

    /// <summary>The temporary starter room, sited apart from the core so its ground frees cleanly once it is demolished.</summary>
    public static PlannedRole PlannedShelter => PlannedRole.Shelter;

    public static PlannedRole PlannedDining => PlannedRole.Dining;
    public static PlannedRole PlannedRec => PlannedRole.Rec;
    public static PlannedRole PlannedLab => PlannedRole.Lab;

    /// <summary>The sarcophagus room, planned from the start in the outskirts cluster and shelled only once a colonist lies dead.</summary>
    public static PlannedRole PlannedTomb => PlannedRole.Tomb;

    /// <summary>The room for human corpses, planned from the start in the outskirts cluster and shelled only once one lies waiting.</summary>
    public static PlannedRole PlannedMorgue => PlannedRole.Morgue;

    /// <summary>The unfenced, unfloored Outdoor room of graves, planned from the start in the outskirts cluster.</summary>
    public static PlannedRole PlannedGraveyard => PlannedRole.Graveyard;
}
