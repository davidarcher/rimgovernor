using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only (issue #91). rimworld/start_debug_game_ready
    // goes through Root_Play.SetupForQuickTestPlay, which generates a 250x250
    // map on a large planet; most acceptance assertions fit a 200x200 map on a
    // 5% planet, and world and map generation are the bulk of the start. Arm
    // once from the main menu and the next quick start uses the knobs.
    public static class DebugStart
    {
        public const int DefaultMapSize = 200, MinMapSize = 150, MaxMapSize = 400;
        public const float DefaultPlanetCoverage = 0.05f;

        private static bool armed, patched;
        private static int mapSize;
        private static float planetCoverage;

        public static void Arm(int size, float coverage)
        {
            if (size < MinMapSize || size > MaxMapSize) throw new ArgumentException($"mapSize must be within {MinMapSize}..{MaxMapSize}.");
            if (float.IsNaN(coverage) || coverage < 0.05f || coverage > 1f) throw new ArgumentException("planetCoverage must be within 0.05..1.");
            mapSize = size; planetCoverage = coverage; armed = true;
            if (patched) return;
            new Harmony("rimgovernor.test.debug-start").Patch(
                AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)),
                prefix: new HarmonyMethod(typeof(DebugStart), nameof(Start)));
            patched = true;
        }

        // Root_Play.SetupForQuickTestPlay with the map size and planet coverage
        // swapped in; everything else (Crashlanded, Cassandra, Rough, a random
        // seed and tile) is the quick start's own.
        private static bool Start()
        {
            if (!armed) return true;
            armed = false;
            Current.ProgramState = ProgramState.Entry;
            Game.ClearCaches();
            Current.Game = new Game();
            Current.Game.InitData = new GameInitData();
            Current.Game.Scenario = ScenarioDefOf.Crashlanded.scenario;
            Find.Scenario.PreConfigure();
            Current.Game.storyteller = new Storyteller(StorytellerDefOf.Cassandra, DifficultyDefOf.Rough);
            Current.Game.World = WorldGenerator.GenerateWorld(planetCoverage, GenText.RandomSeedString(),
                OverallRainfall.Normal, OverallTemperature.Normal, OverallPopulation.Normal, LandmarkDensity.Normal);
            Find.GameInitData.ChooseRandomStartingTile();
            Find.GameInitData.mapSize = mapSize;
            Find.Scenario.PostIdeoChosen();
            return false;
        }
    }

    public sealed class DebugStartFixture
    {
        [Tool("test/configure_debug_start", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: the next rimworld/start_debug_game_ready uses this map size and planet coverage instead of the quick start's 250 and full planet. Main menu only; one start per call.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Map edge in cells, 150..400 (default 200).")] int mapSize = DebugStart.DefaultMapSize,
            [ToolParameter(Description = "Planet coverage 0.05..1 (default 0.05).")] float planetCoverage = DebugStart.DefaultPlanetCoverage)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.ProgramState != ProgramState.Entry || Current.Game != null) throw new InvalidOperationException("Only a fresh main-menu process can configure a debug start.");
                DebugStart.Arm(mapSize, planetCoverage);
                return new { success = true, armed = true, mapSize, planetCoverage };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
