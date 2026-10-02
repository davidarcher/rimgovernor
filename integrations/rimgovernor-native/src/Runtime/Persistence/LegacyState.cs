using Verse;

namespace HomeBridge.BridgeTools
{
    // Old saves list these components (folded into GuardState/DrillingState in #1351).
    // The classes stay as empty shells so the loader resolves their nodes and discards
    // the contents instead of logging "Could not find class".
    public sealed class MiningState : GameComponent
    {
        public MiningState(Game game) { }
    }

    public sealed class WallRemovalState : GameComponent
    {
        public WallRemovalState(Game game) { }
    }
}
