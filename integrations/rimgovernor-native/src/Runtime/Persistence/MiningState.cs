using System.Collections.Generic;
using System.Linq;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class MiningRecord : IExposable
    {
        public string ThingId, SourceId, Definition, Resource, Blocker;
        public int MapId, X, Z, Started, Finished = -1, Recovered;
        public bool Cancelled, RebindVerified;
        public int SavedHitPoints;
        public void ExposeData()
        {
            Scribe_Values.Look(ref ThingId, "thingId"); Scribe_Values.Look(ref Resource, "resource");
            Scribe_Values.Look(ref SourceId, "sourceId"); Scribe_Values.Look(ref Definition, "definition");
            Scribe_Values.Look(ref RebindVerified, "rebindVerified"); Scribe_Values.Look(ref SavedHitPoints, "savedHitPoints");
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
        public List<DrillingRecord> Drills = new List<DrillingRecord>();
        public MiningState(Game game) { }
        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
                foreach (var record in Records.Where(r => r.Finished < 0)) {
                    var map = Find.Maps.FirstOrDefault(m => m.uniqueID == record.MapId);
                    var source = map?.listerThings.AllThings.FirstOrDefault(t => t.ThingID == record.ThingId
                        && t.Position.x == record.X && t.Position.z == record.Z && t.def.defName == record.Definition);
                    record.RebindVerified = source != null;
                    if (source != null) record.SavedHitPoints = source.HitPoints;
                }
            Scribe_Collections.Look(ref Records, "rimgovernorMining", LookMode.Deep);
            Scribe_Collections.Look(ref Drills, "rimgovernorDrilling", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Records == null) Records = new List<MiningRecord>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Drills == null) Drills = new List<DrillingRecord>();
        }
        public override void FinalizeInit()
        {
            // Native compressed mineables are recreated with fresh IDs on load.
            // Bind only to the exact source verified when this save was written.
            foreach (var record in Records.Where(r => r.Finished < 0 && r.RebindVerified)) {
                var map = Find.Maps.FirstOrDefault(m => m.uniqueID == record.MapId);
                var source = map?.listerThings.AllThings.FirstOrDefault(t => t.Position.x == record.X
                    && t.Position.z == record.Z && t.def.defName == record.Definition);
                if (source != null && source.def.defName == record.Definition && source.HitPoints == record.SavedHitPoints)
                    record.ThingId = source.ThingID;
                else record.Cancelled = true;
            }
        }
    }

    public sealed class DrillingRecord : IExposable
    {
        public string Definition, Resource, ThingId, PendingId;
        public int MapId, X, Z, Recovered;
        public int Target; // Re-admitted before each supervised lease; never restored as authority.
        public void ExposeData()
        {
            Scribe_Values.Look(ref Definition, "definition"); Scribe_Values.Look(ref Resource, "resource");
            Scribe_Values.Look(ref ThingId, "thingId"); Scribe_Values.Look(ref MapId, "mapId");
            Scribe_Values.Look(ref PendingId, "pendingId");
            Scribe_Values.Look(ref X, "x"); Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Recovered, "recovered");
        }
    }
}
