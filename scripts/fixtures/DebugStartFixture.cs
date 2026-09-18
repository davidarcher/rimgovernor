using System;
using System.Linq;
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
        private static string[] biomes = Array.Empty<string>();

        public static void Arm(int size, float coverage, string biomePreference = "")
        {
            if (size < MinMapSize || size > MaxMapSize) throw new ArgumentException($"mapSize must be within {MinMapSize}..{MaxMapSize}.");
            if (float.IsNaN(coverage) || coverage < 0.05f || coverage > 1f) throw new ArgumentException("planetCoverage must be within 0.05..1.");
            var wanted = (biomePreference ?? "").Split(',').Select(b => b.Trim()).Where(b => b.Length > 0).ToArray();
            foreach (var name in wanted)
                if (DefDatabase<BiomeDef>.GetNamedSilentFail(name)?.canBuildBase != true) throw new ArgumentException($"Unknown or non-settleable BiomeDef {name}.");
            mapSize = size; planetCoverage = coverage; biomes = wanted; armed = true;
            if (patched) return;
            new Harmony("rimgovernor.test.debug-start").Patch(
                AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)),
                prefix: new HarmonyMethod(typeof(DebugStart), nameof(Start)));
            patched = true;
        }

        // Root_Play.SetupForQuickTestPlay with the map size and planet coverage
        // swapped in, and the starting tile drawn from the first requested
        // biome the generated planet offers a valid settlement tile in
        // (issue #172); everything else (Crashlanded, Cassandra, Rough, a
        // random seed, and a random tile when no biome is asked for) is the
        // quick start's own.
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
            if (biomes.Length > 0)
            {
                var surface = Find.WorldGrid.Surface;
                var valid = Enumerable.Range(0, surface.TilesCount).Select(i => surface[i])
                    .Where(t => TileFinder.IsValidTileForNewSettlement(t.tile)).ToList();
                var chosen = biomes.Select(b => valid.Where(t => t.PrimaryBiome.defName == b).ToList()).FirstOrDefault(c => c.Count > 0);
                if (chosen == null) throw new InvalidOperationException($"No valid settlement tile in any requested biome ({string.Join(", ", biomes)}) on this planet.");
                Find.GameInitData.startingTile = chosen.RandomElement().tile;
            }
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
            [ToolParameter(Description = "Planet coverage 0.05..1 (default 0.05).")] float planetCoverage = DebugStart.DefaultPlanetCoverage,
            [ToolParameter(Description = "Optional comma-separated native BiomeDef names in preference order; the start settles a random valid tile of the first biome the planet offers, or fails when it offers none.")] string biomes = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.ProgramState != ProgramState.Entry || Current.Game != null) throw new InvalidOperationException("Only a fresh main-menu process can configure a debug start.");
                DebugStart.Arm(mapSize, planetCoverage, biomes);
                return new { success = true, armed = true, mapSize, planetCoverage, biomes };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
