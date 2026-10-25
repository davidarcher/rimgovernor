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
            Log.Warning($"[Native] The launch waterfall has no RimWorld stages: {ex.Message}");
        }

        try
        {
            using (LaunchPhases.Phase("mod-constructor.total"))
            {
                using (LaunchPhases.Phase("main-thread.initialize"))
                {
                    NativeMainThread.Initialize();
                    RimGovernor.Host.Sdk.NativeMainThreadAccess.Current = new NativeMainThreadClient();
                }
                using (LaunchPhases.Phase("harmony.startup-patches"))
                    NativePatches.Apply();
                using (LaunchPhases.Phase("startup.on-mod-constructed"))
                    NativeStartup.OnModConstructed();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Native] STARTUP_INIT_FAILURE: {ex}");
            Log.Error($"[Native] Failed to initialize server: {ex}");
        }
    }
}
