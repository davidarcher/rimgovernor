using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable fixture only: forces the live Pawn_DraftController.Drafted setter
    // itself to throw for one exact armed pawn, simulating a native setter failure
    // rather than a pre-effect validation refusal. Never compiled into production
    // builds (see RimGovernor.Bridge.csproj's DraftFaultFixture-gated Compile entry).
    //
    // This mirrors DraftOwnership.cs's own established pattern (a second, independently
    // owned Harmony patch on the same Pawn_DraftController.Drafted setter, keyed by
    // controller instance) rather than touching NativeAuthorityHooks or any other
    // production hook. One-shot: the armed target self-clears the instant it fires
    // (or on explicit disarm), so no fixture state can leak into a later attempt.
    [StaticConstructorOnStartup]
    internal static class DraftFaultHook
    {
        private const string PatchOwner = "rimgovernor.fixture.draft-fault";
        private static Pawn_DraftController armed;
        private static bool patched;
        static DraftFaultHook()
        {
            try
            {
                var setter = AccessTools.PropertySetter(typeof(Pawn_DraftController), "Drafted");
                if (setter == null) throw new MissingMethodException("Pawn_DraftController.Drafted");
                new Harmony(PatchOwner).Patch(setter, prefix: new HarmonyMethod(typeof(DraftFaultHook), nameof(BeforeDraft)));
                patched = true;
            }
            catch (Exception ex)
            {
                Log.Error("[RimGovernor] Fixture draft-fault hook installation failed: " + ex);
            }
        }
        internal static bool Healthy => patched;
        internal static void Arm(Pawn_DraftController controller) => armed = controller;
        internal static bool Disarm()
        {
            var was = armed != null;
            armed = null;
            return was;
        }
        // Prefix on Pawn_DraftController's own Drafted setter: throws before the real
        // field write when (and only when) this exact controller was armed, then
        // immediately disarms so the fault fires at most once.
        private static void BeforeDraft(Pawn_DraftController __instance)
        {
            if (!ReferenceEquals(__instance, armed)) return;
            armed = null;
            throw new DraftFaultInjectedException("Fixture-armed native draft setter fault.");
        }
    }

    internal sealed class DraftFaultInjectedException : Exception
    {
        internal DraftFaultInjectedException(string message) : base(message) { }
    }

    public sealed class DraftFaultFixture
    {
        [Tool("test/draft_fault_arm", Description = "Disposable fixture: arms the exact colonist's next Pawn_DraftController.Drafted setter call to throw before mutating state, simulating a live native setter failure rather than a pre-effect validation refusal. One-shot; self-clears the instant it fires.")]
        public async Task<object> Arm(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact colonist identity to arm.")] string pawn)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required");
                var actor = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawn);
                if (actor?.drafter == null) throw new InvalidOperationException("Exact pawn with a draft controller is required");
                if (!DraftFaultHook.Healthy) throw new InvalidOperationException("Draft-fault fixture hook is not installed");
                DraftFaultHook.Arm(actor.drafter);
                return new { success = true, pawn, armed = true };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/draft_fault_disarm", Description = "Disposable fixture: clears any armed draft-fault without triggering it. Returns whether a fault was actually armed.")]
        public async Task<object> Disarm(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var wasArmed = DraftFaultHook.Disarm();
                return new { success = true, wasArmed };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
