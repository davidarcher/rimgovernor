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
    // Scenario editor settings only, before native generation. Excluded from production.
    public sealed class ScenarioStartFixture
    {
        private static Scenario pending;
        private static string worldSeed;
        private static string requestedBiome;
        private static DifficultyDef requestedDifficulty;
        private static float minimumTemperature, maximumTemperature;
        private static bool patched;

        [Tool("test/configure_start", Description = "Arm one ordinary scenario start from the main menu; test builds only. Does not edit saves or existing colonies.")]
        public async Task<object> Configure(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Native ScenarioDef name; inspect the definitions returned by list.")] string scenario,
            [ToolParameter(Description = "Native scenario editor count, 1 through 10.")] int count,
            [ToolParameter(Description = "World generation seed.")] string seed,
            [ToolParameter(Description = "Optional native BiomeDef for an ordinary valid settlement tile.")] string biome = "",
            [ToolParameter(Description = "Native DifficultyDef, selected before colony generation.")] string difficulty = "Rough",
            [ToolParameter(Description = "Minimum native seasonal temperature for the selected settlement tile.")] float minTemperature = -100,
            [ToolParameter(Description = "Maximum native seasonal temperature for the selected settlement tile.")] float maxTemperature = 100)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.ProgramState != ProgramState.Entry || Find.CurrentMap != null || Current.Game != null)
                    throw new InvalidOperationException("Only a fresh main-menu process can configure a start.");
                if (pending != null) throw new InvalidOperationException("A start is already armed.");
                if (count < 1 || count > 10 || string.IsNullOrWhiteSpace(seed))
                    throw new ArgumentException("Require 1..10 pawns and a nonempty world seed.");
                if (float.IsNaN(minTemperature) || float.IsNaN(maxTemperature) || minTemperature < -100 || maxTemperature > 100 || minTemperature > maxTemperature)
                    throw new ArgumentException("Require ordered finite seasonal temperatures within -100..100 C.");
                var definition = DefDatabase<ScenarioDef>.GetNamedSilentFail(scenario);
                if (definition == null) throw new ArgumentException("Unknown ScenarioDef.");
                var difficultyDef = DefDatabase<DifficultyDef>.GetNamedSilentFail(difficulty);
                if (difficultyDef == null) throw new ArgumentException("Unknown DifficultyDef.");
                if (!string.IsNullOrEmpty(biome) && DefDatabase<BiomeDef>.GetNamedSilentFail(biome)?.canBuildBase != true)
                    throw new ArgumentException("Unknown or non-settleable BiomeDef.");
                var copy = definition.scenario.CopyForEditing();
                var part = copy.AllParts.OfType<ScenPart_ConfigPage_ConfigureStartingPawns>().SingleOrDefault();
                if (part == null) throw new ArgumentException("Scenario has no editable starting-pawn count.");
                part.pawnCount = count;
                part.pawnChoiceCount = Math.Max(count, part.pawnChoiceCount);
                if (!patched)
                {
                    new Harmony("rimgovernor.test.scenario-start").Patch(
                        AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)),
                        prefix: new HarmonyMethod(typeof(ScenarioStartFixture), nameof(Start)));
                    patched = true;
                }
                pending = copy; worldSeed = seed; requestedBiome = biome;
                requestedDifficulty = difficultyDef;
                minimumTemperature = minTemperature; maximumTemperature = maxTemperature;
                return new { success = true, armed = true, scenario, count, seed, biome,
                    minTemperature, maxTemperature,
                    storyteller = "Cassandra", difficulty, mapSize = 250,
                    cropYieldFactor = difficultyDef.cropYieldFactor,
                    rainfall = "Normal", temperature = "Normal", population = "Normal" };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/list_start_scenarios", Description = "Read native scenario definitions and configurable pawn counts; test builds only.")]
        public async Task<object> List(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => new { success = true,
                difficulties = DefDatabase<DifficultyDef>.AllDefsListForReading.Select(d => new {
                    defName = d.defName, label = d.label, cropYieldFactor = d.cropYieldFactor
                }).ToArray(),
                scenarios = DefDatabase<ScenarioDef>.AllDefsListForReading.Select(d => new {
                    defName = d.defName, label = d.label,
                    pawnCount = d.scenario.AllParts.OfType<ScenPart_ConfigPage_ConfigureStartingPawns>()
                        .Select(p => (int?)p.pawnCount).SingleOrDefault()
                }).ToArray() }, cancellationToken).ConfigureAwait(false);
        }

        private static bool Start()
        {
            if (pending == null) return true;
            var scenario = pending; pending = null;
            // Follow Root_Play's quick-start lifecycle with an edited scenario copy.
            // RimWorld generates starting pawns, stocks, map and jobs normally.
            Current.ProgramState = ProgramState.Entry;
            Game.ClearCaches();
            Current.Game = new Game();
            Current.Game.InitData = new GameInitData();
            Current.Game.Scenario = scenario;
            Find.Scenario.PreConfigure();
            Current.Game.storyteller = new Storyteller(StorytellerDefOf.Cassandra, requestedDifficulty);
            Current.Game.World = WorldGenerator.GenerateWorld(0.3f, worldSeed,
                OverallRainfall.Normal, OverallTemperature.Normal, OverallPopulation.Normal, LandmarkDensity.Normal);
            Find.GameInitData.ChooseRandomStartingTile();
            if (!string.IsNullOrEmpty(requestedBiome) || minimumTemperature != -100 || maximumTemperature != 100)
            {
                var surface = Find.WorldGrid.Surface;
                var candidates = Enumerable.Range(0, surface.TilesCount).Select(i => surface[i])
                    .Where(t => (string.IsNullOrEmpty(requestedBiome) || t.PrimaryBiome.defName == requestedBiome)
                        && TileFinder.IsValidTileForNewSettlement(t.tile)
                        && GenTemperature.MinTemperatureAtTile(t.tile) >= minimumTemperature
                        && GenTemperature.MaxTemperatureAtTile(t.tile) <= maximumTemperature)
                    .Take(1).ToList();
                if (candidates.Count == 0) throw new InvalidOperationException("No native valid settlement tile meets requested biome and seasonal temperatures.");
                Find.GameInitData.startingTile = candidates[0].tile;
            }
            Find.GameInitData.mapSize = 250;
            Find.Scenario.PostIdeoChosen();
            return false;
        }
    }
}
