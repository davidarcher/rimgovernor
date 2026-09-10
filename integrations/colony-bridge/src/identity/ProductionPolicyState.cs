using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Map-scoped policy metadata; vanilla bills and their player settings remain owned by RimWorld.
    public sealed class ProductionPolicyState : GameComponent
    {
        public Dictionary<string, int> Floors = new Dictionary<string, int>();
        public Dictionary<string, int> Commitments = new Dictionary<string, int>();
        public List<string> Stopped = new List<string>();
        public ProductionPolicyState(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Floors, "rimgovernorProductionFloors", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref Stopped, "rimgovernorProductionStopped", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (Floors == null) Floors = new Dictionary<string, int>();
                if (Stopped == null) Stopped = new List<string>();
            }
        }
    }
}
