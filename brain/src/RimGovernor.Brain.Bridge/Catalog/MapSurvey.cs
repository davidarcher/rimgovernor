using static RimGovernor.Brain.Policy.SiteRules;
using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The whole-map survey the layout plan is derived from.

public sealed partial class DefinitionCatalog
{
    // Terrain affordance names (TerrainAffordanceDef defNames) of TerrainDef.affordances.
    private const string AffordanceHeavy = "Heavy";
    private const string AffordanceBridgeable = "Bridgeable";

    /// <summary>
    /// The whole-map survey the layout plan is derived from, read off the cell mirror's planning window: terrain and
    /// base-terrain affordances from the catalog, ore and trees from the thing list and the catalog's defs, roofs from
    /// the catalog's roof rules. A fogged cell is not held; it reads as solid rock to mine out, since the fog hides
    /// mountain far more often than a cavern, and the excavation steps check the cell once it is seen.
    /// <para>
    /// Hazard is the top terrain's own def flags. Dries is the top terrain's driesTo: the mirror carries no base terrain
    /// def, so ground a floor or bridge covers is not told to dry.
    /// </para>
    /// </summary>
    public P.MapSurvey SurveyFromCells(IReadOnlyList<P.SiteCell> cells, P.Bounds bounds)
    {
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            throw new CatalogException("invalid map survey bounds");
        }
        var roofs = RoofRules();
        var held = new List<P.SurveyCell>(cells.Count);
        foreach (var cell in cells)
        {
            if (cell.Cell.X < 0 || cell.Cell.X >= bounds.Width || cell.Cell.Z < 0 || cell.Cell.Z >= bounds.Height)
            {
                continue;
            }
            held.Add(SurveyCell(cell, roofs));
        }
        held.AddRange(UnseenRock(held, bounds));
        return new P.MapSurvey { Bounds = bounds, Cells = held };
    }

    // A rock cell for every cell of bounds that read left out (fog hides it): plannable ground, mined out like the
    // stone around it.
    private static List<P.SurveyCell> UnseenRock(List<P.SurveyCell> held, P.Bounds bounds)
    {
        var seen = new bool[bounds.Width * bounds.Height];
        foreach (var c in held)
        {
            if (c.Cell.X >= 0 && c.Cell.X < bounds.Width && c.Cell.Z >= 0 && c.Cell.Z < bounds.Height)
            {
                seen[(c.Cell.Z * bounds.Width) + c.Cell.X] = true;
            }
        }
        var rock = new List<P.SurveyCell>();
        for (var i = 0; i < seen.Length; i++)
        {
            if (!seen[i])
            {
                rock.Add(new P.SurveyCell { Cell = new Domain.Cell(i % bounds.Width, i / bounds.Width), Rock = true, Footing = P.Footing.Firm, ThickRoof = true });
            }
        }
        return rock;
    }

    // Decodes one held cell. A thick roof is the roof rules'; a roof def they lack is an error naming the unknown roof.
    private P.SurveyCell SurveyCell(P.SiteCell cell, P.RoofRules roofs)
    {
        HashSet<string> affordances = [];
        if (cell.BaseTerrain.ValueOrDefault is { Length: > 0 } baseName)
        {
            var baseTerrain = FindTerrain(baseName) ?? throw new CatalogException($"catalog has no def row for base terrain {baseName} at {cell.Cell}");
            affordances = [.. baseTerrain.affordances.Seq().Select(a => a.defName)];
        }
        var footing = P.Footing.Firm;
        if (!affordances.Contains(AffordanceHeavy))
        {
            footing = affordances.Contains(AffordanceLight) ? P.Footing.Light : P.Footing.None;
        }
        var roof = cell.Roof.ValueOrDefault ?? "";
        if (roof.Length > 0 && !roofs.ContainsKey(roof))
        {
            throw new CatalogException($"unknown roof def \"{roof}\" at {cell.Cell}");
        }
        var thick = roof.Length > 0 && roofs[roof].Thick;
        TerrainDef? terrain = null;
        var terrainName = cell.Terrain.ValueOrDefault ?? "";
        if (terrainName.Length > 0)
        {
            terrain = FindTerrain(terrainName) ?? throw new CatalogException($"catalog has no def row for terrain {terrainName} at {cell.Cell}");
        }
        var rock = cell.NaturalRock();
        var ruin = cell.Ruin();
        var edifice = cell.PlayerEdifice();
        bool ore = false, tree = false;
        foreach (var thing in cell.Things)
        {
            var def = FindThing(thing.Def);
            if (thing.Has(P.ThingMarks.Edifice))
            {
                ore = ore || def?.building is { isResourceRock: true };
            }
            else if (thing.Category == P.ThingCategory.Plant)
            {
                tree = tree || SurveyTree(def);
            }
        }
        return new P.SurveyCell
        {
            Cell = cell.Cell,
            Walkable = cell.Walkable.ValueOrDefault,
            Rock = rock,
            Built = edifice.Length > 0,
            Footing = footing,
            Bridgeable = affordances.Contains(AffordanceBridgeable),
            Dries = terrain?.driesTo is not null,
            Hazard = terrain is { } t && (t.dangerous || t.burnDamage > 0 || t.heatPerTick > 0 || t.toxicBuildupFactor > 0),
            ThickRoof = thick,
            Fertility = cell.Fertility.ValueOrDefault,
            Ore = ore,
            Tree = tree,
            // Occupied off rock, player edifice and clearable ruin is a standing prop.
            Prop = cell.Occupied() && !rock && edifice.Length == 0 && !ruin,
            Ruin = ruin,
        };
    }

    // A plant the survey marks a Tree cell for.
    private static bool SurveyTree(ThingDef? def) => def?.plant is { } plant && (plant.forceIsTree || plant.treeCategory != TreeCategory.None);

    /// <summary>
    /// The stuffs the cells offer a ring with nothing stocked (#3747): the items the survey's trees are cut into (their
    /// harvestedThingDef) and the blocks the natural rock's stone is cut into (its building.mineableThing's
    /// butcherProducts: a chunk's stonecutting yield), each by def name in order.
    /// </summary>
    public P.RingStuffSources RingStuffSourcesOf(IReadOnlyList<P.SiteCell> cells)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var wood = new SortedSet<string>(StringComparer.Ordinal);
        var blocks = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            foreach (var thing in cell.Things)
            {
                if (!seen.Add(thing.Def) || FindThing(thing.Def) is not { } def)
                {
                    continue;
                }
                if (thing.Category == P.ThingCategory.Plant && SurveyTree(def) && def.plant.harvestedThingDef is { } cut)
                {
                    wood.Add(cut.defName);
                }
                else if (thing.Has(P.ThingMarks.NaturalRock) && def.building?.mineableThing is { } chunk)
                {
                    foreach (var product in chunk.butcherProducts.Seq())
                    {
                        blocks.Add(product.thingDef.defName);
                    }
                }
            }
        }
        return new P.RingStuffSources { Wood = Domain.EquatableArray.From(wood.ToList()), Blocks = Domain.EquatableArray.From(blocks.ToList()) };
    }
}
