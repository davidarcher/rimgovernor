using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only (issue #92). A colonist that wanders off to
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
