using System;
using RimGovernor.Host.Sdk;
using Verse;

namespace RimGovernor.Host;

public class RimGovernorHostMod : Mod
{
    public RimGovernorHostMod(ModContentPack content)
        : base(content)
    {
        try
        {
            LaunchStagePatches.Apply();
        }
        catch (Exception ex)
        {
            // The waterfall is diagnostics: the bridge starts without it.
            Log.Warning($"[RimBridge] The launch waterfall has no RimWorld stages: {ex.Message}");
        }

        try
        {
            using (LaunchPhases.Phase("mod-constructor.total"))
            {
                using (LaunchPhases.Phase("main-thread.initialize"))
                {
                    RimBridgeMainThread.Initialize();
                    RimGovernor.Host.Sdk.RimBridgeMainThreadAccess.Current = new RimBridgeMainThreadClient();
                }
                using (LaunchPhases.Phase("harmony.startup-patches"))
                    RimBridgePatches.Apply();
                using (LaunchPhases.Phase("startup.on-mod-constructed"))
                    RimBridgeStartup.OnModConstructed();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[RimBridge] STARTUP_INIT_FAILURE: {ex}");
            Log.Error($"[RimBridge] Failed to initialize server: {ex}");
        }
    }
}
