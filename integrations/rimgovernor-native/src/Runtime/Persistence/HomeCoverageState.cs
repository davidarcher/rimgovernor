using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class HomeCoverageState : MapComponent
    {
        public bool Initialized;
        public long Revision;
        public HomeCoverageState(Map map) : base(map) { }
        public override void ExposeData()
        {
            Scribe_Values.Look(ref Initialized, "rimgovernorHomeInitialized");
            Scribe_Values.Look(ref Revision, "rimgovernorHomeRevision");
        }
    }
}
