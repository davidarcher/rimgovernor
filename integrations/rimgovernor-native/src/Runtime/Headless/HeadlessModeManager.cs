using System;
using System.Linq;
using HarmonyLib;
using Verse;

namespace HeadlessRim
{
    // Inheriting from 'Mod' ensures this runs safely during mod initialization,
    // avoiding the Linux Mono GC static constructor crash.
    public class HeadlessRimMod : Mod
    {
        public HeadlessRimMod(ModContentPack content) : base(content)
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                if (!args.Contains("-batchmode")) return;

                Log.Message("--------------------------------------------------");
                Log.Message("---                HEADLESS RIM                ---");
                Log.Message("--------------------------------------------------");

                // MINIMAL STARTUP PATCHES
                var harmony = new Harmony("com.headlessrim.core");
                HeadlessPatches.ApplyStartupPatches(harmony);

                // HOOK MAIN MENU
                var bootstrap = new Harmony("com.headlessrim.bootstrap");
                var original = AccessTools.Method(typeof(UIRoot_Entry), "Init");
                var postfix = AccessTools.Method(typeof(HeadlessRimMod), nameof(OnMainMenuReady));

                bootstrap.Patch(original, postfix: new HarmonyMethod(postfix));

                Log.Message("[HeadlessRim] Bootstrap armed. Waiting for Main Menu...");
            }
            catch (Exception ex)
            {
                Log.Error($"[HeadlessRim] Bootstrap Error: {ex}");
            }
        }

        public static void OnMainMenuReady()
        {
            Log.Message("[HeadlessRim] Main Menu reached...");

            try
            {
                // APPLY RUNTIME PATCHES (UI, Map, Audio)
                var harmony = new Harmony("com.headlessrim.core");
                HeadlessPatches.ApplyRuntimePatches(harmony);

                Log.Message("[HeadlessRim] Headless mode active. Waiting for external control.");
            }
            catch (Exception ex)
            {
                Log.Error($"[HeadlessRim] Post-Init Error: {ex}");
            }
        }
    }
}