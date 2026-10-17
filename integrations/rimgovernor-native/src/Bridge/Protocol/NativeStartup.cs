#nullable enable
using System;
using System.Runtime.CompilerServices;
using RimGovernor.Brain.Bridge;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The Bridge's one startup. RimWorld runs no <c>[StaticConstructorOnStartup]</c> in an assembly the host loads by
    /// file, so this is the module initializer, and the host runs it explicitly (RunModuleConstructor) on the game
    /// thread once play data has loaded, before it publishes its registry. In order: the game patches and guards
    /// install once, the facade is published to the brain in this process, and the HTTP channel and the in-process
    /// brain schedule their (off-thread) starts. An install failure is logged and the rest still starts.
    /// </summary>
    internal static class NativeStartup
    {
        // CA2255 warns against module initializers in libraries; this assembly is a plugin the host loads by file.
#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void Start()
        {
            try { Install(); }
            catch (Exception e) { Log.Error($"[RimGovernor] Native startup install failed: {e}"); }
            if (NativeFacadeRegistry.Current == null) NativeFacadeRegistry.Publish(InProcessNativeFacade.Instance);
            HttpChannelBootstrap.Start();
            BrainBootstrap.Start();
        }
#pragma warning restore CA2255

        // On the game thread.
        private static void Install()
        {
            NativeActionDispatch.AssertComplete();
            ModLogTick.Install();
            ObservationFrameHook.Install();
            PlayerSpeedKeyBlock.Install();
            NativeDrugPolicy.Install();
            NativeDesignationGuards.Install();
            HomeCoverage.Install();
            TreeLatticeSowing.Install();
            WallLayerGuard.Install();
            ConstructionTierGate.Install();
            RemotePickupGuard.Install();
            ConstructionSkillGuard.Install();
            ConsumptionHooks.Install();
            SpoilagePreference.Install();
            TickProfile.Install();
            WallUpgradeSafety.Install();
            NativePawnControlState.Initialize();
            NativePawnActivities.Install();
        }
    }
}
