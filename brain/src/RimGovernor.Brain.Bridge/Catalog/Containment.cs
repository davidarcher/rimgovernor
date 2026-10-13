using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_containment.go: the containment planner's one catalog lookup, every def input of P.ContainmentDefs,
// read from the defs and the game's own stat values.

public sealed partial class DefinitionCatalog
{
    private const string ContainmentStat = "ContainmentStrength";

    /// <summary>
    /// The prediction's inputs for a cell shelled with the wall and door defs built from their stuff (empty for a def made
    /// of none). The holder is the platform def (a <c>CompProperties_EntityHolderPlatform</c> def) with the greatest
    /// containmentFactor, the first by name on a tie. Anything the defs do not carry is an error naming it.
    /// </summary>
    public P.ContainmentDefs ContainmentDefs(string wall, string wallStuff, string door, string doorStuff)
    {
        var stat = DefRow<StatDef>(ContainmentStat) ?? throw new CatalogException($"the catalog has no {ContainmentStat} stat def");
        ThingDef? holder = null;
        float factor = 0;
        foreach (var (_, row) in _thingDefs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var platform in row.comps.Seq().OfType<CompProperties_EntityHolderPlatform>())
            {
                if (holder is null || platform.containmentFactor > factor)
                {
                    (holder, factor) = (row, platform.containmentFactor);
                }
            }
        }

        if (holder is null)
        {
            throw new CatalogException("the catalog has no holding platform def");
        }

        var holderBase = (double)(StatBaseEntry(holder, ContainmentStat) ?? stat.defaultBaseValue);
        double floorStrength = stat.defaultBaseValue;

        // The floor is the terrain whose stat table shows the greatest ContainmentStrength above the plain floor's; a
        // terrain the table shows no such stat for adds none.
        var floor = new P.ContainmentFloor();
        foreach (var name in _terrainDefs.Keys.Order(StringComparer.Ordinal))
        {
            double value;
            try
            {
                value = TerrainStatValue(name, ContainmentStat);
            }
            catch (Exception e) when (e is CatalogException or StatException)
            {
                continue;
            }

            if (value > floorStrength && (floor.Def.Length == 0 || value > floor.Strength))
            {
                floor = new P.ContainmentFloor { Def = name, Strength = value };
            }
        }

        double HitPoints(string def, string stuff)
        {
            var value = StatValue(def, stuff, PlanningStatNames.MaxHitPoints);
            return value > 0 ? value : throw new CatalogException($"{def} has no {PlanningStatNames.MaxHitPoints}");
        }

        var facilities = new List<P.ContainmentFacility>();
        foreach (var affected in holder.comps.Seq().OfType<CompProperties_AffectedByFacilities>())
        {
            foreach (var linked in affected.linkableFacilities.Seq())
            {
                var def = FindThing(linked.defName) ?? throw new CatalogException($"holder {holder.defName} links facility {linked.defName}, which the catalog lacks");
                foreach (var f in def.comps.Seq().OfType<CompProperties_Facility>())
                {
                    foreach (var offset in f.statOffsets.Seq().Where(m => m.stat.defName == ContainmentStat))
                    {
                        facilities.Add(new P.ContainmentFacility { Def = linked.defName, Offset = offset.value, MaxDistance = f.maxDistance, MaxSimultaneous = f.maxSimultaneous });
                    }
                }
            }
        }

        return new P.ContainmentDefs
        {
            Holder = holder.defName,
            HolderFactor = factor,
            HolderBase = holderBase,
            FloorStrength = floorStrength,
            Floor = floor,
            WallHP = HitPoints(wall, wallStuff),
            DoorHP = HitPoints(door, doorStuff),
            Facilities = new([.. facilities]),
        };
    }
}
