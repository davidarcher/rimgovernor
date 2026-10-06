using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
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

        // The cell grid delta probe (#1551): reads the whole-map grid, spawns
        // one player wall at cell, reads it again and encodes the second read
        // against the first as the stream's delta, replying each carried
        // array's form and entry count and both grids' encoded sizes.
        [Tool("test/grid_delta", Description = "UNSAFE FOR MODEL EXECUTION. Disposable probe (#1551): spawn one player wood wall at cell (\"x,z\") between two whole-map grid reads and reply the delta's carried arrays. Test builds only.")]
        public async Task<object> GridDelta(IRimBridgeContext ctx, CancellationToken cancellationToken, string cell)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var p = (cell ?? "").Split(',');
                if (map == null || p.Length != 2 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var z) || !new IntVec3(x, 0, z).InBounds(map))
                    return new { success = false, error = "Expected one in-bounds x,z cell on the current map" };
                var at = new IntVec3(x, 0, z);
                var before = CellGridEncoder.Read(map);
                var wall = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, at, map);
                var after = CellGridEncoder.Read(map);
                var keyframe = CellGridEncoder.Encode(after, null)!;
                var delta = CellGridEncoder.Encode(after, before);
                if (delta == null) return new { success = false, error = "Every array changed" };
                var arrays = new System.Collections.Generic.List<object>();
                var descriptor = RimGovernor.Protocol.Mirror.CellGrid.Descriptor;
                for (int i = 0; i < CellGridEncoder.FieldCount; i++) {
                    var name = CellGridEncoder.FieldName(i);
                    if (!(descriptor.FindFieldByName(name).Accessor.GetValue(delta) is RimGovernor.Protocol.Mirror.FieldArray array)) continue;
                    arrays.Add(new { field = name, form = array.FormCase.ToString(), entries = array.FormCase == RimGovernor.Protocol.Mirror.FieldArray.FormOneofCase.Sparse ? array.Sparse.Index.Count : -1,
                        wallCell = array.FormCase == RimGovernor.Protocol.Mirror.FieldArray.FormOneofCase.Sparse && array.Sparse.Index.Contains((uint)(z * map.Size.x + x)) });
                }
                // The thing list is not a column: it rides the delta as sparse cell lists.
                if (delta.Things != null)
                    arrays.Add(new { field = "things", form = "Sparse", entries = delta.Things.Cells.Count, wallCell = delta.Things.Cells.Contains((uint)(z * map.Size.x + x)) });
                return new { success = true, wall = wall.GetUniqueLoadID(), keyframeBytes = keyframe.CalculateSize(), deltaBytes = delta.CalculateSize(), arrays };
            }, cancellationToken);

        // The thing list probe (#2261): spawns one of each thing category on a
        // row of seven cells from cell ("x,z") eastward (item, wall, wall
        // blueprint, wall frame, plant, filth, corpse) and replies the row as
        // a keyframe grid (base64 mirror.CellGrid), for the case to decode on
        // the Go side and compare with what was spawned. Also replies the
        // timed capture of the whole map (ms).
        [Tool("test/grid_things", Description = "UNSAFE FOR MODEL EXECUTION. Disposable probe (#2261): spawn one of each thing category on seven cells east of cell (\"x,z\") and reply that row as a base64 mirror.CellGrid keyframe plus the whole-map read and encode cost. Test builds only.")]
        public async Task<object> GridThings(IRimBridgeContext ctx, CancellationToken cancellationToken, string cell)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var p = (cell ?? "").Split(',');
                if (map == null || p.Length != 2 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var z) || !new IntVec3(x + 6, 0, z).InBounds(map) || !new IntVec3(x, 0, z).InBounds(map))
                    return new { success = false, error = "Expected one in-bounds x,z cell with six cells free to its east on the current map" };
                IntVec3 At(int i) => new IntVec3(x + i, 0, z);
                var steel = ThingMaker.MakeThing(ThingDefOf.Steel); steel.stackCount = 25;
                GenSpawn.Spawn(steel, At(0), map);
                var wall = (Building)ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, At(1), map);
                GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, At(2), map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                var frame = (Frame)ThingMaker.MakeThing(ThingDefOf.Wall.frameDef, ThingDefOf.WoodLog);
                frame.SetFactionDirect(Faction.OfPlayer);
                frame.SetStuffDirect(ThingDefOf.WoodLog);
                GenSpawn.Spawn(frame, At(3), map);
                var plant = (Plant)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Plant_Potato"));
                GenSpawn.Spawn(plant, At(4), map);
                plant.Growth = 0.5f;
                GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Filth_Dirt")), At(5), map);
                var animal = PawnGenerator.GeneratePawn(PawnKindDef.Named("Muffalo"));
                GenSpawn.Spawn(animal, At(6), map);
                animal.Kill(null);
                var row = CellGridEncoder.Encode(CellGridEncoder.Read(map, x, z, 7, 1), null)!;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var whole = CellGridEncoder.Read(map);
                var readMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                var wire = CellGridEncoder.Encode(whole, null)!;
                var encodeMs = watch.Elapsed.TotalMilliseconds;
                return new { success = true, grid = Convert.ToBase64String(Google.Protobuf.MessageExtensions.ToByteArray(row)),
                    frameId = frame.thingIDNumber, wallId = wall.thingIDNumber, steelId = steel.thingIDNumber, readMs, encodeMs, keyframeBytes = wire.CalculateSize() };
            }, cancellationToken);

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

        [Tool("test/floor_cells", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture (#1245): with def, lay that constructed floor on exact cells (\"x,z;x,z\"); always reply how many of them carry a removable constructed floor. Test builds only.")]
        public async Task<object> FloorCells(IRimBridgeContext ctx, CancellationToken cancellationToken, string cells, string def = "")
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var parsed = (cells ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split(','))
                    .Where(p => p.Length == 2).Select(p => new IntVec3(int.Parse(p[0]), 0, int.Parse(p[1]))).ToList();
                if (map == null || parsed.Count == 0 || parsed.Count > 256 || parsed.Any(c => !c.InBounds(map))) return new { success = false, error = "Expected 1..256 in-bounds x,z cells on the current map" };
                if (!string.IsNullOrEmpty(def)) {
                    var terrain = DefDatabase<TerrainDef>.GetNamedSilentFail(def);
                    if (terrain == null) return new { success = false, error = "Unknown terrain " + def };
                    foreach (var c in parsed) map.terrainGrid.SetTerrain(c, terrain);
                }
                return new { success = true, floored = parsed.Count(c => map.terrainGrid.CanRemoveTopLayerAt(c)) };
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
