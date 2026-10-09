using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Empty stand-in for the removed construction lineage component,
    /// so saves that carry it (the committed tribal8 baseline) load without a
    /// missing-class error. Go matches building intents by geometry now; the
    /// saved records are dropped, not migrated.
    /// </summary>
    public sealed class ConstructionLineageState : GameComponent
    {
        public ConstructionLineageState(Game game) { }
    }
}
