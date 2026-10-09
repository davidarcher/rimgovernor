#nullable enable
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// Vanilla hauls a chunk only under a Haul designation (alwaysHaulable is
    /// false on the chunk defs), so a stored-nowhere chunk never draws a
    /// hauler. Setting the flag once on the defs lets ordinary hauling carry
    /// every chunk to a stockpile that takes it (#2513); no per-haul patch.
    internal static class ChunkAutoHaul
    {
        private static bool installed;
        internal static void Install()
        {
            if (installed) return;
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                if (def.category == ThingCategory.Item && def.IsWithinCategory(ThingCategoryDefOf.Chunks)) def.alwaysHaulable = true;
            installed = true;
        }
    }
}
