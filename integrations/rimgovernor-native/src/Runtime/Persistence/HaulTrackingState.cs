using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Empty stand-in for the removed haul bookkeeping component (#1355), so
    /// saves that carry it (the committed tribal8 baseline) load without a
    /// missing-class error. Its saved records are dropped, not migrated.
    /// </summary>
    public sealed class HaulTrackingState : GameComponent
    {
        public HaulTrackingState(Game game) { }
    }
}
