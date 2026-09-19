#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Stateless, inclusive tick deltas. Only removals need retention; a
    // caller older than that retention must replace its held census.
    internal sealed class ZoneTracking
    {
        internal const int Window = 2500;
        internal const string Expired = "entity_tombstone_window_expired";
        internal const string BeforeTracking = "entity_tracking_not_available";
        private static readonly ConditionalWeakTable<Map, ZoneTracking> Maps = new ConditionalWeakTable<Map, ZoneTracking>();
        private static bool installed;
        private readonly Dictionary<string, int> lastChangedByEntityId = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly LinkedList<(string id, int removedTick)> tombstones = new LinkedList<(string, int)>();
        private readonly Dictionary<string, Obs.ZoneState> shadows = new Dictionary<string, Obs.ZoneState>(StringComparer.Ordinal);
        internal readonly int Since;

        private ZoneTracking(int since) { Since = since; Install(); }
        internal static void Initialize(Map map, int since) => Maps.GetValue(map, _ => new ZoneTracking(since));
        internal static ZoneTracking For(Map map) { CellTracking.For(map); return Maps.GetValue(map, _ => new ZoneTracking(Find.TickManager.TicksGame - 1)); }
        internal string? Refusal(long since)
        {
            Prune();
            if (since <= 0) return null;
            if (since < Find.TickManager.TicksGame - Window) return Expired;
            return since < Since ? BeforeTracking : null;
        }
        private void Prune()
        {
            while (tombstones.First != null && tombstones.First.Value.removedTick < Find.TickManager.TicksGame - Window)
                tombstones.RemoveFirst();
        }
        private void Bump(Zone zone) => lastChangedByEntityId[zone.GetUniqueLoadID()] = Find.TickManager.TicksGame;
        private void Remove(Zone zone)
        {
            var id = zone.GetUniqueLoadID();
            lastChangedByEntityId.Remove(id);
            shadows.Remove(id);
            Prune();
            tombstones.AddLast((id, Find.TickManager.TicksGame));
        }
        internal IEnumerable<string> Removed(long since) => tombstones.Where(t => t.removedTick >= since && !lastChangedByEntityId.ContainsKey(t.id)).Select(t => t.id).Distinct();
        internal bool Unchanged(Zone zone, Obs.ZoneState row, long since)
        {
            // Plant growth, room enclosure, pawn diets and directly assigned
            // zone settings have no zone event. Compare the projected facts
            // on a read, excluding the context-stamped CAS reference.
            var shadow = row.Clone();
            shadow.Snapshot = null;
            var id = zone.GetUniqueLoadID();
            if (shadows.TryGetValue(id, out var old) && !old.Equals(shadow)) Bump(zone);
            else if (!shadows.ContainsKey(id)) Bump(zone);
            shadows[id] = shadow;
            return since > 0 && lastChangedByEntityId[id] < since;
        }
        private static void Install()
        {
            if (installed) return;
            installed = true;
            var harmony = new Harmony("rimgovernor.entity-tracking");
            harmony.Patch(AccessTools.Method(typeof(ZoneManager), "RegisterZone"), postfix: new HarmonyMethod(typeof(ZoneTracking), nameof(Registered)));
            harmony.Patch(AccessTools.Method(typeof(ZoneManager), "DeregisterZone"), postfix: new HarmonyMethod(typeof(ZoneTracking), nameof(RemovedZone)));
            harmony.Patch(AccessTools.Method(typeof(Zone), "AddCell"), postfix: new HarmonyMethod(typeof(ZoneTracking), nameof(Changed)));
            harmony.Patch(AccessTools.Method(typeof(Zone), "RemoveCell"), postfix: new HarmonyMethod(typeof(ZoneTracking), nameof(Changed)));
        }
        private static void Registered(ZoneManager __instance, Zone __0)
        { if (Maps.TryGetValue(__instance.map, out var tracker)) tracker.Bump(__0); }
        private static void RemovedZone(ZoneManager __instance, Zone __0)
        { if (Maps.TryGetValue(__instance.map, out var tracker)) tracker.Remove(__0); }
        private static void Changed(Zone __instance)
        {
            if (__instance.Map != null && Maps.TryGetValue(__instance.Map, out var tracker))
            {
                if (__instance.Cells.Count == 0) tracker.Remove(__instance);
                else tracker.Bump(__instance);
            }
        }
    }
}
