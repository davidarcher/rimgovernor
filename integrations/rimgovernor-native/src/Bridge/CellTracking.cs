#nullable enable

using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class CellTracking
    {
        // Indoors is the cell grid's indoors as the grid read computes it.
        internal static bool Indoors(Room? room) => room != null && room.ProperRoom && !room.PsychologicallyOutdoors;
    }
}
