#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Identity evidence only. A controller must separately prove plan ownership,
    // current direction and native safety before modifying an observed building.
    internal static class ConstructionLineage
    {
        private static bool installed;
        private sealed class Completion
        {
            internal Completion(ConstructionLineageRecord record, Frame frame, string stage) { Record = record; Frame = frame; Stage = stage; }
            internal readonly ConstructionLineageRecord Record;
            internal readonly Frame Frame;
            internal readonly string Stage;
            internal readonly List<Thing> Spawned = new List<Thing>();
        }
        private static readonly List<Completion> completions = new List<Completion>();
        private static string? StuffOf(Thing thing) => thing is Blueprint_Build blueprint
            ? blueprint.EntityToBuildStuff()?.defName : thing.Stuff?.defName;
        private static ConstructionLineageState? State(bool create = false)
        {
            if (Current.Game == null) return null;
            var state = Current.Game.GetComponent<ConstructionLineageState>();
            if (state == null && create) {
                state = new ConstructionLineageState(Current.Game);
                Current.Game.components.Add(state);
            }
            return state;
        }
        internal static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.construction-lineage");
            harmony.Patch(AccessTools.Method(typeof(Blueprint_Build), "MakeSolidThing"),
                postfix: new HarmonyMethod(typeof(ConstructionLineage), nameof(FrameCreated)));
            harmony.Patch(AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction)),
                prefix: new HarmonyMethod(typeof(ConstructionLineage), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(ConstructionLineage), nameof(Finish)));
            harmony.Patch(AccessTools.Method(typeof(Frame), nameof(Frame.FailConstruction)),
                prefix: new HarmonyMethod(typeof(ConstructionLineage), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(ConstructionLineage), nameof(Finish)));
            harmony.Patch(AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] {
                typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(ConstructionLineage), nameof(Spawned)));
            installed = true;
        }
        internal static string? Register(Thing placed, BuildableDef definition, string? key = null)
        {
            if (!(definition is ThingDef) || placed?.Map == null) return null;
            var state = State(true);
            if (state == null) return null;
            if (state.Records.Count >= 4096) Prune(state);
            if (state.Records.Count >= 4096) return null;
            var id = placed.GetUniqueLoadID();
            state.Records.Add(new ConstructionLineageRecord { Origin = id, Current = id,
                Definition = definition.defName, Stuff = StuffOf(placed),
                MapId = placed.Map.uniqueID, X = placed.Position.x, Z = placed.Position.z,
                Rotation = placed.Rotation.AsInt, Started = Find.TickManager.TicksGame,
                Stage = placed is Blueprint ? "blueprint" : placed is Frame ? "frame" : "built", Key = key });
            return id;
        }

        // Prune drops the records whose current object is gone from every
        // loaded map (destroyed, cancelled or on an abandoned map).
        private static void Prune(ConstructionLineageState state)
        {
            var live = new HashSet<string>();
            foreach (var map in Find.Maps)
                foreach (var thing in map.listerThings.AllThings)
                    live.Add(thing.GetUniqueLoadID());
            state.Records.RemoveAll(r => !live.Contains(r.Current));
        }

        // Claim stamps an intent key on a blueprint, frame or building that
        // already matches the intent: an unkeyed lineage takes the key, a
        // keyed one keeps its first owner, and an untracked thing starts one.
        internal static void Claim(Thing thing, BuildableDef definition, string key)
        {
            var state = State(true);
            if (state == null || thing.Map == null) return;
            var id = thing.GetUniqueLoadID();
            var record = state.Records.FirstOrDefault(r => r.Current == id && r.MapId == thing.Map.uniqueID);
            if (record == null) { Register(thing, definition, key); return; }
            if (string.IsNullOrEmpty(record.Key)) record.Key = key;
        }

        // Keys maps each built building's load id to the intent key that
        // placed it, for the building census (BuildingState.intent_key).
        internal static Dictionary<string, string> Keys(Map map)
        {
            var keys = new Dictionary<string, string>();
            foreach (var r in State()?.Records ?? new List<ConstructionLineageRecord>())
                if (r.MapId == map.uniqueID && r.Stage == "built" && r.Blocker == null && !string.IsNullOrEmpty(r.Key))
                    keys[r.Current] = r.Key!;
            return keys;
        }
        // OpenIntents lists the keyed, unblocked lineages still standing as a
        // blueprint or frame on the map.
        internal static List<(string Key, string Stage)> OpenIntents(Map map)
        {
            var open = new List<(string Key, string Stage)>();
            var live = new HashSet<string>(map.listerThings.AllThings.Select(t => t.GetUniqueLoadID()));
            foreach (var r in State()?.Records ?? new List<ConstructionLineageRecord>())
                if (r.MapId == map.uniqueID && (r.Stage == "blueprint" || r.Stage == "frame") && r.Blocker == null && !string.IsNullOrEmpty(r.Key) && live.Contains(r.Current))
                    open.Add((r.Key!, r.Stage));
            return open;
        }
        private static void FrameCreated(Blueprint_Build __instance, Thing __result)
        {
            var record = State()?.Records.FirstOrDefault(r => r.Current == __instance.GetUniqueLoadID() && r.Stage == "blueprint");
            if (record == null) return;
            if (__result is Frame && __result.def.entityDefToBuild?.defName == record.Definition) {
                record.Current = __result.GetUniqueLoadID();
                record.Stage = "frame";
            } else record.Blocker = "Native blueprint transition was not a matching frame";
        }
        private static void Begin(Frame __instance, MethodBase __originalMethod, out Completion? __state)
        {
            __state = null;
            var record = State()?.Records.FirstOrDefault(r => r.Current == __instance.GetUniqueLoadID()
                && r.Stage == "frame" && r.Blocker == null);
            if (record == null) return;
            __state = new Completion(record, __instance,
                __originalMethod.Name == nameof(Frame.FailConstruction) ? "blueprint" : "built");
            completions.Add(__state);
        }
        private static void Spawned(Thing __result)
        {
            if (__result?.Map == null || __result is Frame) return;
            foreach (var scope in completions) {
                var r = scope.Record;
                var definition = scope.Stage == "blueprint" && __result is Blueprint_Build
                    ? __result.def.entityDefToBuild?.defName : scope.Stage == "built" && __result is Building ? __result.def.defName : null;
                if (__result.Map.uniqueID == r.MapId && definition == r.Definition
                    && __result.Position.x == r.X && __result.Position.z == r.Z
                    && StuffOf(__result) == r.Stuff && __result.Rotation.AsInt == r.Rotation)
                    scope.Spawned.Add(__result);
            }
        }
        private static void Finish(Completion? __state, Exception? __exception)
        {
            if (__state == null) return;
            completions.Remove(__state);
            if (__state.Record.Current != __state.Frame.GetUniqueLoadID()) return;
            var matches = __state.Spawned.Distinct().ToList();
            if (__exception == null && __state.Frame.Destroyed && matches.Count == 1 && matches[0].Spawned) {
                __state.Record.Current = matches[0].GetUniqueLoadID();
                __state.Record.Stage = __state.Stage;
                if (__state.Stage == "blueprint") __state.Record.Failures++;
            } else __state.Record.Blocker = "Native construction completion identity is unavailable or ambiguous";
        }
        internal static object Read(Map map)
        {
            var records = (State()?.Records ?? new List<ConstructionLineageRecord>()).Where(r => r.MapId == map.uniqueID).ToList();
            if (records.Count == 0) return new List<object>();
            var ids = new HashSet<string>(records.Select(r => r.Current));
            var current = map.listerThings.AllThings.Where(t => ids.Contains(t.GetUniqueLoadID())).ToDictionary(t => t.GetUniqueLoadID());
            return records
                .Select(r => new { origin = r.Origin, current = r.Current, definition = r.Definition, stuff = r.Stuff,
                    x = r.X, z = r.Z, rotation = r.Rotation, stage = r.Stage, started = r.Started, failures = r.Failures, blocker = r.Blocker,
                    present = current.TryGetValue(r.Current, out var t) && t.Position.x == r.X && t.Position.z == r.Z
                        && t.Faction == Faction.OfPlayerSilentFail && StuffOf(t) == r.Stuff
                        && t.Rotation.AsInt == r.Rotation && (r.Stage == "built" ? t.def.defName : t.def.entityDefToBuild?.defName) == r.Definition
                }).ToList();
        }
    }
}
