using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_terrain.go: the floors.

public sealed partial class DefinitionCatalog
{
    // The StatDefs a floor is judged by.
    private const string StatCleanliness = "Cleanliness";
    private const string StatBeauty = "Beauty";
    private const string StatFlammability = "Flammability";

    // The affordance the ground of a light site must support (SiteCell.SupportsLight).
    private const string AffordanceLight = "Light";

    /// <summary>The price of every TerrainDef row: the table a flooring census scores its cells against, built once per catalog. A terrain the stat table does not show a floor stat for fails the whole table.</summary>
    public IReadOnlyDictionary<string, P.FloorTerrain> FloorTerrains() => _floorTerrains.Value;

    private Dictionary<string, P.FloorTerrain> BuildFloorTerrains() =>
        _terrainDefs.Keys.ToDictionary(name => name, FloorTerrain, StringComparer.Ordinal);

    /// <summary>
    /// What a TerrainDef says about the floor it lays: its cleanliness, beauty and flammability stat values from the
    /// stat table, and its path cost and natural flag from its def row. A terrain without a row or without one of the
    /// stats is an error, never a default.
    /// </summary>
    public P.FloorTerrain FloorTerrain(string name)
    {
        var row = FindTerrain(name) ?? throw new CatalogException($"catalog has no def row for terrain {name}");
        return new P.FloorTerrain
        {
            Cleanliness = TerrainStatValue(name, StatCleanliness),
            Beauty = TerrainStatValue(name, StatBeauty),
            Flammability = TerrainStatValue(name, StatFlammability),
            PathCost = row.pathCost,
            Fertility = row.fertility,
            Natural = row.natural,
            Tags = [.. row.tags.Seq()],
        };
    }

    /// <summary>The TerrainDef rows whose affordances include Light: the ground <c>SiteCell.SupportsLight</c> accepts.</summary>
    public IReadOnlySet<string> LightTerrains() =>
        _terrainDefs.Values.Where(row => row.affordances.Seq().Any(a => a.defName == AffordanceLight)).Select(row => row.defName).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every TerrainDef carrying any of <paramref name="tags"/>, sorted: the floors that satisfy a TerrainWithTags requirement.</summary>
    public IReadOnlyList<string> TerrainsWithTags(IReadOnlyCollection<string> tags) =>
        [.. _terrainDefs.Values.Where(row => row.tags.Seq().Any(tags.Contains)).Select(row => row.defName).Order(StringComparer.Ordinal)];
}
