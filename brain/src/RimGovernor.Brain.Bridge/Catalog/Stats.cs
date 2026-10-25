using RimGovernor.Brain.Domain;
using Verse;

namespace RimGovernor.Brain.Bridge;

// Stat reads over the load's precomputed def stat table (DefStatTable).

/// <summary>The StatDef names the catalog compares def fields against.</summary>
public static class StatNames
{
    public const string MarketValue = "MarketValue";
    public const string Nutrition = "Nutrition";
    public const string MedicalPotency = "MedicalPotency";
    public const string Beauty = "Beauty";
    public const string WorkToBuild = "WorkToBuild";
    public const string Comfort = "Comfort";
    public const string Cleanliness = "Cleanliness";
}

/// <summary>
/// A stat the catalog reads from the game: the StatDef name and whether it is read for subjects the game hides it
/// for. Only <see cref="CatalogStats"/> creates one, so the declared list is every stat the brain can ask for and the
/// stat table computes exactly that list.
/// </summary>
public sealed class CatalogStat
{
    internal CatalogStat(string name, bool readHidden)
    {
        Name = name;
        ReadHidden = readHidden;
    }

    /// <summary>The StatDef name.</summary>
    public string Name { get; }

    /// <summary>The value is computed for every subject, not only those the game shows the stat for.</summary>
    public bool ReadHidden { get; }

    public override string ToString() => Name;
}

/// <summary>Every stat the brain reads through the catalog; <see cref="All"/> is what the stat table computes.</summary>
public static class CatalogStats
{
    private static readonly List<CatalogStat> Declared = [];

    public static readonly CatalogStat MarketValue = Declare(StatNames.MarketValue);
    public static readonly CatalogStat Nutrition = Declare(StatNames.Nutrition);
    public static readonly CatalogStat Beauty = Declare(StatNames.Beauty);
    public static readonly CatalogStat Cleanliness = Declare(StatNames.Cleanliness);
    public static readonly CatalogStat Mass = Declare("Mass");
    public static readonly CatalogStat MaxHitPoints = Declare("MaxHitPoints");
    public static readonly CatalogStat DoorOpenSpeed = Declare("DoorOpenSpeed");
    // Read for every product, as RecipeDef.WorkAmountTotal reads it whether or not the game shows it.
    public static readonly CatalogStat WorkToMake = Declare("WorkToMake", readHidden: true);
    public static readonly CatalogStat ContainmentStrength = Declare("ContainmentStrength");
    public static readonly CatalogStat BandwidthCost = Declare("BandwidthCost");
    // Read for every race, as the game's carrying, taming and handling code reads them whether or not it shows them.
    public static readonly CatalogStat CarryingCapacity = Declare("CarryingCapacity", readHidden: true);
    public static readonly CatalogStat Wildness = Declare("Wildness", readHidden: true);
    public static readonly CatalogStat MinimumHandlingSkill = Declare("MinimumHandlingSkill", readHidden: true);
    public static readonly CatalogStat LeatherAmount = Declare("LeatherAmount");
    public static readonly CatalogStat MeatAmount = Declare("MeatAmount");
    public static readonly CatalogStat ArmorSharp = Declare("ArmorRating_Sharp");
    public static readonly CatalogStat ArmorBlunt = Declare("ArmorRating_Blunt");
    public static readonly CatalogStat ArmorHeat = Declare("ArmorRating_Heat");
    public static readonly CatalogStat InsulationCold = Declare("Insulation_Cold");
    public static readonly CatalogStat InsulationHeat = Declare("Insulation_Heat");

    // Read whether or not the game shows them: Flammability and BedRestEffectiveness of
    // placed furniture, the Comfort of every sleeping bed (#3840) and the WorkToBuild of terrain, which the game does not show.
    public static readonly CatalogStat Flammability = Declare("Flammability", readHidden: true);
    public static readonly CatalogStat BedRestEffectiveness = Declare("BedRestEffectiveness", readHidden: true);
    public static readonly CatalogStat Comfort = Declare(StatNames.Comfort, readHidden: true);
    public static readonly CatalogStat WorkToBuild = Declare(StatNames.WorkToBuild, readHidden: true);

    // Read for every medicine: a def that does not set it has the stat's default (#3635).
    public static readonly CatalogStat MedicalQualityMax = Declare(nameof(RimWorld.StatDefOf.MedicalQualityMax), readHidden: true);

    /// <summary>Every declared stat, in declaration order.</summary>
    public static IReadOnlyList<CatalogStat> All => Declared;

