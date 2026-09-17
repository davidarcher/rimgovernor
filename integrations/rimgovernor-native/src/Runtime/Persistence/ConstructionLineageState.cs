#nullable disable // Legacy Scribe state predating nullable enforcement; annotate and remove per #85.
using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class ConstructionLineageRecord : IExposable
    {
        public string Origin, Current, Definition, Stuff, Stage, Blocker;
        public int MapId, X, Z, Rotation, Started, Failures;
        public void ExposeData()
        {
            Scribe_Values.Look(ref Origin, "origin"); Scribe_Values.Look(ref Current, "current");
            Scribe_Values.Look(ref Definition, "definition"); Scribe_Values.Look(ref Stuff, "stuff");
            Scribe_Values.Look(ref Stage, "stage"); Scribe_Values.Look(ref Blocker, "blocker");
            Scribe_Values.Look(ref MapId, "mapId"); Scribe_Values.Look(ref X, "x"); Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Rotation, "rotation"); Scribe_Values.Look(ref Started, "started");
            Scribe_Values.Look(ref Failures, "failures");
        }
    }

    public sealed class ConstructionLineageState : GameComponent
    {
        public List<ConstructionLineageRecord> Records = new List<ConstructionLineageRecord>();
        public ConstructionLineageState(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Records, "rimgovernorConstructionLineage", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Records == null) Records = new List<ConstructionLineageRecord>();
        }
    }
}
