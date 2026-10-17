using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only; every fixture build carries this file. The needs test/freeze_needs pins
    // (FrozenNeeds) live in FixtureKit.cs with the other shared fixture primitives.

    // Disposable test setup only (#3230). Under an active controller authority
    // NativePawnActivities owns every player-controlled colonist's voluntary job
    // choice. A case that probes a vanilla mechanic (a colonist hauling a corpse
    // on its own) lists this op and calls it so colonists choose jobs natively for
    // the current game only. Production keeps
    // the rule that a controlled colonist never silently falls back to vanilla AI.
    public static class VanillaColonists
    {
        private static bool patched;
        private static Game game;

        public static bool Active => game != null && ReferenceEquals(game, Current.Game);

        public static object Apply()
        {
            game = Current.Game;
            EnsurePatched();
            return new { success = true, vanillaColonists = true,
                colonists = Find.Maps.Sum(m => m.mapPawns.FreeColonistsSpawned.Count) };
        }

        private static void EnsurePatched()
        {
            if (patched) return;
            var controlled = AccessTools.Method(typeof(NativePawnActivities), "Controlled")
                ?? throw new MissingMethodException("NativePawnActivities.Controlled");
            new Harmony("rimgovernor.test.vanilla-colonists").Patch(controlled,
                prefix: new HarmonyMethod(typeof(VanillaColonists), nameof(BeforeControlled)));
            patched = true;
        }

        private static bool BeforeControlled(ref bool __result)
        {
            if (!Active) return true;
            __result = false;
            return false;
        }
    }

    public sealed class VanillaColonistsFixture
    {
        [Tool("test/vanilla_colonists", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#3230): colonists choose voluntary jobs natively under an active controller authority, for the current game only. For acceptance cases that probe vanilla mechanics.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || Find.CurrentMap == null) throw new InvalidOperationException("A loaded game is required.");
                return VanillaColonists.Apply();
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // Disposable test setup only (#3322). Under brain authority every controlled colonist holds a Hands
    // assignment: the brain gives a colonist it selected nothing for an explicit standby. A native-only case runs
    // no brain, so a fixture that needs it (pawn cooking, pawn delivery) stands in for that one decision: this
    // installs the same standby order (native wander, no work, no deadline) for each controlled colonist with no
    // assignment and an idle job. It never replaces an assignment, so a fixture's own orders keep their pawns;
    // under VanillaColonists no colonist is controlled and nothing is installed.
    public static class StandbyColonists
    {
        public static object Apply()
        {
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                return new { success = true, active = false, standby = 0 };
            var status = authority.Status();
            if (!status.Active || status.Identity?.Map is not Map map) return new { success = true, active = false, standby = 0 };
            var controlled = AccessTools.Method(typeof(NativePawnActivities), "Controlled")
                ?? throw new MissingMethodException("NativePawnActivities.Controlled");
            var context = new RimGovernor.Contracts.Wire.Common.ObservationContext { NativeGeneration = status.Generation,
                Tick = Find.TickManager.TicksGame, Identity = new RimGovernor.Contracts.Wire.Common.Identity
                { ColonyId = status.Identity.ColonyId, LoadToken = status.Identity.LoadToken, MapId = status.Identity.MapId } };
            var installed = new List<string>();
            var refused = new List<string>();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray())
            {
                if (NativePawnActivities.Owned(pawn) || !(bool)controlled.Invoke(null, new object[] { pawn })
                    || !NativeFoodActivity.Available(pawn)) continue;
                var id = pawn.GetUniqueLoadID();
                var batch = new RimGovernor.Brain.Domain.PawnActivities(status.Generation, new[] {
                    new RimGovernor.Brain.Domain.PawnActivityOrder(id, "harness-standby/" + id, "harness-standby", 0, 1, false,
                        new[] { new RimGovernor.Brain.Domain.PawnActivityStep(RimGovernor.Brain.Domain.PawnActivityKind.Standby, "", "", "", 0, long.MaxValue) }) });
                var invalid = NativePawnActivities.Validate(batch, context);
                if (invalid != null) { refused.Add(id + ":" + invalid.Detail); continue; }
                NativePawnActivities.Apply(batch, context);
                installed.Add(id);
            }
            return new { success = true, active = true, standby = installed.Count, pawns = installed, refused };
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
