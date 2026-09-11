using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Native equivalent of instruments/mini_install.py's verified item/destination
    // workflow. Uses the same placement calls as Designator_Install, without UI selection.
    public sealed class HomeInstallTools
    {
        [Tool("home/install", Description = "Inspect or queue installation of exact packed furniture. Use its observed thingId (packed or inner). Omit x/z for status. dryRun=true previews native legality; false queues pawn work. Existing orders elsewhere are refused, not cancelled. The returned inner thingId survives installation.")]
        public async Task<object> Install(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string thingId, int x = -1, int z = -1, int rotation = 0, bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() => Run(thingId, x, z, rotation, dryRun), cancellationToken);
        }

        private static bool Matches(Thing t, string id) => t != null && (t.ThingID == id || t.GetUniqueLoadID() == id);

        private static object Run(string id, int x, int z, int rotation, bool dryRun)
        {
            var map = Find.CurrentMap;
            if (map == null || string.IsNullOrWhiteSpace(id))
                return BridgeCommon.Failure("home/install", "A loaded map and exact thingId are required.");
            if (rotation < 0 || rotation > 3)
                return BridgeCommon.Failure("home/install", "rotation must be 0 North, 1 East, 2 South or 3 West.");
            var all = map.listerThings.AllThings;
            var installs = all.OfType<Blueprint_Install>().ToList();
            var mini = all.OfType<MinifiedThing>().FirstOrDefault(t => Matches(t, id) || Matches(t.InnerThing, id));
            var pending = installs.FirstOrDefault(b => Matches(b.MiniToInstallOrBuildingToReinstall, id) || Matches(b.ThingToInstall, id));
            if (mini == null && pending != null)
                mini = pending.MiniToInstallOrBuildingToReinstall as MinifiedThing;
            var inner = mini?.InnerThing ?? all.OfType<Building>().FirstOrDefault(t => Matches(t, id));
            if (!(inner is Building) || inner.Faction != Faction.OfPlayer)
                return BridgeCommon.Failure("home/install", "Exact player-owned packed building or installed inner building was not found.");
            if (!inner.def.rotatable) rotation = 0;
            if (pending == null)
                pending = installs.FirstOrDefault(b => b.ThingToInstall == inner);
            object State(string state, bool accepted, string reason = null) => new
            {
                success = true, state, accepted, reason, dryRun,
                thingId = inner.GetUniqueLoadID(), packedThingId = mini?.GetUniqueLoadID(),
                defName = inner.def.defName, stuff = inner.Stuff?.defName,
                position = inner.Spawned ? new { x = inner.Position.x, z = inner.Position.z } : null,
                rotation = inner.Spawned ? (int?)inner.Rotation.AsInt : null,
                blueprint = pending == null ? null : new { thingId = pending.GetUniqueLoadID(), x = pending.Position.x,
                    z = pending.Position.z, rotation = pending.Rotation.AsInt },
                meaning = "Queued installation needs pawn work; installed means this exact inner building is spawned."
            };
            if (inner.Spawned)
                return State("installed", (dryRun && x == -1 && z == -1) || (inner.Position.x == x && inner.Position.z == z && inner.Rotation.AsInt == (inner.def.rotatable ? rotation : 0)));
            if (x == -1 && z == -1 && dryRun)
                return State(pending == null ? "packed" : "queued", true);
            if (pending != null)
                return State("queued", pending.Position.x == x && pending.Position.z == z && pending.Rotation.AsInt == rotation,
                    "Existing installation order retained; cancel it explicitly before changing destination.");
            if (mini == null || !mini.Spawned || mini.Position.Fogged(map))
                return State("packed", false, "The packed item must be spawned and visible to queue installation.");
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map) || cell.Fogged(map))
                return State("packed", false, "Destination must be in bounds and visible.");
            var rot = inner.def.rotatable ? new Rot4(rotation) : Rot4.North;
            var report = GenConstruct.CanPlaceBlueprintAt(inner.def, cell, rot, map, false, mini, inner);
            if (!report.Accepted)
                return State("packed", false, report.Reason);
            if (dryRun)
                return State("placeable", true);
            // Designator_Install.DesignateSingleCell uses this same wipe/placement sequence.
            GenSpawn.WipeExistingThings(cell, rot, inner.def.installBlueprintDef, map, DestroyMode.Deconstruct);
            GenConstruct.PlaceBlueprintForInstall(mini, cell, map, rot, Faction.OfPlayer);
            pending = map.listerThings.AllThings.OfType<Blueprint_Install>().FirstOrDefault(b => b.ThingToInstall == inner
                && b.Position == cell && b.Rotation == rot);
            return State(pending == null ? "unverified" : "queued", pending != null);
        }
    }
}
