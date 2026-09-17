#nullable enable

using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal static class MiningGuard
    {
        private static bool patched;
        internal static MiningState State()
        {
            var state = Current.Game.GetComponent<MiningState>();
            if (state == null) { state = new MiningState(Current.Game); Current.Game.components.Add(state); }
            return state;
        }
        internal static void Install()
        {
            if (patched) return;
            var method = AccessTools.Method(typeof(JobDriver_Mine), "DoDamage");
            if (method == null) throw new InvalidOperationException("Native mining damage contract unavailable");
            new Harmony("rimgovernor.mining-safety").Patch(method,
                prefix: new HarmonyMethod(typeof(MiningGuard), nameof(Before)),
                postfix: new HarmonyMethod(typeof(MiningGuard), nameof(After)));
            new Harmony("rimgovernor.mining-safety").Patch(AccessTools.Method(typeof(DesignationManager), "RemoveDesignation"),
                postfix: new HarmonyMethod(typeof(MiningGuard), nameof(Removed)));
            patched = true;
        }
        private static void Removed(DesignationManager __instance, Designation des)
        {
            if (des.def != DesignationDefOf.Mine || Current.Game == null) return;
            foreach (var record in State().Records.Where(r => r.MapId == __instance.map.uniqueID
                && r.X == des.target.Cell.x && r.Z == des.target.Cell.z && r.Finished < 0)) record.Cancelled = true;
            foreach (var record in State().Excavations.Where(r => r.MapId == __instance.map.uniqueID
                && r.X == des.target.Cell.x && r.Z == des.target.Cell.z && r.Finished < 0)) record.Cancelled = true;
        }
        internal static ExcavationRecord OpenExcavation(Map map, IntVec3 cell) => State().Excavations
            .FirstOrDefault(r => r.MapId == map.uniqueID && r.X == cell.x && r.Z == cell.z && r.Finished < 0 && !r.Cancelled);
        private sealed class Sample
        {
            internal Sample(Map map, ExcavationRecord excavation) { Map = map; Excavation = excavation; }
            internal Sample(Map map, MiningRecord record, int stock) { Map = map; Record = record; Stock = stock; }
            internal readonly MiningRecord? Record;
            internal readonly ExcavationRecord? Excavation;
            internal readonly Map Map;
            internal readonly int Stock;
        }
        private static int Stock(Map map, string resource) => map.listerThings.AllThings
            .Where(t => t.def.defName == resource).Sum(t => t.stackCount);
        private static bool Before(JobDriver_Mine __instance, Thing target, out Sample? __state)
        {
            __state = null;
            if (target?.Map == null) return true;
            var excavation = OpenExcavation(target.Map, target.Position);
            if (excavation != null)
            {
                // Staged excavation: roofed cells are expected; recheck the
                // cell rule and counterfactual support for this one removal.
                excavation.Blocker = ExcavationTools.CellBlocker(target.Position, target.Map);
                if (excavation.Blocker == null && ExcavationSafety.Check(target.Map, new[] { target.Position }, out _, out var support) != ExcavationSafety.Support.Supported)
                    excavation.Blocker = support;
                if (excavation.Blocker != null) { __instance.EndJobWith(JobCondition.Incompletable); return false; }
                __state = new Sample(target.Map, excavation);
                return true;
            }
            var record = State().Records.FirstOrDefault(r => r.MapId == target.Map.uniqueID && r.ThingId == target.ThingID && r.Finished < 0 && !r.Cancelled);
            if (record == null) return true;
            record.Blocker = ResourceAcquisitionTools.MiningBlocker(target, target.Map);
            if (record.Blocker != null)
            {
                __instance.EndJobWith(JobCondition.Incompletable);
                return false;
            }
            __state = new Sample(target.Map, record, Stock(target.Map, record.Resource));
            return true;
        }
        private static void After(Thing target, Sample? __state)
        {
            if (__state == null || !target.Destroyed) return;
            if (__state.Excavation != null) { __state.Excavation.Finished = Find.TickManager.TicksGame; return; }
            var record = __state.Record;
            if (record == null) return;
            record.Finished = Find.TickManager.TicksGame;
            record.Cancelled = false;
            record.Recovered = Math.Max(0, Stock(__state.Map, record.Resource) - __state.Stock);
        }
    }
}
