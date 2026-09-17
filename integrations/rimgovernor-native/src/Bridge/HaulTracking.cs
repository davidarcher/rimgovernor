#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Track whole stacks, including stock mixed into them. Protecting the entire
    // mixture is conservative and never assigns surviving units to a lost source.
    internal static class HaulTracking
    {
        private static bool installed;
        private sealed class Transfer
        {
            internal Transfer(Thing? source, Thing destination) { Source = source; Destination = destination; SourceCount = source?.stackCount ?? 0; DestinationCount = destination.stackCount; }
            internal readonly Thing? Source; internal readonly Thing Destination;
            internal readonly int SourceCount, DestinationCount;
        }
        private static readonly List<Transfer> merges = new List<Transfer>();
        private static HaulTrackingState? State(bool create = false)
        {
            if (Current.Game == null) return null;
            var state = Current.Game.GetComponent<HaulTrackingState>();
            if (state == null && create) { state = new HaulTrackingState(Current.Game); Current.Game.components.Add(state); }
            return state;
        }
        internal static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.haul-tracking");
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TryAbsorbStack)),
                prefix: new HarmonyMethod(typeof(HaulTracking), nameof(MergeBegin)),
                finalizer: new HarmonyMethod(typeof(HaulTracking), nameof(MergeEnd)));
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.SplitOff)),
                prefix: new HarmonyMethod(typeof(HaulTracking), nameof(SplitBegin)),
                finalizer: new HarmonyMethod(typeof(HaulTracking), nameof(SplitEnd)));
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.Destroy)),
                prefix: new HarmonyMethod(typeof(HaulTracking), nameof(Destroying)));
            harmony.Patch(AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] {
                typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(HaulTracking), nameof(Spawned)));
            installed = true;
        }
        private static IEnumerable<HaulRecord> Active() => State()?.Records.Where(r => !r.Complete && r.Blocker == null)
            ?? Enumerable.Empty<HaulRecord>();
        private static HaulPortion? Part(HaulRecord r, Thing? t) => t == null ? null : r.Portions.FirstOrDefault(p => p.Id == t.GetUniqueLoadID());
        private static HaulPortion Portion(Thing t) => new HaulPortion { Id = t.GetUniqueLoadID(), Count = t.stackCount, Cached = t };
        internal static string? Begin(Thing? source, Pawn pawn)
        {
            Install();
            if (source?.Map == null || source.stackCount <= 0) return null;
            var state = State(true);
            if (state == null || state.Records.Count >= 512) return null;
            var record = new HaulRecord { Id = Guid.NewGuid().ToString("N"), Source = source.GetUniqueLoadID(),
                Pawn = pawn.GetUniqueLoadID(), MapId = source.Map.uniqueID, Definition = source.def.defName,
                OriginalCount = source.stackCount, RequiredCount = source.stackCount, Started = Find.TickManager.TicksGame };
            record.Portions.Add(Portion(source)); state.Records.Add(record);
            return record.Id;
        }
        internal static HaulRecord? Lookup(string id) => id == null ? null : State()?.Records.FirstOrDefault(r => r.Id == id);
        internal static void Accept(string? id, bool accepted)
        {
            var record = State()?.Records.FirstOrDefault(r => r.Id == id);
            if (record == null) return;
            record.Accepted = accepted;
            if (!accepted) record.Blocker = "Native haul order was not verified";
            else Check(record);
        }
        private static void Check(HaulRecord r)
        {
            if (!r.Accepted || r.Complete || r.Blocker != null) return;
            if (r.Portions.Count == 0 || r.Portions.Count > 128 || r.Portions.Sum(p => p.Count) != r.RequiredCount) {
                r.Blocker = "Tracked quantity is not conserved"; return;
            }
            bool protectedAll = true;
            foreach (var p in r.Portions) {
                var thing = p.Cached;
                if (thing == null) { protectedAll = false; continue; }
                if (thing.Destroyed || thing.stackCount != p.Count || thing.def.defName != r.Definition) {
                    r.Blocker = "Tracked quantity changed outside a verified transfer"; return;
                }
                // IsInValidStorage() alone is the correct native completion
                // signal: it already checks the thing sits somewhere its
                // current slot group's storage settings actually accept it.
                // Requiring the cell to also be Roofed here (as this line
                // used to) makes a legitimate outdoor destination -- e.g.
                // NativeWasteOperations.DirtyCell explicitly REQUIRES
                // !Roofed for a valid dirty dumping stockpile -- impossible
                // to ever observe as Complete: a real live run confirmed the
                // haul physically finished (the item genuinely relocated)
                // while receipts_observe_progress stayed Pending forever.
                protectedAll &= thing.Spawned && thing.Map.uniqueID == r.MapId && thing.IsInValidStorage();
            }
            if (protectedAll) { r.Complete = true; r.CompletedTick = Find.TickManager.TicksGame; }
        }
        private static void MergeBegin(Thing __instance, Thing other, out Transfer? __state)
        {
            __state = null;
            if (!Active().Any(r => Part(r, other) != null || Part(r, __instance) != null)) return;
            __state = new Transfer(other, __instance);
            foreach (var r in Active().ToList()) if (Part(r, other) != null || Part(r, __instance) != null) Check(r);
            merges.Add(__state);
        }
        private static Exception? MergeEnd(Transfer? __state, Exception? __exception)
        {
            if (__state == null) return __exception;
            merges.Remove(__state);
            var s = __state.Source; var d = __state.Destination;
            foreach (var r in Active().ToList()) {
                var source = Part(r, s); var destination = Part(r, d);
                if (source == null && destination == null) continue;
                int moved = d.stackCount - __state.DestinationCount;
                if (__exception != null || s == null || moved < 0 || __state.SourceCount - s.stackCount != moved
                    || source != null && source.Count != __state.SourceCount
                    || destination != null && destination.Count != __state.DestinationCount) {
                    r.Blocker = "Native stack merge did not conserve tracked quantity"; continue;
                }
                if (moved > 0) {
                    if (destination == null) { r.RequiredCount += __state.DestinationCount; r.Portions.Add(Portion(d)); }
                    else { destination.Count = d.stackCount; destination.Cached = d; }
                    if (source == null) r.RequiredCount += moved;
                    else if (s.stackCount == 0) r.Portions.Remove(source);
                    else { source.Count = s.stackCount; source.Cached = s; }
                }
                Check(r);
            }
            return __exception;
        }
        private static void SplitBegin(Thing __instance, out int __state)
        {
            foreach (var r in Active().ToList()) if (Part(r, __instance) != null) Check(r);
            __state = __instance.stackCount;
        }
        private static Exception? SplitEnd(Thing __instance, Thing __result, int __state, Exception __exception)
        {
            foreach (var r in Active().ToList()) {
                var p = Part(r, __instance); if (p == null) continue;
                if (__exception != null || __result == null || p.Count != __state
                    || (__result == __instance ? __instance.stackCount : __instance.stackCount + __result.stackCount) != __state) {
                    r.Blocker = "Native stack split did not conserve tracked quantity"; continue;
                }
                p.Count = __instance.stackCount; p.Cached = __instance;
                if (__result != __instance) r.Portions.Add(Portion(__result));
                Check(r);
            }
            return __exception;
        }
        private static void Destroying(Thing __instance)
        {
            if (merges.Any(m => m.Source == __instance)) return;
            foreach (var r in Active().ToList()) {
                if (Part(r, __instance) == null) continue;
                Check(r);
                if (!r.Complete) r.Blocker = "Tracked stock was destroyed before protected delivery";
            }
        }
        private static void Spawned(Thing __result)
        {
            if (__result == null || merges.Count != 0) return;
            foreach (var r in Active().ToList()) {
                var p = Part(r, __result); if (p == null) continue;
                p.Cached = __result; Check(r);
            }
        }
        internal static object Read(Map map)
        {
            Install();
            var records = State()?.Records.Where(r => r.MapId == map.uniqueID).ToList() ?? new List<HaulRecord>();
            var unresolved = new HashSet<string>(records.Where(r => !r.Complete && r.Blocker == null)
                .SelectMany(r => r.Portions).Where(p => p.Cached == null).Select(p => p.Id));
            var found = new Dictionary<string, Thing>();
            if (unresolved.Count != 0) {
                var things = map.listerThings.AllThings.Concat(map.mapPawns.AllPawnsSpawned.SelectMany(p =>
                    (p.inventory?.innerContainer?.ToList() ?? new List<Thing>()).Concat(
                        p.carryTracker?.CarriedThing == null ? Enumerable.Empty<Thing>() : new[] { p.carryTracker.CarriedThing })));
                foreach (var t in things) if (unresolved.Contains(t.GetUniqueLoadID())) found[t.GetUniqueLoadID()] = t;
            }
            foreach (var r in records.Where(r => !r.Complete && r.Blocker == null)) {
                foreach (var p in r.Portions) if (p.Cached == null && found.TryGetValue(p.Id, out var t)) p.Cached = t;
                Check(r);
            }
            return records.Select(r => new { id = r.Id, source = r.Source, pawn = r.Pawn, definition = r.Definition,
                originalCount = r.OriginalCount, requiredCount = r.RequiredCount, started = r.Started,
                accepted = r.Accepted, complete = r.Complete, completedTick = r.CompletedTick, blocker = r.Blocker,
                portions = r.Portions.Select(p => new { id = p.Id, count = p.Count, resolved = p.Cached != null }).ToList() }).ToList();
        }
    }
}
