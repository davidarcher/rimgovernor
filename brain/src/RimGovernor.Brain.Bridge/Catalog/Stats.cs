using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Bridge;

// Go catalog_stats.go: stat reads through IStatSource (the Go stateval evaluator over a stat environment is not
// ported; the source is the game-thread service or a fake).

/// <summary>The StatDef names the catalog reads.</summary>
public static class StatNames
{
    public const string MarketValue = "MarketValue";
    public const string Nutrition = "Nutrition";
    public const string MedicalPotency = "MedicalPotency";
    public const string Beauty = "Beauty";
    public const string DeteriorationRate = "DeteriorationRate";
    public const string WorkToBuild = "WorkToBuild";
    public const string Comfort = "Comfort";
    public const string Cleanliness = "Cleanliness";
}

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// The game's <c>GetStatValueAbstract(stat, stuff)</c> of def, with stuff empty for a def not made from stuff. A
    /// stat the game does not show for the def is an error, never a default.
    /// </summary>
    public float StatValue(string def, string stuff, string stat)
    {
        var (value, shown) = ShownStatValue(def, stuff, stat);
        return shown ? value : throw new CatalogException($"stat {stat} is not shown for def {def} with stuff \"{stuff}\"");
    }

    /// <summary>
    /// StatValue for a stat that is legitimately absent from a def: <c>Shown</c> is false when the game does not show
    /// the stat for the def. DeteriorationRate is a planner stat of every ThingDef, which the game hides for a def
    /// that does not set it: it is read whether shown or not.
    /// </summary>
    public (float Value, bool Shown) ShownStatValue(string def, string stuff, string stat) =>
        ShownValue(stat, StatSubject.ForThing(def, stuff), stat == StatNames.DeteriorationRate);

    /// <summary>The stat of a thing made of stuff, read whether or not the game shows it: a stat the def does not set has the stat def's default.</summary>
    public float PlannerStatValue(string def, string stuff, string stat) =>
        ShownValue(stat, StatSubject.ForThing(def, stuff), true).Value;

    private (float Value, bool Shown) ShownValue(string stat, StatSubject subject, bool planner)
    {
        var result = Stats.Evaluate(stat, subject);
        if (!result.Shown && !planner)
        {
            return (0, false);
        }

        return float.IsFinite(result.Value)
            ? (result.Value, true)
            : throw new CatalogException($"stat {stat} of {subject.Def} with stuff \"{subject.Stuff}\" is {result.Value}");
    }

    /// <summary>The game's <c>ThingDef.CostListAdjusted(stuff)</c> of def; an empty list is a def with no cost.</summary>
    public IReadOnlyList<StatCost> AdjustedCosts(string def, string stuff) => Stats.CostListAdjusted(def, stuff);

    /// <summary>The game's <c>GenStuff.AllowedStuffsFor(def)</c>: the stuffs the def can be made from, empty for a def not made from stuff.</summary>
    public IReadOnlyList<string> AllowedStuffs(string def) => Stats.AllowedStuffsFor(def);

    /// <summary>The game's <c>CostListAdjusted(null)</c> of a TerrainDef; an empty list is a free floor.</summary>
    public IReadOnlyList<StatCost> TerrainAdjustedCosts(string terrain) => Stats.TerrainCostListAdjusted(terrain);

    /// <summary>The game's WorkToBuild of a TerrainDef, read whether shown or not (the game does not show the stat for terrain).</summary>
    public float TerrainWorkToBuild(string terrain) =>
        ShownValue(StatNames.WorkToBuild, StatSubject.ForTerrain(terrain), true).Value;

    /// <summary>The game's <c>GetStatValueAbstract(stat)</c> of a TerrainDef; a stat the game does not show for it is an error.</summary>
    public float TerrainStatValue(string terrain, string stat)
    {
        var (value, shown) = ShownValue(stat, StatSubject.ForTerrain(terrain), false);
        return shown ? value : throw new CatalogException($"stat {stat} is not shown for terrain {terrain}");
    }
}
