using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal static class DrillingGuard
    {
        private static bool patched;
        internal static void Install()
        {
            if (patched) return;
            var harmony = new Harmony("rimbot.bounded-drilling");
            harmony.Patch(AccessTools.Method(typeof(CompDeepDrill), "CanDrillNow"), postfix: new HarmonyMethod(typeof(DrillingGuard), nameof(Available)));
            harmony.Patch(AccessTools.Method(typeof(CompDeepDrill), "DrillWorkDone"),
                prefix: new HarmonyMethod(typeof(DrillingGuard), nameof(Before)), postfix: new HarmonyMethod(typeof(DrillingGuard), nameof(After)));
            harmony.Patch(AccessTools.Method(typeof(Blueprint), "TryReplaceWithSolidThing"),
                prefix: new HarmonyMethod(typeof(DrillingGuard), nameof(BeforeBlueprint)), postfix: new HarmonyMethod(typeof(DrillingGuard), nameof(AfterBlueprint)));
            harmony.Patch(AccessTools.Method(typeof(Frame), "CompleteConstruction"),
                prefix: new HarmonyMethod(typeof(DrillingGuard), nameof(BeforeFrame)), postfix: new HarmonyMethod(typeof(DrillingGuard), nameof(AfterFrame)));
            patched = true;
        }
        private static DrillingRecord Record(CompDeepDrill comp) => comp.parent.Spawned
            ? MiningGuard.State().Drills.FirstOrDefault(r => r.MapId == comp.parent.Map.uniqueID
                && r.ThingId == comp.parent.ThingID && r.Definition == comp.parent.def.defName
                && r.X == comp.parent.Position.x && r.Z == comp.parent.Position.z)
            : null;
        private static int Stock(Map map, string resource) => map.listerThings
            .ThingsOfDef(DefDatabase<ThingDef>.GetNamed(resource)).Sum(t => t.stackCount);
        private static int AvailableStock(Map map, string resource) => map.listerThings
            .ThingsOfDef(DefDatabase<ThingDef>.GetNamed(resource)).Where(t => !t.Position.Fogged(map)
                && (t.Faction == null || t.Faction.IsPlayer) && !t.IsForbidden(Faction.OfPlayer)
                && map.mapPawns.FreeColonistsSpawned.Any(p => !p.Downed && !p.Drafted && !p.InMentalState
                    && p.CanReach(t, PathEndMode.Touch, Danger.None))).Sum(t => t.stackCount);
        private static bool Allowed(CompDeepDrill comp, DrillingRecord record)
        {
            if (record == null || !Supervisor.IsActive) return true;
            var building = comp.parent;
            return record.ThingId == building.ThingID && record.Target > AvailableStock(building.Map, record.Resource)
                && DeepDrillUtility.GetNextResource(building.Position, building.Map, out var resource, out var count, out var cell)
                && resource.defName == record.Resource && count > 0 && !cell.Fogged(building.Map)
                && !building.IsForbidden(Faction.OfPlayer) && !building.Position.Roofed(building.Map);
        }
        private static void Available(CompDeepDrill __instance, ref bool __result)
        { if (__result) __result = Allowed(__instance, Record(__instance)); }
        private static bool Before(CompDeepDrill __instance, out int __state)
        {
            var record = Record(__instance);
            __state = record == null ? -1 : Stock(__instance.parent.Map, record.Resource);
            return Allowed(__instance, record);
        }
        private static void After(CompDeepDrill __instance, int __state)
        {
            var record = Record(__instance);
            if (record != null && __state >= 0)
                record.Recovered += Math.Max(0, Stock(__instance.parent.Map, record.Resource) - __state);
        }
        private static void BeforeBlueprint(Blueprint __instance, out DrillingRecord __state)
        { __state = MiningGuard.State().Drills.FirstOrDefault(r => r.PendingId == __instance.ThingID && r.MapId == __instance.Map?.uniqueID); }
        private static void AfterBlueprint(bool __result, Thing createdThing, DrillingRecord __state)
        { if (__result && __state != null && createdThing is Frame) __state.PendingId = createdThing.ThingID; }
        private static void BeforeFrame(Frame __instance, out DrillingRecord __state)
        { __state = MiningGuard.State().Drills.FirstOrDefault(r => r.PendingId == __instance.ThingID && r.MapId == __instance.Map?.uniqueID); }
        private static void AfterFrame(DrillingRecord __state)
        {
            if (__state == null) return;
            var map = Find.Maps.FirstOrDefault(m => m.uniqueID == __state.MapId);
            if (map == null) return;
            var building = new IntVec3(__state.X, 0, __state.Z).GetEdifice(map);
            if (building?.def.defName == __state.Definition && building.TryGetComp<CompDeepDrill>() != null) {
                __state.ThingId = building.ThingID; __state.PendingId = null;
            }
        }
        internal static List<DrillingRecord> Parse(Map map, string value)
        {
            var records = new List<DrillingRecord>();
            foreach (var part in (value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var row = part.Split('/');
                if (row.Length != 5 || !int.TryParse(row[1], out var x) || !int.TryParse(row[2], out var z)
                    || !int.TryParse(row[4], out var target) || target < 0 || target > 100000
                    || DefDatabase<ThingDef>.GetNamedSilentFail(row[0])?.CompDefFor<CompDeepDrill>() == null
                    || DefDatabase<ThingDef>.GetNamedSilentFail(row[3]) == null
                    || !new IntVec3(x, 0, z).InBounds(map) || records.Any(r => r.X == x && r.Z == z))
                    throw new ArgumentException("Invalid or duplicate bounded drilling facility");
                var prior = MiningGuard.State().Drills.FirstOrDefault(r => r.MapId == map.uniqueID && r.X == x && r.Z == z);
                if (prior != null && (prior.Resource != row[3] || prior.Definition != row[0]))
                    throw new ArgumentException("Retained extraction facility requires inspection");
                if (prior == null && new IntVec3(x, 0, z).GetThingList(map).Any(t => t.TryGetComp<CompDeepDrill>() != null))
                    throw new ArgumentException("An existing player drill cannot be adopted by a construction policy");
                var pending = new IntVec3(x, 0, z).GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame)
                    && t.def.entityDefToBuild?.defName == row[0]);
                if (prior == null && pending == null) throw new ArgumentException("A confirmed native construction target is required for drill ownership");
                records.Add(new DrillingRecord { MapId = map.uniqueID, Definition = row[0], X = x, Z = z, Resource = row[3], Target = target,
                    PendingId = pending?.ThingID });
            }
            if (records.Count > 32) throw new ArgumentException("Bounded drilling facility limit exceeded");
            if (MiningGuard.State().Drills.Count + records.Count(r => !MiningGuard.State().Drills.Any(p =>
                p.MapId == r.MapId && p.X == r.X && p.Z == r.Z)) > 256) throw new ArgumentException("Native drilling ownership capacity requires inspection");
            return records;
        }
        internal static void Apply(Map map, List<DrillingRecord> records)
        {
            var retained = MiningGuard.State().Drills;
            foreach (var prior in retained.Where(r => r.MapId == map.uniqueID)) prior.Target = 0;
            foreach (var record in records)
            {
                var prior = retained.FirstOrDefault(r => r.MapId == map.uniqueID && r.X == record.X && r.Z == record.Z);
                if (prior == null) retained.Add(record); else prior.Target = record.Target;
            }
        }
    }
}
