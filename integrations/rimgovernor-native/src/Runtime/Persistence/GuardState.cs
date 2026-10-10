using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // The open guarded designations: which designation a Designate
    // placed under which named guard. The guard's own rule is native code;
    // this is only the membership the job hooks consult. It follows the save
    // so a reload keeps the designation guarded.
    public sealed class GuardState : GameComponent
    {
        public List<GuardedDesignation> Records = new List<GuardedDesignation>();
        public GuardState(Game game) { }
        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) Records.RemoveAll(r => !r.Open);
            Scribe_Collections.Look(ref Records, "rimgovernorGuardedDesignation", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Records == null) Records = new List<GuardedDesignation>();
        }
    }

    // One guarded designation: the guard, the designation def, the map cell
    // and, for a thing designation, the exact thing. A Mine record is keyed by
    // cell and rock definition (compressed rock is recreated with fresh ids
    // on load). Finished marks the observed removal; Cancelled a designation
    // removed, replaced or released before it.
    public sealed class GuardedDesignation : IExposable
    {
        public string Id = "", Guard = "", Designation = "", ExpectedDef = "";
        public string? ThingId, WallStuff, ReplacementId, Blocker;
        public int MapId, X, Z, Finished = -1;
        public bool Cancelled;
        // The wall_upgrade guard's site: the wall-upgrade geometry and
        // identities its re-check holds the demolition to.
        public WallRemovalRecord? Wall;
        public bool Open => Finished < 0 && !Cancelled;
        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id", ""); Scribe_Values.Look(ref Guard, "guard", "");
            Scribe_Values.Look(ref Designation, "designation", ""); Scribe_Values.Look(ref ExpectedDef, "expectedDef", "");
            Scribe_Values.Look(ref ThingId, "thingId"); Scribe_Values.Look(ref WallStuff, "wallStuff");
            Scribe_Values.Look(ref ReplacementId, "replacementId"); Scribe_Values.Look(ref Blocker, "blocker");
            Scribe_Values.Look(ref MapId, "mapId"); Scribe_Values.Look(ref X, "x"); Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Finished, "finished", -1); Scribe_Values.Look(ref Cancelled, "cancelled");
            Scribe_Deep.Look(ref Wall, "wallUpgrade");
        }
    }

    // A wall-upgrade site: target, the original wall, its left and right
    // supports, the completed backups or the permanent wall, and the material.
    public sealed class WallRemovalRecord : IExposable
    {
        public string Id = "", Target = "", Original = "", Left = "", Right = "";
        public string? Permanent, Material, Load, Blocker;
        public List<string> Backup = new List<string>();
        public int MapId, X, Z, Nx, Nz;
        public long UiRevision;
        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id", ""); Scribe_Values.Look(ref Target, "target", "");
            Scribe_Values.Look(ref Original, "original", ""); Scribe_Values.Look(ref Left, "left", "");
            Scribe_Values.Look(ref Right, "right", ""); Scribe_Values.Look(ref Permanent, "permanent");
            Scribe_Values.Look(ref Material, "material");
            Scribe_Values.Look(ref Load, "load"); Scribe_Values.Look(ref Blocker, "blocker");
            Scribe_Values.Look(ref MapId, "mapId"); Scribe_Values.Look(ref X, "x"); Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Nx, "nx"); Scribe_Values.Look(ref Nz, "nz");
            Scribe_Values.Look(ref UiRevision, "uiRevision");
            Scribe_Collections.Look(ref Backup, "backup", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Backup == null) Backup = new List<string>();
        }
    }
}
