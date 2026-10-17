#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// The per-frame boundary: a prefix on TickManager.TickManagerUpdate, which Game.UpdatePlay calls
    /// once per Unity update for the loaded game before the world and map
    /// update passes. That is the only update-to-update signal available
    /// without patching the engine, and it is the same boundary the play
    /// supervisor already counts frames on, so it holds in a batch-mode
    /// launch too -- a GameComponent's GameComponentUpdate does not, since
    /// UpdatePlay reaches it only after the draw passes have run.
    ///
    /// It opens the per-frame main-thread allowance, answers the colony-naming
    /// letter and refreshes clearance salvage evidence.
    ///
    /// The patch installs from the native startup (NativeStartup, the
    /// module initializer the host runs), not from a startup constructor:
    /// this assembly is loaded by RimGovernor.Host as a tool plugin rather
    /// than as a mod assembly, so RimWorld never runs a
    /// [StaticConstructorOnStartup] here.
    public static class ObservationFrameHook
    {
        public const string Owner = "rimgovernor.native.observation-frames";
        /// Patches the boundary; NativeStartup calls it once. A failure is
        /// logged and never thrown: a missing frame boundary must not take the
        /// bridge down; the allowance then never defers (MainThreadAdmission.StaleMs).
        public static void Install()
        {
            try
            {
                MethodBase? target = AccessTools.Method(typeof(TickManager), "TickManagerUpdate");
                if (target == null) throw new MissingMethodException("TickManager.TickManagerUpdate");
                new Harmony(Owner).Patch(target, prefix: new HarmonyMethod(typeof(ObservationFrameHook), nameof(OnUpdate)));
                var info = Harmony.GetPatchInfo(target);
                if (info == null || !info.Owners.Contains(Owner))
                    throw new InvalidOperationException("Harmony did not report the observation frame patch after installation.");
            }
            catch (Exception ex)
            {
                ModLog.Error("startup", "observation frame hook not installed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void OnUpdate()
        {
            // The one per-frame allowance opens here: it drains hops earlier
            // frames deferred; paused, hops are not budgeted.
            try
            {
                var ticks = Find.TickManager;
                if (Current.Game != null) MainThreadAdmission.Frame(ticks != null && !ticks.Paused);
            }
            catch (Exception)
            {
                // Never let the allowance interrupt an update.
            }
            // The colony-naming letter is answered here, with the suggestions.
            try
            {
                if (Current.Game != null && Find.WindowStack != null) ColonyNamingTools.ConfirmSuggested();
            }
            catch (Exception)
            {
                // A failed confirmation leaves the dialog for the next frame.
            }
            // Clearance salvage evidence refreshes over frames.
            try
            {
                if (Current.Game != null) NativeClearanceObservationTools.RefreshSalvage();
            }
            catch (Exception)
            {
                // Never let the refresh interrupt an update.
            }
        }
    }
}
