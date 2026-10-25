using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

public sealed partial class DefinitionCatalog
{
    private static readonly string[] NoStuff = [""];
    /// <summary>
    /// What every buildable def costs per stuff: the things, then the buildable floors (a one-cell terrain with no stuff),
    /// so the work-resource accounting prices a floor order the way it prices a wall.
    /// </summary>
    public EquatableArray<P.WorkBuildingCost> WorkBuildingCosts() => EquatableArray.From(
        _thingDefs.Values.Where(Buildable).OrderBy(d => d.defName, StringComparer.Ordinal).SelectMany(d =>
        {
            var stuffs = AllowedStuffs(d.defName);
            return (stuffs.Count == 0 ? NoStuff : stuffs).Select(stuff => new P.WorkBuildingCost(d.defName, stuff,
                EquatableArray.From(AdjustedCosts(d.defName, stuff).Select(c => new P.Amount(new(c.Def), c.Units))))
            { Size = new Domain.Cell(Math.Max(d.size.x, 1), Math.Max(d.size.z, 1)) });
        }).Concat(_terrainDefs.Values.Where(TerrainBuildable).OrderBy(t => t.defName, StringComparer.Ordinal).Select(t =>
            new P.WorkBuildingCost(t.defName, "",
                EquatableArray.From(TerrainAdjustedCosts(t.defName).Select(c => new P.Amount(new(c.Def), c.Units))))
            { Size = new Domain.Cell(1, 1) })));
}
