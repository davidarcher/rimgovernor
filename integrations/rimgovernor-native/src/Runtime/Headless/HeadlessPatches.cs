using System;
using HarmonyLib;
using Verse;
using Verse.Sound;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;

namespace HeadlessRim
{
    public static class HeadlessPatches
    {
        // CRITICAL STARTUP PATCHES (Run Early)
        public static void ApplyStartupPatches(Harmony harmony)
        {
            Log.Message("[HeadlessRim] Applying CRITICAL STARTUP patches...");
            // Def resolution creates build designators before the main menu.
            Patch(harmony, typeof(Designator_Build), "UpdateIcon", nameof(SkipPrefix));
            Patch(harmony, typeof(ResolutionUtility), "Update", nameof(SkipPrefix));

            Patch(harmony, typeof(GlobalTextureAtlasManager), "TryInsertStatic", nameof(SkipPrefix));
            Patch(harmony, typeof(Graphic_Single), "TryInsertIntoAtlas", nameof(SkipPrefix));
            Patch(harmony, typeof(Graphic_Multi), "TryInsertIntoAtlas", nameof(SkipPrefix));
            Patch(harmony, typeof(Graphic_Collection), "TryInsertIntoAtlas", nameof(SkipPrefix));

            Log.Message("[HeadlessRim] Finished applying patches for: [GlobalTextureAtlasManager, Graphic_Single, Graphic_Multi, Graphic_Collection]");
        }

        // RUNTIME PATCHES (Run Late)
        public static void ApplyRuntimePatches(Harmony harmony)
        {
            Log.Message("[HeadlessRim] Applying RUNTIME patches (UI, Map, Audio)...");

            // UI LOOP & DRAWING
            Patch(harmony, typeof(UIRoot_Play), "UIRootUpdate", nameof(SkipPrefix));
            Patch(harmony, typeof(UIRoot_Entry), "UIRootOnGUI", nameof(SkipPrefix));
            Patch(harmony, typeof(UIRoot_Play), "UIRootOnGUI", nameof(SkipPrefix));
            Patch(harmony, typeof(MapInterface), "MapInterfaceOnGUI_BeforeMainTabs", nameof(SkipPrefix));
            Patch(harmony, typeof(LongEventHandler), "LongEventsOnGUI", nameof(SkipPrefix));
            // Pawn registration still updates the map normally. Refreshing open
            // pawn-table windows only initializes GUI text/layout, absent in batch mode.
            Patch(harmony, typeof(MainTabWindowUtility), "NotifyAllPawnTables_PawnsChanged", nameof(SkipPrefix));
            // World feature labels initialize GUI text; they never advance world simulation.
            Patch(harmony, typeof(WorldFeatures), "UpdateFeatures", nameof(SkipPrefix));
            // Synchronous events wait for their loading window's first repaint.
            // Headless mode has no repaint: acknowledge presentation only, then
            // let the unmodified native update execute/save/finish the event.
            Patch(harmony, typeof(LongEventHandler), "LongEventsUpdate", nameof(LongEventUpdatePrefix));

            // MAP MESH GENERATION (Prevents Gameplay NREs)
            Patch(harmony, typeof(Section), "RegenerateAllLayers", nameof(SkipPrefix));
            Patch(harmony, typeof(MapDrawer), "WholeMapChanged", nameof(SkipPrefix));
            Patch(harmony, typeof(MapDrawer), "SectionChanged", nameof(SkipPrefix));
            Patch(harmony, typeof(MapDrawer), "RegenerateEverythingNow", nameof(SkipPrefix));
            Patch(harmony, typeof(MapDrawer), "MapMeshDrawerUpdate_First", nameof(SkipPrefix));
            Patch(harmony, typeof(MapDrawer), "DrawMapMesh", nameof(SkipPrefix));
            Patch(harmony, typeof(MapDrawer), "Dispose", nameof(DisposeMapPrefix));
            Patch(harmony, typeof(Graphic), "Print", nameof(SkipPrefix));

            // AUDIO
            Patch(harmony, typeof(SoundStarter), "PlayOneShot", nameof(SkipPrefix));
            Patch(harmony, typeof(SoundRoot), "Update", nameof(SkipPrefix));
            Patch(harmony, typeof(MusicManagerPlay), "MusicUpdate", nameof(SkipPrefix));
            Patch(harmony, typeof(MusicManagerEntry), "MusicManagerEntryUpdate", nameof(SkipPrefix));

            // PORTRAITS
            var portraitOriginal = AccessTools.Method(typeof(PortraitsCache), "Get", new Type[] { typeof(Pawn), typeof(Vector2), typeof(Rot4), typeof(Vector3), typeof(float), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(System.Collections.Generic.Dictionary<Apparel, Color>) });
            if (portraitOriginal != null)
                harmony.Patch(portraitOriginal, prefix: new HarmonyMethod(typeof(HeadlessPatches), nameof(ReturnNullTexturePrefix)));

            Log.Message("[HeadlessRim] Finished applying patches for: [UI Loop, Mesh generation, Audio, Portraits]");
        }

        // Helpers
        private static void Patch(Harmony harmony, Type type, string methodName, string patchMethodName)
        {
            try
            {
                var original = AccessTools.Method(type, methodName);
                if (original != null) harmony.Patch(original, prefix: new HarmonyMethod(typeof(HeadlessPatches), patchMethodName));
            }
            catch (Exception error) { Log.Warning("[HeadlessRim] Patch failed: " + type.FullName + "." + methodName + ": " + error.Message); }
        }

        public static bool SkipPrefix() => false;
        public static void LongEventUpdatePrefix(object ___currentEvent)
        {
            if (___currentEvent != null)
                AccessTools.Field(___currentEvent.GetType(), "alreadyDisplayed")?.SetValue(___currentEvent, true);
        }
        // With regeneration disabled neither drawing collection was allocated.
        // Preserve native cleanup if any drawing resources do exist.
        public static bool DisposeMapPrefix(Section[,] ___sections, System.Collections.Generic.List<MapDrawLayer> ___global)
            => ___sections != null || ___global != null;
        public static bool ReturnNullTexturePrefix(ref Texture __result) { __result = null; return false; }
    }
}
