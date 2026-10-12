using RimGovernor.Host.Sdk;
using UnityEngine;
using Verse;

namespace HeadlessRim
{
    [StaticConstructorOnStartup]
    public static class HeadlessStartup
    {
        static HeadlessStartup()
        {
            if (!HeadlessMode.Active) return;
            ModLog.Info("startup", "test mode: rendering disabled, frame cap removed");
            // Waylith has no window to vsync in headless mode (the QualitySettings
            // vsync setter is a stub); its frame limiter is Application.targetFrameRate.
            Application.targetFrameRate = -1;
        }
    }
}
