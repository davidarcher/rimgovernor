#nullable enable
using Verse;

namespace HomeBridge.BridgeTools
{
    // The one Danger level for reach checks on a player-style forced order.
    // Vanilla's float menu (FloatMenuOptionProvider_WorkGivers) tests every
    // work-giver order with CanReach(target, PathEndMode, Danger.Deadly): a
    // forced order may cross danger the automatic scanner
    // (WorkGiver_Scanner.MaxPathDanger -> pawn.NormalMaxDanger()) avoids. An
    // order its own work giver validates needs no stricter pre-veto.
    internal static class NativeOrderDanger
    {
        internal const Danger OrderDanger = Danger.Deadly;
    }
}
