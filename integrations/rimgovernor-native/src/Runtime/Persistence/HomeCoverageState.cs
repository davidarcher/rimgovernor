using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Per-map Home coverage ledger: whether the controller has observed the
    /// map (Initialized), the revision every Home edit advances, and the
    /// exclusion set (#314): cells a player removed from Home (Set false,
    /// Clear, Invert) after observation began, cleared again for a cell
    /// anyone sets back to Home. MaintainHomeCoverage never re-adds an
    /// excluded cell.
    /// </summary>
    public sealed class HomeCoverageState : MapComponent
    {
        public bool Initialized;
        public long Revision;
        public HashSet<IntVec3> Excluded = new HashSet<IntVec3>();
        private List<IntVec3>? excludedScribe;
        public HomeCoverageState(Map map) : base(map) { }
        public override void ExposeData()
        {
            Scribe_Values.Look(ref Initialized, "rimgovernorHomeInitialized");
            Scribe_Values.Look(ref Revision, "rimgovernorHomeRevision");
            if (Scribe.mode == LoadSaveMode.Saving) excludedScribe = new List<IntVec3>(Excluded);
            Scribe_Collections.Look(ref excludedScribe, "rimgovernorHomeExcluded", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Excluded = excludedScribe == null ? new HashSet<IntVec3>() : new HashSet<IntVec3>(excludedScribe);
                excludedScribe = null;
            }
        }
    }
}
