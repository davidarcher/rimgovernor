using System;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using Verse;

namespace RimGovernor.Native
{
    [StaticConstructorOnStartup]
    internal static partial class IdleExit
    {
        private static bool fired;
        static IdleExit()
        {
            if (Limit == null) return;
            try
            {
                new Harmony(Owner).Patch(AccessTools.Method(typeof(Root), nameof(Root.Update)) ?? throw new MissingMethodException("Root.Update"),
                    postfix: new HarmonyMethod(typeof(IdleExit), nameof(AfterUpdate)));
                ModLog.Info("idle-exit", "idle exit armed: the game quits after " + Limit.Value.TotalMinutes + " minutes without a bridge call");
            }
            catch (Exception ex)
            {
                ModLog.Error("idle-exit", "idle exit not installed: " + ex);
            }
        }

        private static void AfterUpdate()
        {
            if (fired || Limit == null) return;
            var since = SinceActivity;
            if (!Due(Limit.Value)) return;
            fired = true;
            ModLog.Warn("idle-exit", "idle exit: no bridge call for " + (int)since.TotalMinutes + " minutes; quitting");
            Root.Shutdown();
        }
    }
}