    private static CatalogStat Declare(string name, bool readHidden = false)
    {
        var stat = new CatalogStat(name, readHidden);
        Declared.Add(stat);
        return stat;
    }
}

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// The game's <c>GetStatValueAbstract(stat, stuff)</c> of def, with stuff empty for a def not made from stuff. A
    /// stat the game does not show for the def is an error, never a default.
    /// </summary>
    public float StatValue(string def, string stuff, CatalogStat stat)
    {
        var (value, shown) = ShownStatValue(def, stuff, stat);
        return shown ? value : throw new CatalogException($"stat {stat} is not shown for def {def} with stuff \"{stuff}\"");
    }

    /// <summary>
    /// StatValue for a stat that is legitimately absent from a def: <c>Shown</c> is false when the game does not show
    /// the stat for the def.
    /// </summary>
    public (float Value, bool Shown) ShownStatValue(string def, string stuff, CatalogStat stat) =>
        ShownValue(stat, StatThing(def), StuffOf(stuff), false);

    /// <summary>The stat of a thing made of stuff, read whether or not the game shows it: a stat the def does not set has the stat def's default.</summary>
    public float PlannerStatValue(string def, string stuff, CatalogStat stat) =>
        ShownValue(stat, StatThing(def), StuffOf(stuff), true).Value;

    private (float Value, bool Shown) ShownValue(CatalogStat stat, BuildableDef def, ThingDef? stuff, bool planner)
    {
        if (planner && !stat.ReadHidden)
        {
            throw new CatalogException($"stat {stat} is read whether shown or not, but CatalogStats does not declare it ReadHidden");
        }

        var result = Stats.Evaluate(stat, def, stuff);
        if (!result.Shown && !planner)
        {
            return (0, false);
        }

        return float.IsFinite(result.Value)
            ? (result.Value, true)
            : throw new CatalogException($"stat {stat} of {def.defName} with stuff \"{stuff?.defName}\" is {result.Value}");
    }

    /// <summary>The game's <c>ThingDef.CostListAdjusted(stuff)</c> of def; an empty list is a def with no cost.</summary>
    public IReadOnlyList<StatCost> AdjustedCosts(string def, string stuff) => Stats.Costs(StatThing(def), StuffOf(stuff));

    /// <summary>The game's <c>GenStuff.AllowedStuffsFor(def)</c>: the stuffs the def can be made from, empty for a def not made from stuff.</summary>
    public IReadOnlyList<string> AllowedStuffs(string def) =>
        Stats.AllowedStuffs(_thingDefs.TryGetValue(def, out var thing) ? thing : _terrainDefs.TryGetValue(def, out var terrain) ? terrain : throw new StatException($"unknown def {def}"));

    /// <summary>The game's <c>CostListAdjusted(null)</c> of a TerrainDef; an empty list is a free floor.</summary>
    public IReadOnlyList<StatCost> TerrainAdjustedCosts(string terrain) => Stats.Costs(StatTerrain(terrain), null);

    /// <summary>The game's WorkToBuild of a TerrainDef, read whether shown or not (the game does not show the stat for terrain).</summary>
    public float TerrainWorkToBuild(string terrain) =>
        ShownValue(CatalogStats.WorkToBuild, StatTerrain(terrain), null, true).Value;

    /// <summary>The game's <c>GetStatValueAbstract(stat)</c> of a TerrainDef; a stat the game does not show for it is an error.</summary>
    public float TerrainStatValue(string terrain, CatalogStat stat) =>
        TerrainShownStat(terrain, stat) ?? throw new CatalogException($"stat {stat} is not shown for terrain {terrain}");

    /// <summary>The game's <c>GetStatValueAbstract(stat)</c> of a TerrainDef; null when the game does not show it for it.</summary>
    public float? TerrainShownStat(string terrain, CatalogStat stat)
    {
        var (value, shown) = ShownValue(stat, StatTerrain(terrain), null, false);
        return shown ? value : null;
    }

    private ThingDef StatThing(string def) =>
        _thingDefs.TryGetValue(def, out var row) ? row : throw new StatException($"unknown def {def}");

    private TerrainDef StatTerrain(string terrain) =>
        _terrainDefs.TryGetValue(terrain, out var row) ? row : throw new StatException($"unknown terrain {terrain}");

    private ThingDef? StuffOf(string stuff) =>
        stuff.Length == 0 ? null : _thingDefs.TryGetValue(stuff, out var row) ? row : throw new StatException($"unknown stuff {stuff}");
}
