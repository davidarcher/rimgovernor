using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    // The bounded deep drills (DrillingGuard): which drill the controller
    // placed and the target it may drill to.
    public sealed class DrillingState : GameComponent
    {
        public List<DrillingRecord> Drills = new List<DrillingRecord>();
        public DrillingState(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Drills, "rimgovernorDrilling", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Drills == null) Drills = new List<DrillingRecord>();
        }
    }

    public sealed class DrillingRecord : IExposable
    {
        public string Definition = "", Resource = "";
        public string? ThingId, PendingId; // Built drill, or the blueprint/frame still pending; either may be unknown.
        public int MapId, X, Z, Recovered;
        public int Target; // Re-admitted before each supervised lease; never restored as authority.
        public void ExposeData()
        {
            Scribe_Values.Look(ref Definition, "definition", ""); Scribe_Values.Look(ref Resource, "resource", "");
            Scribe_Values.Look(ref ThingId, "thingId"); Scribe_Values.Look(ref MapId, "mapId");
            Scribe_Values.Look(ref PendingId, "pendingId");
            Scribe_Values.Look(ref X, "x"); Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Recovered, "recovered");
        }
    }
}
