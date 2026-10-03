using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Every native Home edit advances this map's freshness token. Autonomous
    /// coverage restores missing cells.
    /// </summary>
    public sealed class HomeCoverageState : MapComponent
    {
        public long Revision;
        public HomeCoverageState(Map map) : base(map) { }
        public override void ExposeData()
        {
            Scribe_Values.Look(ref Revision, "rimgovernorHomeRevision");
        }
    }
}
