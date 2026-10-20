using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only; every fixture build carries this file. The needs test/freeze_needs pins
    // (FrozenNeeds) live in FixtureKit.cs with the other shared fixture primitives.

    // Disposable test setup only (#3669). A controlled colonist with no committed order runs vanilla job selection
    // under its work priorities. A native-only fixture (pawn cooking, delivery, sowing) turns those priorities off
    // for each such colonist so it does not work the staged targets itself; a fixture's own orders keep their pawns.
    public static class IdleColonists
    {
        public static void Apply()
        {
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return;
            var status = authority.Status();
            if (!status.Active || status.Identity?.Map is not Map map) return;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                if (NativePawnActivities.Standing(pawn)) pawn.workSettings?.DisableAll();
        }
    }

    public sealed class FreezeNeedsFixture
    {
        [Tool("test/freeze_needs", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: pin every free colonist's needs at maximum after each needs tick except the NeedDefs in keep (e.g. Food, Rest, Joy), for the current game only. action=apply (default), inspect or release. Scenarios about eating, sleeping or mood keep those needs.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "apply (default), inspect or release.")] string action = "apply",
            [ToolParameter(Description = "Comma-separated NeedDef names left live; empty freezes everything.")] string keep = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || Find.CurrentMap == null) throw new InvalidOperationException("A loaded game is required.");
                switch (action)
                {
                    case "apply":
                        return FrozenNeeds.Apply(keep.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()));
                    case "inspect":
                        return new { success = true, frozen = FrozenNeeds.Active, keep = FrozenNeeds.Keep.ToArray(), needs = FrozenNeeds.Inspect() };
                    case "release":
                        return FrozenNeeds.Release();
                }
                throw new ArgumentException("Unknown action.");
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
