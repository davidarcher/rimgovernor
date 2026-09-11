using System;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class ColonyIdentityTools
    {
        [Tool("home/colony_identity", Title = "Saved colony identity", Description = "Saved colony ID, current map ID and per-load token. Use the load token to invalidate in-flight work after loading a save.")]
        public async Task<object> Identity(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                if (Current.Game == null || Find.CurrentMap == null)
                    return new { success = false, error = "Load a colony first" };
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                // Bridge extension assemblies can load after Verse cached GameComponent types.
                // Attach our metadata component explicitly for older saves and cached type lists.
                if (identity == null)
                {
                    identity = new ColonyIdentity(Current.Game);
                    Current.Game.components.Add(identity);
                }
                if (string.IsNullOrEmpty(identity.ColonyId)) identity.ColonyId = Guid.NewGuid().ToString("N");
                return new { success = true, colonyId = identity.ColonyId,
                    loadToken = identity.LoadToken, mapId = Find.CurrentMap.uniqueID,
                    tick = Find.TickManager.TicksGame, observationBatchVersion = 1, placementPreviewBatchVersion = 1 };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
