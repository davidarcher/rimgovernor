#nullable enable

using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// The real frame boundary behind the update-interval account (#642): a
    /// prefix on TickManager.TickManagerUpdate, which Game.UpdatePlay calls
    /// once per Unity update for the loaded game before the world and map
    /// update passes. That is the only update-to-update signal available
    /// without patching the engine, and it is the same boundary the play
    /// supervisor already counts frames on, so it holds in a batch-mode
    /// launch too -- a GameComponent's GameComponentUpdate does not, since
    /// UpdatePlay reaches it only after the draw passes have run.
    ///
    /// The hook does no work of its own beyond reading the current tick and
    /// handing FrameAccounting a monotonic timestamp, and FrameAccounting
    /// times that, so the recorder's own cost is measured rather than assumed.
    /// The session it passes is the loaded Game, so a reload starts a new
    /// session and the account resets with it.
    ///
    /// The intervals are Unity update intervals, never a statement about GPU
    /// presentation: an unrendered launch still updates.
    ///
    /// The patch installs on the first main-thread hop (ProtoBoundary calls
    /// Ensure), not from a startup constructor: this assembly is loaded by
    /// RimBridgeServer as a tool plugin rather than as a mod assembly, so
    /// RimWorld never runs a [StaticConstructorOnStartup] here -- the first
    /// attempt at this hook was such a constructor and it never ran.
    public static class ObservationFrameHook
    {
        public const string Owner = "rimgovernor.native.observation-frames";
        private static int _patched;
        private static string? _error;

        /// Install once, from any thread; after the first call this is one
        /// interlocked read.
        internal static void Ensure()
        {
            if (Volatile.Read(ref _patched) != 0) return;
            Install();
        }

        /// Patch the boundary once. A failure is recorded and never thrown:
        /// the frame account going unrecorded must not take the bridge down.
        /// InstallError carries the reason, and with nothing driving the
        /// recorder every reply reports the account as unhooked.
        public static void Install()
        {
            if (Interlocked.CompareExchange(ref _patched, 1, 0) != 0) return;
            try
            {
                MethodBase? target = AccessTools.Method(typeof(TickManager), "TickManagerUpdate");
                if (target == null) throw new MissingMethodException("TickManager.TickManagerUpdate");
                new Harmony(Owner).Patch(target, prefix: new HarmonyMethod(typeof(ObservationFrameHook), nameof(OnUpdate)));
                var info = Harmony.GetPatchInfo(target);
                if (info == null || !info.Owners.Contains(Owner))
                    throw new InvalidOperationException("Harmony did not report the observation frame patch after installation.");
                _error = null;
            }
            catch (Exception ex)
            {
                _error = ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// The patch's error, null when it installed.
        public static string? InstallError => _error;

        internal static void OnUpdate()
        {
            try
            {
                var game = Current.Game;
                if (game == null) return;
                var ticks = Find.TickManager;
                FrameAccounting.Update(game, ticks != null ? ticks.TicksGame : 0);
            }
            catch (Exception)
            {
                // Never let the account interrupt an update.
            }
            // The optional-observation allowance opens here, and queued
            // resumable captures (#654) spend it before the frame's ticks.
            try
            {
                if (Current.Game != null) ObservationScheduling.Frame();
            }
            catch (Exception)
            {
                // Never let the account interrupt an update.
            }
        }
    }
}
