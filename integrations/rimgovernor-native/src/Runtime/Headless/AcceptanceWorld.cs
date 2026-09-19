using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Cheaper fixture worlds for the headless acceptance profiles (#272).
    // Everything here is gated on the -rimgovernor-test-acceleration launch
    // argument, the same gate as the clock's tick boost: a production or
    // player launch never sees it. Under the gate the autosaver never ticks
    // (the profile already sets a 1000-day interval; this removes the tick
    // itself), and a game whose QuietWorld marker is set (test/quiet_world,
    // persisted with the save) skips the simulation the cases never
    // observe: wild plants and wild animals outside the home area stop
    // ticking, and the wild plant and animal spawners stop. Plants in a
    // growing zone always tick, so a farm case that opts in still grows its
    // crops. Audio is already off headless (HeadlessPatches skips the sound
    // root under -batchmode).
    public sealed class AcceptanceWorld : GameComponent
    {
        public bool QuietWorld;

        public AcceptanceWorld(Game game) { }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref QuietWorld, "rimgovernorQuietWorld", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) Invalidate();
        }

        public static readonly bool Launched = Array.IndexOf(Environment.GetCommandLineArgs(), "-rimgovernor-test-acceleration") >= 0;

        private static Game? cachedGame;
        private static bool cachedQuiet;
        private static bool installed;

        // Quiet is the loaded game's marker, cached per Game instance so the
        // per-thing tick prefixes cost a reference compare.
        public static bool Quiet
        {
            get
            {
                if (!Launched) return false;
                var game = Current.Game;
                if (game == null) return false;
                if (!ReferenceEquals(game, cachedGame))
                {
                    cachedGame = game;
                    cachedQuiet = game.GetComponent<AcceptanceWorld>()?.QuietWorld ?? false;
                }
                return cachedQuiet;
            }
        }

        public static void Invalidate() { cachedGame = null; }

        public static void SetQuiet(bool quiet)
        {
            var component = Current.Game?.GetComponent<AcceptanceWorld>();
            if (component == null) throw new InvalidOperationException("A loaded game is required.");
            component.QuietWorld = quiet;
            Invalidate();
        }

        public static void Install()
        {
            if (installed || !Launched) return;
            installed = true;
            var harmony = new Harmony("rimgovernor.test-acceleration.world");
            Patch(harmony, AccessTools.Method(typeof(Autosaver), "AutosaverTick"), nameof(Skip));
            Patch(harmony, AccessTools.Method(typeof(WildPlantSpawner), "WildPlantSpawnerTick"), nameof(SkipWhenQuiet));
            Patch(harmony, AccessTools.Method(typeof(WildAnimalSpawner), "WildAnimalSpawnerTick"), nameof(SkipWhenQuiet));
            // A plant ticks Long (and, in 1.6, through TickInterval); a pawn
            // ticks every tick, rare and through TickInterval. Every entry is
            // prefixed so whichever the tick list calls is covered.
            foreach (var name in new[] { "TickLong", "TickInterval" })
                Patch(harmony, AccessTools.DeclaredMethod(typeof(Plant), name), nameof(WildPlant));
            foreach (var name in new[] { "Tick", "TickRare", "TickInterval" })
                Patch(harmony, AccessTools.DeclaredMethod(typeof(Pawn), name), nameof(WildAnimal));
            Log.Message("[RimGovernor] test acceleration world patches installed: no autosave tick; quiet-world wild tick skip armed.");
        }

        private static void Patch(Harmony harmony, System.Reflection.MethodBase? original, string prefix)
        {
            if (original == null) { Log.Warning("[RimGovernor] test acceleration world patch target missing for " + prefix); return; }
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(AcceptanceWorld), prefix));
        }

        private static bool Skip() => false;
        private static bool SkipWhenQuiet() => !Quiet;

        // Outside the home area and any growing zone a wild plant neither
        // grows nor dies while the world is quiet.
        private static bool WildPlant(Plant __instance)
        {
            if (!Quiet || !__instance.Spawned) return true;
            var map = __instance.Map;
            var cell = __instance.Position;
            if (map.areaManager.Home[cell]) return true;
            return map.zoneManager.ZoneAt(cell) is Zone_Growing;
        }

        // A factionless animal outside the home area does not tick while the
        // world is quiet: it neither wanders, hungers nor spawns young.
        private static bool WildAnimal(Pawn __instance)
        {
            if (!Quiet || !__instance.Spawned || __instance.Faction != null) return true;
            var race = __instance.RaceProps;
            if (race == null || race.Humanlike) return true;
            return __instance.Map.areaManager.Home[__instance.Position];
        }
    }
}
