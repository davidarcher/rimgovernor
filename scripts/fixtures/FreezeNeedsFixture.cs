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
    // Disposable test setup only; every fixture build carries this file. A colonist that wanders off to
    // eat, sleep or break mid-assertion reruns a harness for a reason
    // unrelated to it. Freezing pins every colonist need at its maximum after
    // each needs tick except the ones the scenario keeps live (Food for a
    // cooking harness, Rest for a sleeping one). This bends "preserve normal
    // game rules" for the test game only: the op lives here, never in
    // rimgovernor-native, and the reply names what was frozen so a report
    // cannot pass silently because nobody ever ate or slept.
    public static class FrozenNeeds
    {
        private static bool patched;
        private static Game game;
        private static HashSet<string> keep = new HashSet<string>();

        public static bool Active => game != null && ReferenceEquals(game, Current.Game);
        public static IEnumerable<string> Keep => keep.OrderBy(k => k);

        public static object Apply(IEnumerable<string> keepDefs)
        {
            var set = new HashSet<string>(keepDefs ?? Enumerable.Empty<string>());
            foreach (var name in set)
                if (DefDatabase<NeedDef>.GetNamedSilentFail(name) == null) throw new ArgumentException($"Unknown NeedDef {name}.");
            keep = set; game = Current.Game;
            EnsurePatched();
            var pinned = 0;
            foreach (var pawn in Colonists()) pinned += Pin(pawn);
            return new { success = true, frozen = true, keep = Keep.ToArray(), colonists = Colonists().Count(), pinned,
                needs = Inspect() };
        }

        public static object Release()
        {
            game = null; keep = new HashSet<string>();
            return new { success = true, frozen = false, needs = Inspect() };
        }

        public static object[] Inspect()
        {
            return Colonists().Select(p => (object)new {
                pawn = p.GetUniqueLoadID(), name = p.LabelShort,
                needs = p.needs.AllNeeds.Select(n => new { def = n.def.defName, level = n.CurLevelPercentage, frozen = Active && !keep.Contains(n.def.defName) }).ToArray(),
            }).ToArray();
        }

        private static IEnumerable<Pawn> Colonists()
        {
            if (Current.Game == null) return Enumerable.Empty<Pawn>();
            return Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).Where(p => p.needs != null);
        }

        private static int Pin(Pawn pawn)
        {
            var pinned = 0;
            foreach (var need in pawn.needs.AllNeeds)
            {
                if (keep.Contains(need.def.defName)) continue;
                need.CurLevelPercentage = 1f;
                pinned++;
            }
            return pinned;
        }

        private static void EnsurePatched()
        {
            if (patched) return;
            // Need.NeedInterval is overridden per need without calling base,
            // so the pin runs after the whole tracker's interval instead.
            new Harmony("rimgovernor.test.freeze-needs").Patch(
                AccessTools.Method(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.NeedsTrackerTickInterval)),
                postfix: new HarmonyMethod(typeof(FrozenNeeds), nameof(AfterTick)));
            patched = true;
        }

        private static void AfterTick(Pawn ___pawn)
        {
            if (!Active || ___pawn == null || ___pawn.needs == null || !___pawn.IsColonist || ___pawn.Faction != Faction.OfPlayer) return;
            Pin(___pawn);
        }
    }

    // Disposable test setup only (#3230). Under an active controller authority
    // NativePawnActivities owns every player-controlled colonist's voluntary job
    // choice, and the acceptance scenario clock runs only under that authority.
    // A case that probes a vanilla mechanic (a colonist hauling a corpse on its
    // own) declares AcceptanceCase.VanillaColonists, and the runner calls this op
    // so colonists choose jobs natively for the current game only. Production keeps
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
        [Tool("test/vanilla_colonists", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#3230): colonists choose voluntary jobs natively under an active controller authority, for the current game only. For acceptance cases that probe vanilla mechanics under the scenario clock.")]
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
    // no brain, so the scenario clock stands in for that one decision: before each Run it calls this op, which
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

    public sealed class StandbyColonistsFixture
    {
        [Tool("test/standby_colonists", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#3322): the scenario clock's stand-in for the brain's standby. Installs the explicit idle for each controlled colonist with no Hands assignment and an idle job; never replaces an assignment.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || Find.CurrentMap == null) throw new InvalidOperationException("A loaded game is required.");
                return StandbyColonists.Apply();
            }, cancellationToken).ConfigureAwait(false);
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
