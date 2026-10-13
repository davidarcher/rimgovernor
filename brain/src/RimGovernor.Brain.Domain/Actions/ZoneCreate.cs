using System.Text;

namespace RimGovernor.Brain.Domain;

public enum ZoneKind
{
    Growing,
    Stockpile,
    Fishing,
}

/// <summary>
/// Spans vanilla's five storage priorities: a Normal general store holds the colony's stock, Important/Critical
/// working stockpiles at benches and the kitchen pull from it, and Low is the dump.
/// </summary>
public enum StockpilePriority
{
    Critical,
    Important,
    Preferred,

    /// <summary>The general store's: any working stockpile above it pulls its allowed things out of the store.</summary>
    Normal,

    /// <summary>
    /// Vanilla's dumping-stockpile priority: the chunk dump a clearance method places so any other store the
    /// player or a later method admits for the same things wins the haul.
    /// </summary>
    Low,
}

public static partial class DomainConstants
{
    /// <summary>The mod's burnable special filter (native BurnableRule; saved in games, never rename).</summary>
    public const string BurnableFilterDef = "RimGovernorBurnable";

    /// <summary>The mod's not-burnable special filter (native BurnableRule; saved in games, never rename).</summary>
    public const string NotBurnableFilterDef = "RimGovernorNotBurnable";

    /// <summary>Ordinary native fishing's conservative population floor.</summary>
    public const double FishingPopulationFloor = 0.6;

    /// <summary>
    /// The sowing lattice of a blockAdjacentSow plant (every tree): the native sower offers only cells whose
    /// offsets from the growing zone's minimum corner are both multiples of the pitch, so a zone holds one tree
    /// per TreeLatticePitch^2 cells and fills completely. Mirrors TreeLatticeSowing.Pitch in the native mod.
    /// </summary>
    public const int TreeLatticePitch = 2;

    /// <summary>The zone area one lattice tree takes.</summary>
    public const int TreeCellsPerTree = TreeLatticePitch * TreeLatticePitch;
}

/// <summary>Go <c>GroundRect.Cells</c> (the type itself is declared with deconstruction).</summary>
public static class ZoneGroundRectExtensions
{
    public static EquatableArray<Cell> Cells(this GroundRect r)
    {
        var cells = new List<Cell>();
        for (var x = 0; x < r.Width; x++)
        {
            for (var z = 0; z < r.Height; z++)
            {
                cells.Add(new Cell(r.Origin.X + x, r.Origin.Z + z));
            }
        }
        return EquatableArray.From(cells);
    }
}

/// <summary>
/// An exact growing/fishing footprint or a stockpile rectangle drag. RimWorld resolves the stockpile's usable
/// cells and connected zones. Role is the stable key a stockpile planner claims the zone by (e.g. "general",
/// "ingredients:&lt;benchID&gt;", "dump:worn"); it travels with the zone_create receipt into store.OwnedZone.
/// Empty is a legacy role-less claim. Instances are always canonical: the constructors validate, so Go's
/// <c>ReconstructZone</c> re-check has no counterpart.
/// </summary>
public sealed record ZoneCreate : IActionPayload
{
    // Requested ground of a cell-listed zone; empty for a rectangle stockpile (whose ground is Rectangle).
    private readonly EquatableArray<Cell> _cells;

    private ZoneCreate(ZoneKind kind, string crop, StockpilePriority? priority, EquatableArray<Cell> cells, StockpileFilter? filter, GroundRect? rectangle)
    {
        Kind = kind;
        Crop = crop;
        Priority = priority;
        _cells = cells;
        Filter = filter;
        Rectangle = rectangle;
    }

    /// <summary>Go <c>NewZoneCreate</c>: a growing zone over an exact connected footprint.</summary>
    public ZoneCreate(ZoneKind kind, string crop, IEnumerable<Cell> cells)
    {
        DomainException.Require(kind == ZoneKind.Growing && Identity.IsValidText(crop), "invalid zone configuration");
        _cells = CanonicalConnectedCells(cells);
        Kind = kind;
        Crop = crop;
    }

    public ZoneKind Kind { get; }

    public string Crop { get; } = "";

    /// <summary>A stockpile's priority; null for other zone kinds.</summary>
    public StockpilePriority? Priority { get; }

    /// <summary>The requested ground; stockpile receipts hold actual created cells.</summary>
    public EquatableArray<Cell> Cells => Kind == ZoneKind.Stockpile && _cells.Count == 0 && Rectangle is { } r ? r.Cells() : _cells;

    /// <summary>A stockpile rectangle drag's rectangle; null for other zones and for a cell-listed stockpile.</summary>
    public GroundRect? Rectangle { get; }

    /// <summary>A stockpile's storage filter; null for other zone kinds.</summary>
    public StockpileFilter? Filter { get; }

    /// <summary>A stockpile's planner role key; empty when untagged.</summary>
    public string Role { get; private init; } = "";

    /// <summary>Go <c>ExtendZoneID()</c>: the existing fishing zone this request extends; empty otherwise.</summary>
    public string ExtendId { get; private init; } = "";

    /// <summary>
    /// Go <c>canonicalConnectedCells</c>: sorts by (X, Z), rejects empty, negative or duplicate cells and
    /// disconnected footprints.
    /// </summary>
    internal static EquatableArray<Cell> CanonicalConnectedCells(IEnumerable<Cell> cells)
    {
        var rows = cells.OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
        DomainException.Require(rows.Count != 0, "invalid zone configuration");
        var seen = new HashSet<Cell>();
        foreach (var cell in rows)
        {
            DomainException.Require(cell.X >= 0 && cell.Z >= 0 && seen.Add(cell), "invalid zone cell");
        }
        var reached = new HashSet<Cell> { rows[0] };
        var queue = new Queue<Cell>();
        queue.Enqueue(rows[0]);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var next in new[] { new Cell(cell.X + 1, cell.Z), new Cell(cell.X - 1, cell.Z), new Cell(cell.X, cell.Z + 1), new Cell(cell.X, cell.Z - 1) })
            {
                if (seen.Contains(next) && reached.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }
        DomainException.Require(reached.Count == rows.Count, "zone footprint disconnected");
        return EquatableArray.From(rows);
    }

