#nullable enable

using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // A drawn-down store is not refilled item by item (#2519). Vanilla sizes a
    // haul by the room left, so a nearly full stockpile, shelf or bench input
    // store draws a stream of 1-3 item hauls. Under supervision a pawn's
    // haul-to-storage job is withheld until the destination group has room for a
    // stack of the item. A haul that carries the whole source stack is kept.
    // Only the pawn job is patched: the native reads of
    // StoreUtility.TryFindBestBetterStoreCellFor are untouched. Container hauls
    // and the callers of HaulToCellStorageJob that skip HaulToStorageJob
    // (opportunistic hauling, bill-product storing) are out of scope.
    internal static class StorageRefillHysteresis
    {
        private static bool patched;
        internal static void Install()
        {
            if (patched) return;
            new Harmony("rimgovernor.storage-refill-hysteresis").Patch(
                AccessTools.Method(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToStorageJob)),
                postfix: new HarmonyMethod(typeof(StorageRefillHysteresis), nameof(Refill)));
            patched = true;
        }

        // Room under a stack, and not enough to take the whole source stack.
        internal static bool Withheld(int room, int stackLimit, int sourceCount) =>
            room < stackLimit && room < sourceCount;

        private static void Refill(Pawn p, Thing t, ref Job? __result)
        {
            if (__result == null || !Supervisor.IsActive || __result.def != JobDefOf.HaulToCell) return;
            var map = p.Map;
            ISlotGroup? group = map.haulDestinationManager.SlotGroupAt(__result.targetB.Cell);
            if (group == null) return;
            ISlotGroup? shared = group.StorageGroup;
            var cells = (shared ?? group).CellsList;
            var stack = t.def.stackLimit;
            int room = 0;
            // Stops once a stack fits, so the scan an idle hauler repeats stays short.
            for (int i = 0; i < cells.Count && room < stack; i++)
                if (StoreUtility.IsGoodStoreCell(cells[i], map, t, p, p.Faction))
                    room += cells[i].GetItemStackSpaceLeftFor(map, t.def);
            if (!Withheld(room, stack, t.stackCount)) return;
            JobFailReason.Is("storage waits for room for a full stack");
            __result = null;
        }
    }
}
