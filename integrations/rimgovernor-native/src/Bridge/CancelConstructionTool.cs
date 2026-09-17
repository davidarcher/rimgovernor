#nullable enable

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class ConstructionCancellationTools
    {
        [Tool("home/cancel_construction", Title = "Cancel one exact construction order",
            Description = "Cancel an exact observed player Blueprint_Build or Frame through the game's normal cancellation path. "
                + "Requires matching colony/load/map, target identity, build definition, material and position while paused. "
                + "Completed buildings, installation orders, other things and other cell designations are not targeted. dryRun defaults to true.")]
        public async Task<object> Cancel(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact colonyId from home/colony_identity.")] string colonyId,
            [ToolParameter(Description = "Exact loadToken from home/colony_identity.")] string loadToken,
            [ToolParameter(Description = "Exact mapId from home/colony_identity.")] int mapId,
            [ToolParameter(Description = "Exact thingId from a detailed home/list_buildings row; labels are not accepted.")] string thing,
            [ToolParameter(Description = "Exact buildDefName from that blueprint or frame row.")] string expectedDef,
            [ToolParameter(Description = "Observed anchor x.")] int x,
            [ToolParameter(Description = "Observed anchor z.")] int z,
            [ToolParameter(Description = "Observed stuff definition, or empty for an order without stuff.", DefaultValue = "")] string expectedStuff = "",
            [ToolParameter(Description = "Preview without cancelling or refunding anything.", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (map == null || identity == null || identity.ColonyId != colonyId
                    || identity.LoadToken != loadToken || map.uniqueID != mapId)
                    return new { success = false, error = "Colony, map or load changed; observe before cancelling." };
                if (Find.TickManager == null || !Find.TickManager.Paused)
                    return new { success = false, error = "Pause the game before validating construction cancellation." };
                var player = Faction.OfPlayerSilentFail;
                var target = map.listerThings.AllThings.FirstOrDefault(t => t.ThingID == thing);
                if (player == null || target == null || !target.Spawned || target.Destroyed
                    || target.Faction != player || !(target is Blueprint_Build || target is Frame))
                    return new { success = false, error = "Exact target is not a current player construction blueprint or frame." };
                var definition = target.def.entityDefToBuild;
                var stuff = ((IConstructible)target).EntityToBuildStuff() ?? target.Stuff;
                if (definition == null || definition.defName != expectedDef
                    || (stuff?.defName ?? "") != expectedStuff || target.Position.x != x || target.Position.z != z)
                    return new { success = false, error = "Construction target metadata changed; no cancellation was made." };
                var before = new { thingId = target.ThingID, buildDefName = definition.defName,
                    stuff = stuff?.defName, position = new { x, z }, phase = target is Frame ? "frame" : "blueprint" };
                if (dryRun)
                    return new { success = true, dryRun = true, applied = false, removed = false, target = before };
                // Designator_Cancel.DesignateThing uses this exact branch for
                // blueprints and frames. Native Destroy owns refunds and cleanup.
                target.Destroy(DestroyMode.Cancel);
                var removed = target.Destroyed && !target.Spawned && !map.listerThings.AllThings.Contains(target);
                return new { success = removed, dryRun = false, applied = removed, removed, target = before };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
