using Verse;

namespace HomeBridge.BridgeTools
{
    // Load shell only: the ProductionPolicy planner and its native push are gone
    // (#875), but committed and cached saves still carry this component, so the
    // class stays for them to load. It holds nothing.
    public sealed class ProductionPolicyState : GameComponent
    {
        public ProductionPolicyState(Game game) { }
    }
}
