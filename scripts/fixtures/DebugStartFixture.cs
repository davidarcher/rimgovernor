using System;
using System.Collections.Generic;
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
        public const int DefaultMapSize = 200, MinMapSize = 100, MaxMapSize = 400;
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

    // The blank lab map (#730): a fixture case spawns what it needs at known
    // coordinates on it instead of searching a random world for a site.
    // Wipe turns the loaded map into flat Soil with nothing on it but N
    // fixture-made adult colonists, pins clear weather and a fixed outdoor
    // temperature (persisted through AcceptanceWorld.LabTemperature, so a
    // reloaded lab save keeps them), and quiets the storyteller. Every draw
    // runs under a fixed Rand seed, so the same map in gives the same lab.
    public static class LabStart
    {
        public const float Temperature = 21f;
        public const int DefaultColonists = 3, MaxColonists = 8, SkillLevel = 8;
        private const int Seed = 730;
        private static bool patched;

        public static void EnsurePatched()
        {
            if (patched) return;
            var harmony = new Harmony("rimgovernor.test.lab-start");
            harmony.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp)), postfix: new HarmonyMethod(typeof(LabStart), nameof(PinTemperature)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), nameof(MapTemperature.SeasonalTemp)), postfix: new HarmonyMethod(typeof(LabStart), nameof(PinTemperature)));
            harmony.Patch(AccessTools.Method(typeof(WeatherDecider), nameof(WeatherDecider.WeatherDeciderTick)), prefix: new HarmonyMethod(typeof(LabStart), nameof(SkipWeather)));
            patched = true;
        }

        private static void PinTemperature(ref float __result)
        {
            var pinned = AcceptanceWorld.Lab;
            if (!float.IsNaN(pinned)) __result = pinned;
        }

        private static bool SkipWeather() => float.IsNaN(AcceptanceWorld.Lab);

        public static object Wipe(Map map, int colonists)
        {
            if (colonists < 1 || colonists > MaxColonists) throw new ArgumentException($"colonists must be within 1..{MaxColonists}.");
            EnsurePatched();
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList()) pawn.Destroy(DestroyMode.Vanish);
            foreach (var thing in map.listerThings.AllThings.ToList())
                if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
            foreach (var zone in map.zoneManager.AllZones.ToList()) zone.Delete();
            foreach (var cell in map.AllCells)
            {
                map.roofGrid.SetRoof(cell, null);
                map.snowGrid.SetDepth(cell, 0f);
                if (map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] != null) map.terrainGrid.RemoveFoundation(cell, false);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
            map.fogGrid.ClearAllFog();
            map.areaManager.Home.Clear();
            foreach (var condition in map.gameConditionManager.ActiveConditions.ToList()) condition.End();
            foreach (var condition in Find.World.gameConditionManager.ActiveConditions.ToList()) condition.End();
            map.weatherManager.curWeather = map.weatherManager.lastWeather = WeatherDefOf.Clear;
            map.weatherManager.curWeatherAge = 0;
            AcceptanceWorld.SetLab(Temperature);
            var quiet = QuietStoryteller.Apply(map);

            var center = map.Center;
            var ids = new List<string>();
            Rand.PushState(Seed);
            try
            {
                for (var i = 0; i < colonists; i++)
                {
                    var pawn = Colonist(i);
                    GenSpawn.Spawn(pawn, new IntVec3(center.x - colonists + 1 + 2 * i, 0, center.z), map);
                    ids.Add(pawn.ThingID);
                }
            }
            finally
            {
                Rand.PopState();
            }
            return new { success = true, mapSize = map.Size.x, mapSizeZ = map.Size.z, center = new { x = center.x, z = center.z },
                colonists = ids, temperatureC = Temperature, weather = map.weatherManager.curWeather.defName, quiet, digest = Digest(map) };
        }

        // An adult baseliner with every work type enabled and on, no traits,
        // no bad hediffs and every skill at SkillLevel without passion.
        private static Pawn Colonist(int index)
        {
            for (var tries = 0; tries < 50; tries++)
            {
                var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.PlayerStarter,
                    forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: true, colonistRelationChanceFactor: 0f,
                    allowAddictions: false, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f, fixedGender: index % 2 == 0 ? Gender.Male : Gender.Female,
                    fixedIdeo: Faction.OfPlayer.ideos?.PrimaryIdeo, forceBaselinerChance: 1f, developmentalStages: DevelopmentalStage.Adult);
                var pawn = PawnGenerator.GeneratePawn(request);
                foreach (var trait in pawn.story.traits.allTraits.ToList()) pawn.story.traits.RemoveTrait(trait);
                pawn.Notify_DisabledWorkTypesChanged();
                if (pawn.CombinedDisabledWorkTags != WorkTags.None) { Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard); continue; }
                foreach (var hediff in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(hediff);
                foreach (var skill in pawn.skills.skills) { skill.Level = SkillLevel; skill.xpSinceLastLevel = 0f; skill.passion = Passion.None; }
                pawn.workSettings.EnableAndInitialize();
                foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading) pawn.workSettings.SetPriority(work, 3);
                return pawn;
            }
            throw new InvalidOperationException($"No work-capable lab colonist {index} after 50 generations.");
        }

        // A stable hash of what makes two labs the same: terrain, roofs and
        // every thing's def and cell, plus each colonist's name and skills.
        public static string Digest(Map map)
        {
            var hash = 2166136261u;
            void Add(string s) { foreach (var ch in s) hash = (hash ^ ch) * 16777619u; hash = (hash ^ '|') * 16777619u; }
            Add($"{map.Size.x}x{map.Size.z}");
            foreach (var cell in map.AllCells) Add($"{map.terrainGrid.TerrainAt(cell).defName}{map.roofGrid.RoofAt(cell)?.defName}");
            foreach (var thing in map.listerThings.AllThings.OrderBy(t => t.Position.x).ThenBy(t => t.Position.z).ThenBy(t => t.def.defName))
            {
                Add($"{thing.def.defName}@{thing.Position.x},{thing.Position.z}");
                if (thing is Pawn pawn) Add($"{pawn.Name?.ToStringFull}:{string.Join(",", pawn.skills.skills.Select(s => s.Level))}:{pawn.story.traits.allTraits.Count}");
            }
            return hash.ToString("x8");
        }
    }

    public sealed class LabStartFixture
    {
        static LabStartFixture() { LabStart.EnsurePatched(); }
        public LabStartFixture() { LabStart.EnsurePatched(); }

        [Tool("test/lab_start", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#730): wipe the loaded map to bare Soil (no things, plants, filth, zones, roofs, snow or fog; every pawn removed), spawn N fixture-made adult colonists (fixed skills, every work type on, no traits) at the centre, lock clear weather and a 21 C outdoor temperature, and quiet the storyteller. Persists across save and reload. Replies the map size, colonist ids, centre cell and a digest of the map.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Fixture colonists to spawn, 1..8 (default 3).")] int colonists = LabStart.DefaultColonists,
            [ToolParameter(Description = "wipe (default), or digest, which only reads the current map's digest.")] string action = "wipe")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                if (action == "digest") return new { success = true, mapSize = map.Size.x, lab = !float.IsNaN(AcceptanceWorld.Lab), digest = LabStart.Digest(map) };
                if (action != "wipe") throw new ArgumentException("Unknown action.");
                return LabStart.Wipe(map, colonists);
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public sealed class DebugStartFixture
    {
        [Tool("test/configure_debug_start", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: the next rimworld/start_debug_game_ready uses this map size and planet coverage instead of the quick start's 250 and full planet. Main menu only; one start per call.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Map edge in cells, 100..400 (default 200).")] int mapSize = DebugStart.DefaultMapSize,
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

        [Tool("test/debug_map_census", Description = "Disposable read: how many spawned non-player buildings (natural rock included) the current map carries map-wide and within Home. Lets a case record that a read scoped to Home ran past the old map-wide 8192 bound (#414).")]
        public async Task<object> Census(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("No current map.");
                var player = Faction.OfPlayerSilentFail;
                var home = map.areaManager.Home;
                var mapWide = map.listerThings.AllThings.OfType<Building>().Count(b => b.Spawned && b.Faction != player);
                var inHome = home.ActiveCells.SelectMany(c => c.GetThingList(map)).OfType<Building>()
                    .Where(b => b.Spawned && b.Faction != player).Select(b => b.thingIDNumber).Distinct().Count();
                return new { success = true, mapCells = map.cellIndices.NumGridCells, nonPlayerBuildings = mapWide, homeCells = home.TrueCount, homeNonPlayerBuildings = inHome };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
