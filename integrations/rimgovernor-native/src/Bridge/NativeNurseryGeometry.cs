using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Native;

/// <summary>One compact native topology read. No pairwise cell matrix or live map mutation.</summary>
internal static class NativeNurseryGeometry
{
    internal static NurseryGeometryRead Read(Map map)
    {
        var cells = new int[map.Size.x * map.Size.z];
        var sight = new bool[cells.Length];
        var ids = new Dictionary<Region, int>();
        var regions = new List<Region>();
        int Id(Region region)
        {
            if (ids.TryGetValue(region, out var id)) return id;
            id = regions.Count; ids.Add(region, id); regions.Add(region); return id;
        }
        for (var z = 0; z < map.Size.z; z++)
            for (var x = 0; x < map.Size.x; x++)
            {
                var cell = new IntVec3(x, 0, z); var index = x + z * map.Size.x;
                cells[index] = cell.GetRegion(map) is { } region ? Id(region) : -1;
                sight[index] = cell.CanBeSeenOverFast(map);
            }
        var rows = new List<NurseryRegionRead>();
        // Preserve RegionTraverser's link/side order, including duplicates. Its visited set performs deduplication.
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i]; var neighbours = new List<int>();
            foreach (var link in region.links)
                for (var side = 0; side < 2; side++)
                    if (link.regions[side] is { } next && (next.type & RegionType.Set_Passable) != 0) neighbours.Add(Id(next));
            rows.Add(new(i, region.IsDoorway, region.door == null || region.door.Open, region.Room?.ID ?? -1, EquatableArray.From(neighbours)));
        }
        // GenClamor.DoClamor's maxRegions literal has no native symbol (verified installed 1.7 IL).
        return new(map.Size.x, map.Size.z, EquatableArray.From(cells), EquatableArray.From(sight), EquatableArray.From(rows),
            MentalState_BabyFit.BabyScreamRadius, SocialInteractionUtility.MaxInteractRange, 15)
        {
            BedSlots = EquatableArray.From(DefDatabase<ThingDef>.AllDefs.Where(d => d.thingClass != null && typeof(Building_Bed).IsAssignableFrom(d.thingClass))
                .OrderBy(d => d.defName, StringComparer.Ordinal).Select(d => new NurseryBedSlotsRead(d.defName,
                    EquatableArray.From(Enumerable.Range(0, 4).Select(rotation =>
                    {
                        var cell = BedUtility.GetSleepingSlotPos(0, IntVec3.Zero, new Rot4(rotation), d.size);
                        return new Cell(cell.x, cell.z);
                    }))))),
        };
    }
}
