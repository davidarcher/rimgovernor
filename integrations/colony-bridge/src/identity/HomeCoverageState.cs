using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class HomeCoverageState : MapComponent
    {
        public BoolGrid Excluded;
        public bool Initialized;
        public long Revision;
        public HomeCoverageState(Map map) : base(map) { Excluded = new BoolGrid(map); }
        public override void ExposeData()
        {
            Scribe_Deep.Look(ref Excluded, "rimbotHomeExcluded");
            Scribe_Values.Look(ref Initialized, "rimbotHomeInitialized");
            Scribe_Values.Look(ref Revision, "rimbotHomeRevision");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Excluded == null) Excluded = new BoolGrid(map);
        }
    }
}
