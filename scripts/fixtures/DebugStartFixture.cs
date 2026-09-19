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
        private static string seed = "";
        private static bool flat;

        public static void Arm(int size, float coverage, string biomePreference = "", string worldSeed = "", bool flatTile = false)
        {
            if (size < MinMapSize || size > MaxMapSize) throw new ArgumentException($"mapSize must be within {MinMapSize}..{MaxMapSize}.");
            if (float.IsNaN(coverage) || coverage < 0.05f || coverage > 1f) throw new ArgumentException("planetCoverage must be within 0.05..1.");
            var wanted = (biomePreference ?? "").Split(',').Select(b => b.Trim()).Where(b => b.Length > 0).ToArray();
            foreach (var name in wanted)
                if (DefDatabase<BiomeDef>.GetNamedSilentFail(name)?.canBuildBase != true) throw new ArgumentException($"Unknown or non-settleable BiomeDef {name}.");
            mapSize = size; planetCoverage = coverage; biomes = wanted; seed = (worldSeed ?? "").Trim(); flat = flatTile; armed = true;
            if (patched) return;
            new Harmony("rimgovernor.test.debug-start").Patch(
                AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)),
                prefix: new HarmonyMethod(typeof(DebugStart), nameof(Start)));
            patched = true;
        }

        // Root_Play.SetupForQuickTestPlay with the map size and planet coverage
        // swapped in, and the starting tile drawn from the first requested
        // biome the generated planet offers a valid settlement tile in
        // (issue #172); everything else (Crashlanded, Cassandra, Rough, and
        // a random tile when no biome is asked for) is the quick start's
        // own. The world seed is the armed one, or a random one when none
        // was given; under a given seed the tile choice and the starting
        // pawns draw from a Rand state seeded by it too, so the same seed
        // reproduces the same start (#281). The map itself is generated
        // afterwards from the world seed and the tile, so it follows.
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
            var worldSeed = seed.Length > 0 ? seed : GenText.RandomSeedString();
            Current.Game.World = WorldGenerator.GenerateWorld(planetCoverage, worldSeed,
                OverallRainfall.Normal, OverallTemperature.Normal, OverallPopulation.Normal, LandmarkDensity.Normal);
            Rand.PushState(GenText.StableStringHash(worldSeed));
            try
            {
                Find.GameInitData.ChooseRandomStartingTile();
                if (biomes.Length > 0 || flat)
                {
                    var surface = Find.WorldGrid.Surface;
                    var valid = Enumerable.Range(0, surface.TilesCount).Select(i => surface[i])
                        .Where(t => TileFinder.IsValidTileForNewSettlement(t.tile)).ToList();
                    var chosen = biomes.Length == 0 ? valid
                        : biomes.Select(b => valid.Where(t => t.PrimaryBiome.defName == b).ToList()).FirstOrDefault(c => c.Count > 0);
                    if (chosen == null) throw new InvalidOperationException($"No valid settlement tile in any requested biome ({string.Join(", ", biomes)}) on this planet.");
                    if (flat)
                    {
                        // A flat, river-free, road-free tile with no mutator
                        // (#272): no mountains to path around and no river
                        // to bridge in a construction-heavy case. A planet
                        // that offers none in the biome keeps the mutators
                        // and drops the requirement in that order.
                        var plain = chosen.Where(t => t.hilliness == Hilliness.Flat && t.Rivers.NullOrEmpty() && t.Roads.NullOrEmpty()).ToList();
                        var bare = plain.Where(t => t.Mutators.Count == 0).ToList();
                        var pick = bare.Count > 0 ? bare : plain.Count > 0 ? plain : chosen;
                        if (pick != bare) Log.Warning($"[RimGovernor] debug start: no {(pick == plain ? "mutator-free flat" : "flat river-free")} tile on this planet; settling a {(pick == plain ? "flat tile with mutators" : "tile of the roll's own terrain")}.");
                        chosen = pick;
                    }
                    Find.GameInitData.startingTile = chosen.RandomElement().tile;
                }
                Find.GameInitData.mapSize = mapSize;
                Find.Scenario.PostIdeoChosen();
                EnsureCapableColonists();
            }
            finally
            {
                Rand.PopState();
            }
            return false;
        }

        // Every starting colonist can Construct and Haul (issue #152): the
        // throughput stage needs three such pawns, and a random roll with
        // one incapable pawn used to fail the run -- and, under the cached
        // start, every run after it. Incapable pawns are rerolled the way
        // the starting-pawn page's randomize button rerolls them; a roll
        // that never produces one is a hard error rather than a bad save.
        public const int MaxRerollsPerPawn = 40;

        public static bool CapableColonist(Pawn p) =>
            p != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
            && (p.skills?.GetSkill(SkillDefOf.Construction).Level ?? 0) >= ThingDefOf.Wall.constructionSkillPrerequisite;

        private static void EnsureCapableColonists()
        {
            var pawns = Find.GameInitData.startingAndOptionalPawns;
            var rerolls = 0;
            for (var i = 0; i < Find.GameInitData.startingPawnCount && i < pawns.Count; i++)
            {
                var tries = 0;
                while (!CapableColonist(pawns[i]))
                {
                    if (++tries > MaxRerollsPerPawn) throw new InvalidOperationException($"Starting pawn {i} was incapable of Construction or Hauling after {MaxRerollsPerPawn} rerolls.");
                    StartingPawnUtility.RandomizeInPlace(pawns[i]);
                    rerolls++;
                }
            }
            if (rerolls > 0) Log.Message($"[RimGovernor] debug start rerolled {rerolls} starting pawn(s) incapable of Construction or Hauling.");
        }
    }

    public sealed class DebugStartFixture
    {
        [Tool("test/configure_debug_start", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: the next rimworld/start_debug_game_ready uses this map size and planet coverage instead of the quick start's 250 and full planet. Main menu only; one start per call.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Map edge in cells, 150..400 (default 200).")] int mapSize = DebugStart.DefaultMapSize,
            [ToolParameter(Description = "Planet coverage 0.05..1 (default 0.05).")] float planetCoverage = DebugStart.DefaultPlanetCoverage,
            [ToolParameter(Description = "Optional comma-separated native BiomeDef names in preference order; the start settles a random valid tile of the first biome the planet offers, or fails when it offers none.")] string biomes = "",
            [ToolParameter(Description = "Optional world seed; the tile choice and starting pawns follow it, so the same seed reproduces the same start. Empty draws a random seed.")] string seed = "",
            [ToolParameter(Description = "Settle a flat tile without rivers, roads or tile mutators when the planet (and biome) offers one (#272).", DefaultValue = false)] bool flat = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.ProgramState != ProgramState.Entry || Current.Game != null) throw new InvalidOperationException("Only a fresh main-menu process can configure a debug start.");
                DebugStart.Arm(mapSize, planetCoverage, biomes, seed, flat);
                return new { success = true, armed = true, mapSize, planetCoverage, biomes, seed, flat };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