    /// <summary>
    /// Ordinary native fishing with a conservative population floor. Its footprint provides access; it is not a
    /// daily catch quota.
    /// </summary>
    public static ZoneCreate NewFishingZone(IEnumerable<Cell> cells) =>
        new(ZoneKind.Fishing, "", null, CanonicalConnectedCells(cells), null, null);

    /// <summary>
    /// Names the exact existing zone and the complete final footprint. Native admission requires a strict
    /// superset in the same body.
    /// </summary>
    public static ZoneCreate NewFishingZoneExtension(string zoneId, IEnumerable<Cell> cells)
    {
        DomainException.Require(Identity.IsValidText(zoneId), "invalid fishing zone identity");
        return NewFishingZone(cells) with { ExtendId = zoneId };
    }

    /// <summary>Go <c>validStockpilePriority</c>.</summary>
    internal static bool ValidStockpilePriority(StockpilePriority p) => Wire.IsDefined(p);

    /// <summary>A rectangle drag with a canonical filter.</summary>
    public static ZoneCreate NewFilteredStockpileZone(StockpileFilter filter, StockpilePriority priority, GroundRect rectangle)
    {
        DomainException.Require(ValidStockpilePriority(priority), "invalid stockpile zone configuration");
        DomainException.Require(
            rectangle.Origin.X >= 0
                && rectangle.Origin.Z >= 0
                && rectangle.Width > 0
                && rectangle.Height > 0
                && (long)rectangle.Origin.X + rectangle.Width <= int.MaxValue
                && (long)rectangle.Origin.Z + rectangle.Height <= int.MaxValue,
            "invalid stockpile rectangle"
        );
        return new ZoneCreate(ZoneKind.Stockpile, "", priority, default, filter, rectangle);
    }

    /// <summary>A stockpile over an explicit connected cell list (a room region, not a rectangle), modelled on <c>NewZoneCreate</c>.</summary>
    public static ZoneCreate NewFilteredStockpileCellsZone(StockpileFilter filter, StockpilePriority priority, IEnumerable<Cell> cells)
    {
        DomainException.Require(ValidStockpilePriority(priority), "invalid stockpile zone configuration");
        return new ZoneCreate(ZoneKind.Stockpile, "", priority, CanonicalConnectedCells(cells), filter, null);
    }

    /// <summary>Tags a stockpile with the role key its planner claims it by.</summary>
    public ZoneCreate WithRole(string role)
    {
        DomainException.Require(Kind == ZoneKind.Stockpile && Identity.IsValidText(role) && Encoding.UTF8.GetByteCount(role) <= 128, "invalid stockpile role");
        return this with { Role = role };
    }

    public string Label =>
        Kind switch
        {
            ZoneKind.Fishing => "Fishing",
            ZoneKind.Stockpile => StockpileLabel(Filter ?? throw new InvalidOperationException("stockpile zone without a filter"), Priority),
            _ => "Crops",
        };

    // Names a stockpile by its filter; the named filters keep the labels their retired presets sent native.
    private static string StockpileLabel(StockpileFilter f, StockpilePriority? priority)
    {
        var allowOnly = f.TryGetAllowOnlyDefinitions(out var definitions);
        if (f == StockpileFilter.FoodFilter())
        {
            return "Food storage";
        }
        if (f == StockpileFilter.CorpseLarderFilter())
        {
            return "Corpse larder";
        }
        if (f == StockpileFilter.GeneralFilter())
        {
            return "Warehouse";
        }
        if (f == StockpileFilter.OpeningStoreFilter())
        {
            return "General store";
        }
        if (f == StockpileFilter.RawFoodFilter())
        {
            return "Raw food";
        }
        if (f == StockpileFilter.RawMeatFilter())
        {
            return "Raw meat";
        }
        if (f == StockpileFilter.RawVegFilter())
        {
            return "Raw vegetables";
        }
        if (f == StockpileFilter.PerishablesFilter())
        {
            return "Perishables";
        }
        if (f == StockpileFilter.TombCorpsesFilter())
        {
            return "Tomb corpses";
        }
        if (f == StockpileFilter.MorgueCorpsesFilter())
        {
            return "Morgue";
        }
        if (StockpileFilter.IsArmoryFilter(f))
        {
            return "Armory";
        }
        if (StockpileFilter.IsWardrobeFilter(f))
        {
            return "Wardrobe";
        }
        if (allowOnly && priority == StockpilePriority.Low)
        {
            return "Dumping";
        }
        return allowOnly ? DefinitionList(definitions) : "Stockpile";
    }

    // Names up to three definitions and counts the rest, so a label stays short enough for the zone tab.
    private static string DefinitionList(EquatableArray<string> definitions)
    {
        const int Shown = 3;
        if (definitions.Count <= Shown)
        {
            return string.Join(", ", definitions);
        }
        return $"{string.Join(", ", definitions.Take(Shown))} +{definitions.Count - Shown} more";
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewZoneCreateAction(ActionId id, ZoneCreate z) => Create(id, ActionKind.ZoneCreate, z);

    /// <summary>Go <c>Action.ZoneCreate() (ZoneCreate, bool)</c>: the payload when this is a zone_create action, otherwise null.</summary>
    public ZoneCreate? ZoneCreate => PayloadOf<ZoneCreate>(ActionKind.ZoneCreate);
}
