#nullable enable

using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class CellTracking
    {
        // Indoors is CellState.indoors as the cells read computes it.
        internal static bool Indoors(Room? room) => room != null && room.ProperRoom && !room.PsychologicallyOutdoors;
    }
}
