using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class MiningRecord : IExposable
    {
        public string ThingId, Resource, Blocker;
        public int MapId, X, Z, Started, Finished = -1, Recovered;
        public bool Cancelled;
        public void ExposeData()
        {
            Scribe_Values.Look(ref ThingId, "thingId"); Scribe_Values.Look(ref Resource, "resource");
            Scribe_Values.Look(ref Blocker, "blocker"); Scribe_Values.Look(ref MapId, "mapId");
            Scribe_Values.Look(ref X, "x"); Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Started, "started"); Scribe_Values.Look(ref Finished, "finished", -1);
            Scribe_Values.Look(ref Recovered, "recovered");
            Scribe_Values.Look(ref Cancelled, "cancelled");
        }
    }

    public sealed class MiningState : GameComponent
    {
        public List<MiningRecord> Records = new List<MiningRecord>();
        public MiningState(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Records, "rimbotMining", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Records == null) Records = new List<MiningRecord>();
        }
    }
}
