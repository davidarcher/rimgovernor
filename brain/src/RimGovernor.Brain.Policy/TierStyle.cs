namespace RimGovernor.Brain.Policy;

// Tier-styled buildings: each tech tier looks different because the floor under each room role and the lighting
// fixture advance with TechTier, all through existing Core defs and stuff choices. Every rule here is a pure
// f(tier, role, stock) -> def/stuff with one stock fallback: when the tier's rung names a material the colony
// does not hold, the rung one tier down is tried, and nothing further. A rule never proposes a stuff the colony
// has none of, so a Camp colony never receives a stone, powered or floored proposal.
//
// The shell planners take wall and door stuff from the def stats (ColonyProjection.BulkBuildStuff, not a tier
// rule); the flooring planner prefers FloorDef's floor for a deficient room's role and the lighting planner
// PlannedLighting's fixture. The shape-family rules (double-module hall, paired wings, courtyard) are
// shape_family.go's.

/// <summary>The resource census the rules read: available count per resource. Zero or absent means the colony holds none.</summary>
public sealed class TierStyleStock(IReadOnlyDictionary<Resource, long> counts)
{
    public static TierStyleStock Empty { get; } = new(new Dictionary<Resource, long>());

    public long this[Resource resource] => counts.TryGetValue(resource, out var n) ? n : 0;

    internal bool Has(Resource resource, long count) => this[resource] >= count;

    /// <summary>
    /// The biome's stone as the colony has quarried it: the stone block definition the census holds most of
    /// (ties break on name). A colony without blocks has no quarried stone.
    /// </summary>
    public (Resource Stone, bool Ok) QuarriedStone()
    {
        var blocks = counts.Where(kv => kv.Value > 0 && PolicyFunctions.StoneSuffix(kv.Key).Length > 0).Select(kv => kv.Key).ToList();
        if (blocks.Count == 0)
        {
            return (default, false);
        }
        return (blocks.OrderByDescending(b => this[b]).ThenBy(b => b).First(), true);
    }
}

/// <summary>
/// What FloorDef reads beyond tier, role and stock: the finished research that gates carpet (CarpetMaking) and
/// sterile tile (SterileMaterials), and the cost list per cell of each floor those research projects unlock
/// (the terrain defs' cost lists, by name); a floor with no known cost list cannot be afforded.
/// </summary>
public sealed record FloorStyleFacts
{
    public bool CarpetMaking { get; init; }

