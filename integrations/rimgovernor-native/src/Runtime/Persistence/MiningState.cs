using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class MiningRecord : IExposable
    {
        public string ThingId = "", Definition = "", Resource = "";
        public string? SourceId, Blocker; // SourceId is absent in saves predating source identity.
        public int MapId, X, Z, Started, Finished = -1, Recovered;
        public bool Cancelled, RebindVerified;
        public int SavedHitPoints;
        public void ExposeData()
        {
            Scribe_Values.Look(ref ThingId, "thingId", ""); Scribe_Values.Look(ref Resource, "resource", "");
            Scribe_Values.Look(ref SourceId, "sourceId"); Scribe_Values.Look(ref Definition, "definition", "");
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
        public List<ExcavationRecord> Excavations = new List<ExcavationRecord>();
        // Drills completed from typed constructions the controller admitted (#538).
        public List<BuiltDrillRecord> BuiltDrills = new List<BuiltDrillRecord>();
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
            Scribe_Collections.Look(ref Excavations, "rimgovernorExcavation", LookMode.Deep);
            Scribe_Collections.Look(ref BuiltDrills, "rimgovernorBuiltDrills", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Records == null) Records = new List<MiningRecord>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Drills == null) Drills = new List<DrillingRecord>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Excavations == null) Excavations = new List<ExcavationRecord>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit && BuiltDrills == null) BuiltDrills = new List<BuiltDrillRecord>();
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
            // Excavation is keyed by exact map cell and rock definition, so no
            // thing identity rebind is needed: a cell that no longer holds that
            // rock is finished, a cell whose rock changed is cancelled.
            foreach (var record in Excavations.Where(r => r.Finished < 0 && !r.Cancelled)) {
                var map = Find.Maps.FirstOrDefault(m => m.uniqueID == record.MapId);
                if (map == null) { record.Cancelled = true; continue; }
                var cell = new IntVec3(record.X, 0, record.Z);
                var rock = cell.InBounds(map) ? cell.GetEdifice(map) as Mineable : null;
                if (rock == null) record.Finished = Find.TickManager.TicksGame;
                else if (rock.def.defName != record.Definition) record.Cancelled = true;
            }
        }
    }

    // One admitted excavation cell. Progress is native mining; the guard
    // rechecks excavation safety before each pick hit while the record is open.
    public sealed class ExcavationRecord : IExposable
    {
        public string Definition = "";
        public string? Blocker;
        public int MapId, X, Z, Started, Finished = -1;
        public bool Cancelled;
        public void ExposeData()
        {
            Scribe_Values.Look(ref Definition, "definition", ""); Scribe_Values.Look(ref Blocker, "blocker");
            Scribe_Values.Look(ref MapId, "mapId"); Scribe_Values.Look(ref X, "x"); Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Started, "started"); Scribe_Values.Look(ref Finished, "finished", -1);
            Scribe_Values.Look(ref Cancelled, "cancelled");
        }
    }

    // Ownership evidence for one controller-built drill: the exact thing id,
    // definition and cell bind across save/load (building ids are stable), so a
    // player drill or a rebuilt drill at the same cell is never adopted.
    public sealed class BuiltDrillRecord : IExposable
    {
        public string ThingId = "", Definition = "";
        public int MapId, X, Z, Built;
        public void ExposeData()
        {
            Scribe_Values.Look(ref ThingId, "thingId", ""); Scribe_Values.Look(ref Definition, "definition", "");
            Scribe_Values.Look(ref MapId, "mapId"); Scribe_Values.Look(ref X, "x"); Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Built, "built");
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
