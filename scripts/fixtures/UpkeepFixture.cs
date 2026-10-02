using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable scenario preparation; never included in production builds.
    public sealed class UpkeepFixture
    {
        [Tool("test/deconstruct_prepare", Description = "Stage exact non-colony deconstruction targets. Test builds only.")]
        public async Task<object> PrepareDeconstruction(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
                var cells = GenRadial.RadialCellsAround(pawn.Position, 25, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null && !c.Roofed(map)
                    && RoofSupportSafety.GeometryKnown(map, c)
                    && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)).Take(30).ToList();
                if (cells.Count < 30) throw new InvalidOperationException("No open deconstruction fixture site.");
                var ids = new System.Collections.Generic.List<string>();
                for (int i = 0; i < 6; i++) {
                    var wall = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                    if (i == 0) wall.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(wall, cells[i * 5], map);
                    wall.SetForbidden(false, false);
                    ids.Add(wall.GetUniqueLoadID());
                }
                foreach (var p in map.mapPawns.FreeColonistsSpawned) {
                    foreach (var h in p.health.hediffSet.hediffs.Where(h => h.def.isBad && !(h is Hediff_MissingPart)).ToList()) p.health.RemoveHediff(h);
                    if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                    p.timetable.SetAssignment(GenLocalDate.HourOfDay(p), TimeAssignmentDefOf.Work);
                }
                var player = map.listerThings.AllThings.First(t => t.GetUniqueLoadID() == ids[1]);
                map.designationManager.AddDesignation(new Designation(player, DesignationDefOf.Deconstruct));
                return new { success = true, ids };
            }, cancellationToken);
        }

        [Tool("test/roof_cells", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture (#1366): with set, put a constructed roof over exact cells (\"x,z;x,z\"); always reply how many of them are roofed. Test builds only.")]
        public async Task<object> RoofCells(IRimBridgeContext ctx, CancellationToken cancellationToken, string cells, bool set = false)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var parsed = (cells ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split(','))
                    .Where(p => p.Length == 2).Select(p => new IntVec3(int.Parse(p[0]), 0, int.Parse(p[1]))).ToList();
                if (map == null || parsed.Count == 0 || parsed.Count > 256 || parsed.Any(c => !c.InBounds(map))) return new { success = false, error = "Expected 1..256 in-bounds x,z cells on the current map" };
                if (set) foreach (var c in parsed) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                return new { success = true, roofed = parsed.Count(c => c.Roofed(map)) };
            }, cancellationToken);

        [Tool("test/cell_things", Description = "Disposable fixture (#1245): the things standing on one cell (\"x,z\") with their def, stuff and id. Test builds only.")]
        public async Task<object> CellThings(IRimBridgeContext ctx, CancellationToken cancellationToken, string cell)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var p = (cell ?? "").Split(',');
                if (map == null || p.Length != 2 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var z) || !new IntVec3(x, 0, z).InBounds(map))
                    return new { success = false, error = "Expected one in-bounds x,z cell on the current map" };
                var things = new IntVec3(x, 0, z).GetThingList(map).Select(t => new { id = t.GetUniqueLoadID(), def = t.def.defName, stuff = t.Stuff?.defName ?? "" }).ToList();
                return new { success = true, things };
            }, cancellationToken);

        [Tool("test/deconstruct_target", Description = "Inspect or mutate one staged deconstruction target. Test builds only.")]
        public async Task<object> DeconstructionTarget(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact target id.")] string target,
            [ToolParameter(Description = "inspect, replace, vanish, or colony.", DefaultValue = "inspect")] string action = "inspect")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == target);
                if (thing != null && action == "replace") {
                    var prior = map.designationManager.DesignationOn(thing, DesignationDefOf.Deconstruct);
                    if (prior != null) map.designationManager.RemoveDesignation(prior);
                    map.designationManager.AddDesignation(new Designation(thing, DesignationDefOf.Deconstruct));
                }
                if (thing != null && action == "vanish") thing.Destroy(DestroyMode.Vanish);
                if (thing != null && action == "colony") thing.SetFaction(Faction.OfPlayer);
                return new { success = true, present = thing != null && thing.Spawned,
                    designated = thing != null && map.designationManager.DesignationOn(thing, DesignationDefOf.Deconstruct) != null };
            }, cancellationToken);
        }
    }
}