    public bool SterileMaterials { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<Amount>> Costs { get; init; } = new Dictionary<string, IReadOnlyList<Amount>>();

    /// <summary>Whether stock covers one cell of the floor def.</summary>
    internal bool Affordable(string def, TierStyleStock stock)
    {
        if (!Costs.TryGetValue(def, out var costs))
        {
            return false;
        }
        foreach (var c in costs)
        {
            if (!stock.Has(c.Resource, c.Count))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>How many fixtures a module takes.</summary>
public enum LightingScope
{
    /// <summary>One fixture per module.</summary>
    PerModule,

    /// <summary>One fixture per 5x5 sub-cell.</summary>
    PerSubCell,
}

/// <summary>A lighting proposal for one module.</summary>
public sealed record LightingStyle(string Definition, LightingScope Scope);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The wall placements a shell and its rooms run to: a stuff builds the walls only when the stock, with the
    /// wood standing as trees, covers this many (a few hundred cells at 5 units each).
    /// </summary>
    public const long ShellWallBudget = 200;

    /// <summary>
    /// The wall stuff a shell never spends: steel is what the heater, generators and hydroponics are built
    /// from, and wall material does not change heat equalization, so walls take the cheapest other stuff. Steel
    /// walls are allowed only where the map resupplies steel (a steel acquisition source).
    /// </summary>
    public static string GuardedWallStuff => SteelResource.Value;

    /// <summary>The generated Core carpet definition the soft rooms take; one colour keeps the proposal deterministic.</summary>
    public const string Carpet = "CarpetRed";

    /// <summary>The hospital and kitchen sterile floor.</summary>
    public const string SterileTile = "SterileTile";

    /// <summary>The barn floor: cheap hay matting that accepts little filth.</summary>
    public const string StrawMatting = "StrawMatting";

    /// <summary>
    /// The stone name a block definition carries, the suffix the generated floor definitions share:
    /// BlocksGranite -> Granite -> TileGranite, FlagstoneGranite.
    /// </summary>
    internal static string StoneSuffix(Resource blocks)
    {
        const string prefix = "Blocks";
        var name = blocks.Value;
        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return "";
        }
        return name[prefix.Length..];
    }

    /// <summary>Tries rung(tier) and, when that rung names nothing the colony holds, rung(tier-1) once; Camp has no rung below it.</summary>
    private static (T Value, bool Ok) OneRungDown<T>(TechTier tier, Func<TechTier, (T Value, bool Ok)> rung)
    {
        var first = rung(tier);
        if (first.Ok)
        {
            return first;
        }
        if (tier > TechTier.Camp)
        {
            return rung(tier - 1);
        }
        return (default!, false);
    }

    private enum FloorClass
    {
        None,
        Aisle,
        Living,
        Soft,
        Sterile,
        Barn,
    }

    /// <summary>
    /// Maps a room role to its floor class: aisles (RoomRole.None, the cells between modules) and storage take
    /// flagstone; bedrooms, dining, workshops and the other lived-in rooms take stone tile; bedrooms and
    /// recreation may take carpet; the hospital and kitchen take sterile tile; barns take straw matting. Roles
    /// not listed (tombs, prison cells) receive no floor.
    /// </summary>
    private static FloorClass FloorClassOf(RoomRole role) =>
        role switch
        {
            RoomRole.None or RoomRole.Storeroom => FloorClass.Aisle,
            RoomRole.Bedroom or RoomRole.RecRoom => FloorClass.Soft,
            RoomRole.DiningRoom or RoomRole.Workshop or RoomRole.Laboratory or RoomRole.Barracks or RoomRole.Room => FloorClass.Living,
            RoomRole.Hospital or RoomRole.Kitchen => FloorClass.Sterile,
            RoomRole.Barn => FloorClass.Barn,
            _ => FloorClass.None,
        };

    /// <summary>
    /// The floor rule: nothing at Camp; from Masonry flagstone of the quarried stone on aisles and in storage
    /// and stone tile in the lived-in rooms; sterile tile in the hospital and kitchen at Industrial once
    /// SterileMaterials is finished and steel and silver stock allow; carpet in bedrooms and recreation once
    /// CarpetMaking is finished and cloth stock allows. Each upgrade falls back one rung to the stone floor, and
    /// the stone floor to nothing.
    /// </summary>
    public static (string Def, bool Ok) FloorDef(TechTier tier, RoomRole role, TierStyleStock stock, FloorStyleFacts facts)
    {
        var floorClass = FloorClassOf(role);
        if (floorClass == FloorClass.None)
        {
            return ("", false);
        }
        (string, bool) StoneFloor()
        {
            var (stone, ok) = stock.QuarriedStone();
            if (!ok)
            {
                return ("", false);
            }
            return ((floorClass == FloorClass.Aisle ? "Flagstone" : "Tile") + StoneSuffix(stone), true);
        }
        return OneRungDown(
            tier,
            t =>
            {
                if (t < TechTier.Masonry)
                {
                    return ("", false);
                }
                if (floorClass == FloorClass.Barn)
                {
                    return facts.Affordable(StrawMatting, stock) ? (StrawMatting, true) : ("", false);
                }
                if (floorClass == FloorClass.Sterile && t >= TechTier.Industrial)
                {
                    if (facts.SterileMaterials && facts.Affordable(SterileTile, stock))
                    {
                        return (SterileTile, true);
                    }
                    // Sterile tile unmet: the rung below Industrial is the stone floor, and Spacer shares
                    // Industrial's rung.
                    return t > TechTier.Industrial ? StoneFloor() : ("", false);
                }
                if (floorClass == FloorClass.Soft && facts.CarpetMaking && facts.Affordable(Carpet, stock))
                {
                    return (Carpet, true);
                }
                return StoneFloor();
            }
        );
    }

    /// <summary>
    /// The lighting rule: nothing at Camp; a torch per module at Masonry; at Powered with a powered source, a
    /// standing lamp per 5x5 sub-cell, or one sun lamp per farm module. A powered rung without power or the
    /// steel for a lamp falls back to the torch, and a torch without wood to nothing.
    /// </summary>
    public static (LightingStyle Style, bool Ok) PlannedLighting(TechTier tier, bool farm, TierStyleStock stock, bool powered)
    {
        const long torchWood = 20;
        const long lampSteel = 20;
        var none = new LightingStyle("", LightingScope.PerModule);
        return OneRungDown(
            tier,
            t =>
            {
                if (t >= TechTier.Powered)
                {
                    if (!powered || !stock.Has(new Resource("Steel"), lampSteel))
                    {
                        return (none, false);
                    }
                    return farm
                        ? (new LightingStyle("SunLamp", LightingScope.PerModule), true)
                        : (new LightingStyle("StandingLamp", LightingScope.PerSubCell), true);
                }
                if (t >= TechTier.Masonry)
                {
                    if (farm || !stock.Has(new Resource("WoodLog"), torchWood))
                    {
                        return (none, false);
                    }
                    return (new LightingStyle("TorchLamp", LightingScope.PerModule), true);
                }
                return (none, false);
            }
        );
    }
}
