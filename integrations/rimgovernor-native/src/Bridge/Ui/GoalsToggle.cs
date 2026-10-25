using System;
using System.Threading;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The play-settings bar toggle that opens <see cref="GoalsWindow"/> (#3497). Installed from
    /// <see cref="NativeStartup"/>, not a <c>[StaticConstructorOnStartup]</c> (those never run in the file-loaded Bridge),
    /// and skipped in the headless acceptance build.
    /// </summary>
    internal static class GoalsToggle
    {
        private static int installed;
        private static Texture2D? icon;

        internal static void Install()
        {
            if (HeadlessRim.HeadlessMode.Active) return;
            if (Interlocked.CompareExchange(ref installed, 1, 0) != 0) return;
            new Harmony("rimgovernor.goals.toggle").Patch(
                AccessTools.Method(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))
                    ?? throw new MissingMethodException("PlaySettings.DoPlaySettingsGlobalControls"),
                postfix: new HarmonyMethod(typeof(GoalsToggle), nameof(Controls)));
        }

        private static void Controls(WidgetRow row, bool worldView)
        {
            if (worldView || row == null) return;
            icon ??= ContentFinder<Texture2D>.Get("UI/Buttons/Info", false) ?? BaseContent.BadTex;
            var open = Find.WindowStack.IsOpen<GoalsWindow>();
            var on = open;
            row.ToggleableIcon(ref on, icon, "Open the RimGovernor goals window.", SoundDefOf.Mouseover_ButtonToggle);
            if (on == open) return;
            if (on) Find.WindowStack.Add(new GoalsWindow());
            else Find.WindowStack.WindowOfType<GoalsWindow>()?.Close();
        }
    }
}
