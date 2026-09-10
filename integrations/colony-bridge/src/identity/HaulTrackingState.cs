using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class HaulPortion : IExposable
    {
        public string Id;
        public int Count;
        public Thing Cached;
        public void ExposeData() { Scribe_Values.Look(ref Id, "id"); Scribe_Values.Look(ref Count, "count"); }
    }
    public sealed class HaulRecord : IExposable
    {
        public string Id, Source, Pawn, Definition, Blocker;
        public int MapId, OriginalCount, RequiredCount, Started, CompletedTick;
        public bool Accepted, Complete;
        public List<HaulPortion> Portions = new List<HaulPortion>();
        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id"); Scribe_Values.Look(ref Source, "source");
            Scribe_Values.Look(ref Pawn, "pawn"); Scribe_Values.Look(ref Definition, "definition");
            Scribe_Values.Look(ref Blocker, "blocker"); Scribe_Values.Look(ref MapId, "mapId");
            Scribe_Values.Look(ref OriginalCount, "originalCount"); Scribe_Values.Look(ref RequiredCount, "requiredCount");
            Scribe_Values.Look(ref Started, "started"); Scribe_Values.Look(ref CompletedTick, "completedTick");
            Scribe_Values.Look(ref Accepted, "accepted"); Scribe_Values.Look(ref Complete, "complete");
            Scribe_Collections.Look(ref Portions, "portions", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Portions == null) Portions = new List<HaulPortion>();
        }
    }
    public sealed class HaulTrackingState : GameComponent
    {
        public List<HaulRecord> Records = new List<HaulRecord>();
        public HaulTrackingState(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Records, "rimbotHaulTracking", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Records == null) Records = new List<HaulRecord>();
        }
    }
}
