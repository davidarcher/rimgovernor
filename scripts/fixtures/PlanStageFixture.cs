using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only. Stages the ground of a
    // plan the controller has already derived, so the plan-vs-ground
    // reconciler has a standing room to work on: finished player walls and
    // doors of a chosen stuff, a roof over the room, a constructed floor,
    // furniture with a fixed quality and hit points, loose stock, a stockpile
    // (the warehouse) and finished research. Nothing here designates or orders
    // anything. audit reads back the things (by load id, spawned or packed)
    // and the cells (terrain, roof and the room each stands in) a case
    // compares before and after the reconciler ran.
    public sealed class PlanStageFixture
    {
        [Tool("test/plan_stage", Description = "UNSAFE FOR MODEL EXECUTION. Disposable plan-staging fixture (#2118). action stage takes spec JSON {research:[ResearchProjectDef], walls:[{x,z,stuff}], doors:[{x,z,rotation 0..3,stuff}], roof:[{minX,minZ,maxX,maxZ}], floor:[{def,cells:[{x,z}]}], things:[{def,stuff,x,z,rotation,quality 0..6,hitFraction 0..1,foreign (true leaves the thing unowned and a plant fully grown),filled (true loads an ancient casket with friendly contents)}], drops:[{def,count,x,z}], stockpile:{minX,minZ,maxX,maxZ}} and replies the staged things' load ids; walls, doors and things become finished player buildings, moving pawns and items off their cells. action audit takes {ids:[load id],cells:[{x,z}]} and replies each id's def, stuff, quality, hit points, cell and whether it is packed (a minified item) or spawned, and each cell's edifice (def, stuff, id), terrain, roof and room (id, role, enclosed).")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "stage", string spec = "{}")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("Disposable map required.");
                var body = JObject.Parse(string.IsNullOrWhiteSpace(spec) ? "{}" : spec);
                if (action == "audit") return Audit(map, body);
                if (action != "stage") throw new InvalidOperationException("Unknown action " + action);
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                return Stage(map, body);
            }, cancellationToken);
        }

        private static IEnumerable<JObject> Rows(JToken token)
        {
            return (token as JArray ?? new JArray()).OfType<JObject>();
        }

        private static IntVec3 CellOf(Map map, JObject row)
        {
            var cell = new IntVec3((int)row["x"], 0, (int)row["z"]);
            if (!cell.InBounds(map)) throw new ArgumentException("Cell " + cell + " is out of bounds.");
            return cell;
        }

        private static ThingDef DefOf(string name)
        {
            return DefDatabase<ThingDef>.GetNamedSilentFail(name ?? "") ?? throw new ArgumentException("No ThingDef " + name);
        }

        private static ThingDef StuffFor(ThingDef def, string name)
        {
            if (!def.MadeFromStuff) return null;
            if (string.IsNullOrEmpty(name)) return GenStuff.DefaultStuffFor(def);
            return DefDatabase<ThingDef>.GetNamedSilentFail(name) ?? throw new ArgumentException("No stuff " + name);
        }

        // Vacate moves pawns and items off a cell and removes what a finished
        // building cannot share it with.
        private static void Vacate(Map map, IntVec3 cell, HashSet<IntVec3> avoid)
        {
            foreach (var t in cell.GetThingList(map).Where(t => t is Pawn || t.def.category == ThingCategory.Item).ToList()) {
                var aside = GenRadial.RadialCellsAround(cell, 9.9f, false)
                    .FirstOrDefault(n => n.InBounds(map) && n.Standable(map) && n.GetEdifice(map) == null && !avoid.Contains(n));
                if (aside == default(IntVec3)) throw new InvalidOperationException("Nowhere to move " + t.LabelShort + " off " + cell);
                if (t is Pawn pawn) { pawn.Position = aside; pawn.Notify_Teleported(true, true); continue; }
                t.DeSpawn();
                GenPlace.TryPlaceThing(t, aside, map, ThingPlaceMode.Near);
            }
            foreach (var t in cell.GetThingList(map).Where(t => t is Plant || t is Filth || t is Blueprint || t is Frame).ToList()) t.Destroy(DestroyMode.Vanish);
        }

        private static Thing Build(Map map, ThingDef def, ThingDef stuff, IntVec3 cell, Rot4 rot, bool foreign = false)
        {
            var thing = ThingMaker.MakeThing(def, stuff);
            if (!foreign) thing.SetFaction(Faction.OfPlayer);
            if (thing is Plant plant) plant.Growth = 1f;
            return GenSpawn.Spawn(thing, cell, map, rot);
        }

        // Fill loads an ancient casket with friendly pod contents, so the
        // reconciler meets a casket that still holds something.
        private static void Fill(Map map, Building_AncientCryptosleepCasket casket)
        {
            var parms = default(ThingSetMakerParams);
            parms.podContentsType = PodContentsType.AncientFriendly;
            parms.tile = map.Tile;
            foreach (var thing in ThingSetMakerDefOf.MapGen_AncientPodContents.root.Generate(parms))
                if (!casket.TryAcceptThing(thing, false)) throw new InvalidOperationException("Casket refused its contents.");
            if (casket.Faction != null) casket.SetFaction(null);
            if (!casket.HasAnyContents) throw new InvalidOperationException("Casket holds nothing.");
        }

        private static object Stage(Map map, JObject spec)
        {
            var finished = new List<string>();
            foreach (var name in (spec["research"] as JArray ?? new JArray()).Select(t => (string)t)) {
                var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name) ?? throw new ArgumentException("No research " + name);
                if (!project.IsFinished) Find.ResearchManager.FinishProject(project, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
                finished.Add(name);
            }
            var avoid = new HashSet<IntVec3>();
            var wallRows = Rows(spec["walls"]).ToList();
            var doorRows = Rows(spec["doors"]).ToList();
            var thingRows = Rows(spec["things"]).ToList();
            foreach (var row in wallRows.Concat(doorRows).Concat(thingRows)) avoid.Add(CellOf(map, row));
            var spawned = new List<object>();
            foreach (var row in wallRows) {
                var cell = CellOf(map, row);
                if (cell.GetEdifice(map) is Building held && held.Faction == Faction.OfPlayer && held.def == ThingDefOf.Wall) continue;
                if (cell.GetEdifice(map) != null) throw new InvalidOperationException("Cell " + cell + " already holds " + cell.GetEdifice(map).def.defName);
                Vacate(map, cell, avoid);
                var wall = Build(map, ThingDefOf.Wall, StuffFor(ThingDefOf.Wall, (string)row["stuff"]), cell, Rot4.North);
                spawned.Add(new { x = cell.x, z = cell.z, def = wall.def.defName, id = wall.GetUniqueLoadID() });
            }
            foreach (var row in doorRows) {
                var cell = CellOf(map, row);
                if (cell.GetEdifice(map) is Building heldDoor && heldDoor.Faction == Faction.OfPlayer && heldDoor.def == ThingDefOf.Door) continue;
                if (cell.GetEdifice(map) != null) throw new InvalidOperationException("Cell " + cell + " already holds " + cell.GetEdifice(map).def.defName);
                Vacate(map, cell, avoid);
                var door = Build(map, ThingDefOf.Door, StuffFor(ThingDefOf.Door, (string)row["stuff"]), cell, new Rot4((int?)row["rotation"] ?? 0));
                spawned.Add(new { x = cell.x, z = cell.z, def = door.def.defName, id = door.GetUniqueLoadID() });
            }
            foreach (var row in Rows(spec["roof"])) {
                foreach (var cell in CellRect.FromLimits((int)row["minX"], (int)row["minZ"], (int)row["maxX"], (int)row["maxZ"]))
                    if (cell.InBounds(map)) map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            }
            foreach (var row in Rows(spec["floor"])) {
                var terrain = DefDatabase<TerrainDef>.GetNamedSilentFail((string)row["def"]) ?? throw new ArgumentException("No TerrainDef " + row["def"]);
                foreach (var c in Rows(row["cells"])) map.terrainGrid.SetTerrain(CellOf(map, c), terrain);
            }
            var things = new List<object>();
            foreach (var row in thingRows) {
                var def = DefOf((string)row["def"]);
                var cell = CellOf(map, row);
                Vacate(map, cell, avoid);
                var foreign = (bool?)row["foreign"] == true;
                var thing = Build(map, def, StuffFor(def, (string)row["stuff"]), cell, new Rot4((int?)row["rotation"] ?? 0), foreign);
                if ((bool?)row["filled"] == true && thing is Building_AncientCryptosleepCasket casket) Fill(map, casket);
                if ((int?)row["quality"] is int quality && thing.TryGetComp<CompQuality>() is CompQuality comp)
                    comp.SetQuality((QualityCategory)Math.Max(0, Math.Min(6, quality)), ArtGenerationContext.Colony);
                if ((double?)row["hitFraction"] is double fraction && def.useHitPoints)
                    thing.HitPoints = Math.Max(1, Math.Min(thing.MaxHitPoints, (int)Math.Round(thing.MaxHitPoints * fraction)));
                things.Add(Describe(thing, null));
            }
            foreach (var row in Rows(spec["drops"])) {
                var def = DefOf((string)row["def"]);
                var cell = CellOf(map, row);
                var remaining = (int?)row["count"] ?? 1;
                while (remaining > 0) {
                    var stack = ThingMaker.MakeThing(def, StuffFor(def, null));
                    var placed = Math.Min(remaining, def.stackLimit);
                    stack.stackCount = placed;
                    if (!GenPlace.TryPlaceThing(stack, cell, map, ThingPlaceMode.Near)) throw new InvalidOperationException(def.defName + " placement failed near " + cell);
                    if (!stack.Destroyed) stack.SetForbidden(false, false);
                    remaining -= placed;
                }
            }
            object zoneRect = null;
            if (spec["stockpile"] is JObject pile) {
                var rect = CellRect.FromLimits((int)pile["minX"], (int)pile["minZ"], (int)pile["maxX"], (int)pile["maxZ"]);
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                foreach (var c in rect.Cells) zone.AddCell(c);
                zoneRect = new { minX = rect.minX, minZ = rect.minZ, maxX = rect.maxX, maxZ = rect.maxZ };
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            return new { success = true, tick = Find.TickManager.TicksGame, research = finished, spawned, things, stockpile = zoneRect };
        }

        // Describe is one thing's audit row; packedIn is the minified item
        // holding it, when it is packed.
        private static object Describe(Thing thing, MinifiedThing packedIn)
        {
            var at = packedIn != null ? packedIn.PositionHeld : thing.PositionHeld;
            QualityCategory quality;
            var hasQuality = thing.TryGetQuality(out quality);
            return new {
                id = thing.GetUniqueLoadID(), def = thing.def.defName, stuff = thing.Stuff?.defName,
                quality = hasQuality ? (int?)quality : null, hitPoints = thing.HitPoints, maxHitPoints = thing.MaxHitPoints,
                x = at.x, z = at.z, packed = packedIn != null, packedId = packedIn?.GetUniqueLoadID(),
                spawned = packedIn != null ? packedIn.Spawned : thing.Spawned
            };
        }

        private static object Audit(Map map, JObject spec)
        {
            var wanted = new HashSet<string>((spec["ids"] as JArray ?? new JArray()).Select(t => (string)t));
            var found = new Dictionary<string, object>();
            foreach (var t in map.listerThings.AllThings.ToList()) {
                if (wanted.Contains(t.GetUniqueLoadID())) found[t.GetUniqueLoadID()] = Describe(t, null);
                if (t is MinifiedThing m && m.InnerThing != null && wanted.Contains(m.InnerThing.GetUniqueLoadID()))
                    found[m.InnerThing.GetUniqueLoadID()] = Describe(m.InnerThing, m);
            }
            foreach (var p in map.mapPawns.FreeColonistsSpawned) {
                if (!(p.carryTracker?.CarriedThing is MinifiedThing carried) || carried.InnerThing == null) continue;
                if (wanted.Contains(carried.InnerThing.GetUniqueLoadID())) found[carried.InnerThing.GetUniqueLoadID()] = Describe(carried.InnerThing, carried);
            }
            var things = wanted.OrderBy(id => id).Select(id => found.ContainsKey(id) ? found[id] : new { id, def = (string)null }).ToList();
            var cells = new List<object>();
            foreach (var row in Rows(spec["cells"])) {
                var cell = CellOf(map, row);
                var room = cell.GetRoom(map);
                var edifice = cell.GetEdifice(map);
                var enclosed = room != null && !room.TouchesMapEdge && room.OpenRoofCount == 0 && !room.PsychologicallyOutdoors;
                cells.Add(new {
                    x = cell.x, z = cell.z, edifice = edifice?.def.defName, edificeStuff = edifice?.Stuff?.defName, edificeId = edifice?.GetUniqueLoadID(), terrain = cell.GetTerrain(map).defName, roof = cell.GetRoof(map)?.defName, roofed = cell.Roofed(map),
                    room = room?.ID, role = room?.Role?.defName, enclosed
                });
            }
            return new { success = true, tick = Find.TickManager.TicksGame, things, cells };
        }
    }
}
