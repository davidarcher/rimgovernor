using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // All identity comes from arguments or the saved map, so stage reloads
    // exercise the same fixture without relying on process-static references.
    public sealed class ClearanceFixture
    {
        [Tool("test/clearance_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage a disposable Home clearance wall or three chunks on the tribal baseline; never issues controller orders.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, string scenario)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                if (!new[] { "ancient_wall", "roof_support_refused", "player_designation", "chunk_dump" }.Contains(scenario))
                    throw new ArgumentException("Unknown clearance scenario.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                var worker = people.First(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                var site = GenRadial.RadialCellsAround(worker.Position, 35, true).First(c =>
                    GenRadial.RadialCellsAround(c, 9, true).All(n => n.InBounds(map) && !n.Fogged(map)
                        && !n.Roofed(map) && n.GetEdifice(map) == null)
                    && new CellRect(c.x - 3, c.z - 3, 7, 7).Cells.All(n => n.Standable(map) && n.GetZone(map) == null
                        && n.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                    && worker.CanReach(c, PathEndMode.OnCell, Danger.None));
                // Isolate the census from unrelated ancient ruins and natural chunks.
                foreach (var c in map.areaManager.Home.ActiveCells.ToList()) map.areaManager.Home[c] = false;
                foreach (var c in new CellRect(site.x - 3, site.z - 3, 7, 7).Cells) {
                    foreach (var t in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) t.Destroy(DestroyMode.Vanish);
                    map.areaManager.Home[c] = true;
                }
                foreach (var p in people) {
                    p.jobs.StopAll();
                    foreach (var bad in p.health.hediffSet.hediffs.Where(h => h.def.isBad && !(h is Hediff_MissingPart)).ToList()) p.health.RemoveHediff(bad);
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if (!p.WorkTypeIsDisabled(work)) p.workSettings.SetPriority(work,
                            work == WorkTypeDefOf.Hauling || work == WorkTypeDefOf.Construction && scenario != "player_designation" ? 1 : 0);
                    for (int hour = 0; hour < 24; hour++) p.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                }
                var stone = GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Where(d => d.stuffProps.categories.Contains(StuffCategoryDefOf.Stony)).OrderBy(d => d.defName).First();
                var ids = new System.Collections.Generic.List<string>();
                var defs = new System.Collections.Generic.List<string>();
                string target = "";
                if (scenario == "chunk_dump") {
                    var rocks = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.thingCategories?.Contains(ThingCategoryDefOf.StoneChunks) == true).OrderBy(d => d.defName).Take(2).ToList();
                    if (rocks.Count != 2) throw new InvalidOperationException("Two stone chunk definitions required.");
                    rocks.Add(DefDatabase<ThingDef>.GetNamed("ChunkSlagSteel"));
                    foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Stockpile>())
                        foreach (var def in rocks) zone.GetStoreSettings().filter.SetAllow(def, false);
                    for (int i = 0; i < rocks.Count; i++) {
                        var chunk = GenSpawn.Spawn(ThingMaker.MakeThing(rocks[i]), site + IntVec3.East * (i - 1), map);
                        chunk.SetForbidden(false, false); ids.Add(chunk.GetUniqueLoadID()); defs.Add(chunk.def.defName);
                    }
                } else {
                    var wall = (Building)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, stone), site, map);
                    wall.SetForbidden(false, false); target = wall.GetUniqueLoadID();
                    if (wall.Faction != null || !wall.DeconstructibleBy(Faction.OfPlayer)) throw new InvalidOperationException("Wall must be unowned and deconstructible.");
                    if (scenario == "roof_support_refused") map.roofGrid.SetRoof(site + IntVec3.North, RoofDefOf.RoofConstructed);
                    else {
                        var support = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Column"), stone);
                        support.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(support, site + IntVec3.North * 2, map);
                        map.roofGrid.SetRoof(site + IntVec3.North, RoofDefOf.RoofConstructed);
                    }
                    if (scenario == "player_designation") map.designationManager.AddDesignation(new Designation(wall, DesignationDefOf.Deconstruct));
                }
                return new { success = true, target, chunks = ids, defs, x = site.x, z = site.z, stuff = stone.defName };
            }, cancellationToken);

        [Tool("test/clearance_support", Description = "UNSAFE FOR MODEL EXECUTION. Stage a completed player support column after the roof refusal, without removing the roof or target wall.")]
        public async Task<object> Support(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z, string stuff)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var cell = new IntVec3(x, 0, z + 2);
                if (!Find.TickManager.Paused || cell.GetEdifice(map) != null) throw new InvalidOperationException("Paused empty column site required.");
                var column = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Column"), DefDatabase<ThingDef>.GetNamed(stuff));
                column.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(column, cell, map);
                return new { success = true, column = column.GetUniqueLoadID() };
            }, cancellationToken);

        [Tool("test/clearance_audit", Description = "Read exact clearance target, surviving designations, chunk storage and dumping-zone settings in the disposable colony.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken, string target = "", string chunks = "")
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var wall = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == target);
                var ids = chunks.Split(';');
                var rows = map.listerThings.AllThings.Where(t => ids.Contains(t.GetUniqueLoadID())).Select(t => new {
                    id = t.GetUniqueLoadID(), stored = t.GetSlotGroup()?.Settings.AllowedToAccept(t) == true,
                    zone = (t.Position.GetZone(map) as Zone_Stockpile)?.label
                }).ToList();
                var zones = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Where(z => z.label == "RimGovernor dumping").Select(z => new {
                    label = z.label, priority = z.GetStoreSettings().Priority.ToString(),
                    allow = z.GetStoreSettings().filter.AllowedThingDefs.Select(d => d.defName).OrderBy(n => n).ToList(),
                    cells = z.Cells.Select(c => new { x = c.x, z = c.z, home = map.areaManager.Home[c], roofed = c.Roofed(map), building = c.GetEdifice(map) != null }).ToList()
                }).ToList();
                return new { success = true, present = wall != null, designated = map.designationManager.AllDesignations.Any(d => d.def == DesignationDefOf.Deconstruct && d.target.Thing?.GetUniqueLoadID() == target), chunks = rows, zones };
            }, cancellationToken);
    }
}
