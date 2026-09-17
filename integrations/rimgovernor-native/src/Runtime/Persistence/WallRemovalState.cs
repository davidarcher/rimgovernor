#nullable disable // Legacy Scribe state predating nullable enforcement; annotate and remove per #85.
using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class WallRemovalRecord : IExposable
    {
        public string Id, Target, Original, Left, Right, Permanent, Material, Load, Blocker;
        public List<string> Backup = new List<string>();
        public int MapId, X, Z, Nx, Nz, CompletedTick;
        public long UiRevision;
        public bool Complete, Retired;
        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id"); Scribe_Values.Look(ref Target, "target");
            Scribe_Values.Look(ref Original, "original"); Scribe_Values.Look(ref Left, "left");
            Scribe_Values.Look(ref Right, "right"); Scribe_Values.Look(ref Permanent, "permanent");
            Scribe_Values.Look(ref Material, "material");
            Scribe_Values.Look(ref Load, "load"); Scribe_Values.Look(ref Blocker, "blocker");
            Scribe_Values.Look(ref MapId, "mapId"); Scribe_Values.Look(ref X, "x"); Scribe_Values.Look(ref Z, "z");
            Scribe_Values.Look(ref Nx, "nx"); Scribe_Values.Look(ref Nz, "nz");
            Scribe_Values.Look(ref CompletedTick, "completedTick"); Scribe_Values.Look(ref UiRevision, "uiRevision");
            Scribe_Values.Look(ref Complete, "complete");
            Scribe_Values.Look(ref Retired, "retired");
            Scribe_Collections.Look(ref Backup, "backup", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Backup == null) Backup = new List<string>();
        }
    }
    public sealed class WallRemovalState : GameComponent
    {
        public List<WallRemovalRecord> Records = new List<WallRemovalRecord>();
        public WallRemovalState(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Records, "rimgovernorWallRemoval", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Records == null) Records = new List<WallRemovalRecord>();
        }
    }
}
