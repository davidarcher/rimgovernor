using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

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

        // A fresh process answers the bridge while its defs are still
        // loading (#1264): a start queued then NREs in new Game() and the
        // readiness wait times out. Configuring waits for the main menu:
        // defs loaded and no long event running or queued.
        public static bool MainMenuReached() =>
            PlayDataLoader.Loaded && !LongEventHandler.AnyEventNowOrWaiting && Current.ProgramState == ProgramState.Entry;

        public const int DefaultMainMenuTimeoutMs = 120000;

        public static async Task WaitForMainMenuAsync(IRimBridgeContext ctx, CancellationToken cancellationToken, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            while (!await ctx.MainThread.InvokeAsync<bool>(() => MainMenuReached(), cancellationToken).ConfigureAwait(false))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Main menu not reached within {timeoutMs} ms (defs still loading).");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

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
            // A destroyed holder (a casket, a crate) drops what it held, so
            // the sweep repeats until nothing is left.
            for (var pass = 0; map.listerThings.AllThings.Count > 0 || map.mapPawns.AllPawnsSpawned.Count > 0; pass++)
            {
                if (pass == 10) throw new InvalidOperationException($"{map.listerThings.AllThings.Count} things survived 10 wipe passes, e.g. {map.listerThings.AllThings.FirstOrDefault()?.def.defName}.");
                foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList())
                    if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
                foreach (var thing in map.listerThings.AllThings.ToList())
                {
                    if (thing.Destroyed) continue;
                    if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                    else if (thing.Spawned) thing.DeSpawn();
                }
            }
            foreach (var zone in map.zoneManager.AllZones.ToList()) zone.Delete();
            foreach (var cell in map.AllCells)
            {
                map.roofGrid.SetRoof(cell, null);
                map.snowGrid.SetDepth(cell, 0f);
                if (map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] != null) map.terrainGrid.RemoveFoundation(cell, false);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
            // Despawning the mountains marked their roofs to collapse; the
            // roofs are gone, so the pending collapse is too (#768).
            map.roofCollapseBuffer.Clear();
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

        [Tool("test/lab_spawn", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#743): spawn one finished thing at a cell of the loaded map, the lab contract runner's single-building or single-pawn helper. def is a PawnKindDef (a generated pawn) or a ThingDef (a building, or an item stack of count). Replies the spawned thing's load id, kind and cell.")]
        public async Task<object> Spawn(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "PawnKindDef or ThingDef name.")] string def,
            [ToolParameter(Description = "Cell x.")] int x,
            [ToolParameter(Description = "Cell z.")] int z,
            [ToolParameter(Description = "Stuff ThingDef for a stuffed building; empty takes the def's default stuff.")] string stuff = "",
            [ToolParameter(Description = "Rotation 0..3 (north, east, south, west).")] int rotation = 0,
            [ToolParameter(Description = "Item stack count (items only, clamped to the stack limit).")] int count = 1,
            [ToolParameter(Description = "player (default) or none: the spawned thing's faction.")] string faction = "player")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                if (faction != "player" && faction != "none") throw new ArgumentException("faction must be player or none.");
                if (rotation < 0 || rotation > 3) throw new ArgumentException("rotation must be within 0..3.");
                var cell = new IntVec3(x, 0, z);
                if (!cell.InBounds(map)) throw new ArgumentException($"Cell {x},{z} is out of bounds.");
                var owner = faction == "player" ? Faction.OfPlayer : null;
                var rot = new Rot4(rotation);
                Thing thing;
                string kind;
                var pawnKind = DefDatabase<PawnKindDef>.GetNamedSilentFail(def ?? "");
                if (pawnKind != null)
                {
                    thing = PawnGenerator.GeneratePawn(new PawnGenerationRequest(pawnKind, owner, forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowAddictions: false));
                    kind = "pawn";
                }
                else
                {
                    var thingDef = DefDatabase<ThingDef>.GetNamedSilentFail(def ?? "") ?? throw new ArgumentException($"No PawnKindDef or ThingDef named {def}.");
                    if (thingDef.category != ThingCategory.Building && thingDef.category != ThingCategory.Item && thingDef.category != ThingCategory.Plant) throw new ArgumentException($"{def} is neither a building, an item nor a plant.");
                    ThingDef stuffDef = null;
                    if (thingDef.MadeFromStuff)
                        stuffDef = stuff == "" ? GenStuff.DefaultStuffFor(thingDef) : DefDatabase<ThingDef>.GetNamedSilentFail(stuff) ?? throw new ArgumentException($"No stuff named {stuff}.");
                    if (!GenAdj.OccupiedRect(cell, rot, thingDef.size).InBounds(map)) throw new ArgumentException($"{def} at {x},{z} does not fit the map.");
                    thing = ThingMaker.MakeThing(thingDef, stuffDef);
                    if (thingDef.category == ThingCategory.Item) thing.stackCount = Math.Max(1, Math.Min(count, thingDef.stackLimit));
                    else if (thing is Plant plant) plant.Growth = 1f;
                    else if (owner != null) thing.SetFaction(owner);
                    kind = thingDef.category == ThingCategory.Item ? "item" : thingDef.category == ThingCategory.Plant ? "plant" : "building";
                }
                GenSpawn.Spawn(thing, cell, map, rot);
                return new { success = true, id = thing.GetUniqueLoadID(), thingId = thing.ThingID, kind, def = thing.def.defName,
                    stuff = thing.Stuff?.defName, cell = new { x = thing.Position.x, z = thing.Position.z }, stackCount = thing.stackCount };
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // Stages a whole combat lab fixture (#854) on a wiped lab in one call:
    // the spec (built in Go, internal/nativeaccept/cases/combatlab) lists
    // structures, the lab colonists' cells and gear, and hostiles with fixed
    // weapons, skills and no apparel, spawned under a fixed seed and given an
    // assault lord. The reply reads every staged pawn back from the live map,
    // and a digest without generated names, so two stagings compare equal.
    public static class LabStage
    {
        private const int Seed = 854;
        private const int HostileSkill = 8;

        public static object Stage(Map map, JObject spec)
        {
            var things = new List<object>();
            var pawns = new List<Pawn>();
            var hostiles = new List<Pawn>();
            var prisoners = new List<Pawn>();
            var insects = new List<Pawn>();
            var mechs = new List<Pawn>();
            Rand.PushState(Seed);
            try
            {
                var faction = HostileFaction();
                foreach (JObject t in spec["things"] as JArray ?? new JArray())
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail((string)t["def"]) ?? throw new ArgumentException($"No ThingDef {t["def"]}.");
                    var stuffName = (string)t["stuff"] ?? "";
                    var stuff = def.MadeFromStuff ? (stuffName == "" ? GenStuff.DefaultStuffFor(def) : DefDatabase<ThingDef>.GetNamedSilentFail(stuffName) ?? throw new ArgumentException($"No stuff {stuffName}.")) : null;
                    var cell = Cell(map, t);
                    var thing = ThingMaker.MakeThing(def, stuff);
                    // hostile (#930): the lab hostiles' faction, e.g. a ship part to attack.
                    // A hive (#1071) is the insects'; natural rock no one's.
                    if (thing is Hive) thing.SetFaction(Faction.OfInsects);
                    else if (def.category == ThingCategory.Building && def.CanHaveFaction) thing.SetFaction((bool?)t["hostile"] == true ? faction : Faction.OfPlayer);
                    // hitPoints (#1150): a damaged building, e.g. a door to repair.
                    if ((int?)t["hitPoints"] is int hp && def.useHitPoints) thing.HitPoints = Math.Max(1, Math.Min(hp, thing.MaxHitPoints));
                    GenSpawn.Spawn(thing, cell, map, new Rot4((int?)t["rotation"] ?? 0));
                    things.Add(new { id = thing.GetUniqueLoadID(), def = def.defName, x = cell.x, z = cell.z });
                }
                // roof (#1071): a RoofDef over a rectangle, e.g. overhead mountain.
                if (spec["roof"] is JObject roof)
                {
                    var roofDef = DefDatabase<RoofDef>.GetNamedSilentFail((string)roof["def"]) ?? throw new ArgumentException($"No RoofDef {roof["def"]}.");
                    foreach (var cell in CellRect.FromLimits((int)roof["minX"], (int)roof["minZ"], (int)roof["maxX"], (int)roof["maxZ"]))
                        if (cell.InBounds(map)) map.roofGrid.SetRoof(cell, roofDef);
                }
                var colonists = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                // An arrival mode (#870) drops the hostiles in by the game's
                // own PawnsArrivalModeWorker, landing around the first
                // hostile's cell, instead of spawning each at its cell.
                var arrivalName = (string)spec["arrival"] ?? "";
                var arrival = arrivalName == "" ? null : DefDatabase<PawnsArrivalModeDef>.GetNamedSilentFail(arrivalName) ?? throw new ArgumentException($"No PawnsArrivalModeDef {arrivalName}.");
                IntVec3? arrivalCenter = null;
                foreach (JObject p in spec["pawns"] as JArray ?? new JArray())
                {
                    var cell = Cell(map, p);
                    Pawn pawn;
                    if ((string)p["side"] == "colonist")
                    {
                        var index = (int)p["index"];
                        if (index < 0 || index >= colonists.Count) throw new ArgumentException($"Colonist index {index}: the lab has {colonists.Count}.");
                        pawn = colonists[index];
                        pawn.Position = cell;
                        pawn.Notify_Teleported(true, true);
                    }
                    else if ((string)p["side"] == "hostile" || (string)p["side"] == "prisoner")
                    {
                        var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail((string)p["kind"]) ?? throw new ArgumentException($"No PawnKindDef {p["kind"]}.");
                        pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, faction, forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: true,
                            allowAddictions: false, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f, forceBaselinerChance: 1f, developmentalStages: DevelopmentalStage.Adult));
                        foreach (var trait in pawn.story.traits.allTraits.ToList()) pawn.story.traits.RemoveTrait(trait);
                        foreach (var hediff in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(hediff);
                        foreach (var skill in pawn.skills.skills) { skill.Level = HostileSkill; skill.passion = Passion.None; }
                        pawn.apparel?.DestroyAll();
                        pawn.inventory?.DestroyAll();
                        if ((string)p["side"] == "prisoner")
                        {
                            // #1080: held by the colony, no lord until it breaks out.
                            GenSpawn.Spawn(pawn, cell, map);
                            pawn.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                            prisoners.Add(pawn);
                        }
                        else
                        {
                            if (arrival == null) GenSpawn.Spawn(pawn, cell, map);
                            hostiles.Add(pawn);
                        }
                    }
                    else if ((string)p["side"] == "insect")
                    {
                        // #1071: an insect of the insects' faction, assaulting the colony.
                        var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail((string)p["kind"]) ?? throw new ArgumentException($"No PawnKindDef {p["kind"]}.");
                        pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfInsects, forceGenerateNewPawn: true, canGeneratePawnRelations: false, developmentalStages: DevelopmentalStage.Adult));
                        foreach (var hediff in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(hediff);
                        GenSpawn.Spawn(pawn, cell, map);
                        insects.Add(pawn);
                    }
                    else if ((string)p["side"] == "mech")
                    {
                        // #1118: a mechanoid of the mechanoids' faction, assaulting the colony.
                        var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail((string)p["kind"]) ?? throw new ArgumentException($"No PawnKindDef {p["kind"]}.");
                        pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfMechanoids, forceGenerateNewPawn: true, canGeneratePawnRelations: false));
                        GenSpawn.Spawn(pawn, cell, map);
                        mechs.Add(pawn);
                    }
                    else if ((string)p["side"] == "animal" || (string)p["side"] == "manhunter" || (string)p["side"] == "wild")
                    {
                        // #1116: "wild" is a factionless animal left calm.
                        // #1057: a player animal with the named trainables
                        // learned, or a wild animal gone permanently manhunter.
                        var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail((string)p["kind"]) ?? throw new ArgumentException($"No PawnKindDef {p["kind"]}.");
                        bool ours = (string)p["side"] == "animal";
                        pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, ours ? Faction.OfPlayer : null, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                            fixedBiologicalAge: 3f, fixedChronologicalAge: 3f, developmentalStages: DevelopmentalStage.Adult));
                        foreach (var hediff in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(hediff);
                        GenSpawn.Spawn(pawn, cell, map);
                        if (ours)
                            foreach (var name in p["trained"] as JArray ?? new JArray())
                                pawn.training.Train(DefDatabase<TrainableDef>.GetNamedSilentFail((string)name) ?? throw new ArgumentException($"No TrainableDef {name}."), null, complete: true);
                        else if ((string)p["side"] == "manhunter" && !pawn.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true))
                            throw new InvalidOperationException($"{kind.defName} did not go manhunter.");
                    }
                    else throw new ArgumentException($"Unknown side {p["side"]}.");
                    if (pawn.equipment != null) Arm(pawn, (string)p["weapon"] ?? "", (string)p["weaponStuff"] ?? "");
                    // apparel (#1049): one worn apparel def, a shield belt for the EMP case or a psychic shock lance (#1038).
                    if ((string)p["apparel"] is string apparel && apparel != "")
                    {
                        var apparelDef = DefDatabase<ThingDef>.GetNamedSilentFail(apparel) ?? throw new ArgumentException($"No apparel {apparel}.");
                        var worn = (Apparel)ThingMaker.MakeThing(apparelDef, apparelDef.MadeFromStuff ? GenStuff.DefaultStuffFor(apparelDef) : null);
                        pawn.apparel.Wear(worn, false, true);
                        // A new shield starts empty; stage it fully charged.
                        if (worn.GetComp<CompShield>() is CompShield shield)
                            Traverse.Create(shield).Field("energy").SetValue(worn.GetStatValue(StatDefOf.EnergyShieldEnergyMax));
                    }
                    // downed (#867): anesthetic downs the pawn without wounds.
                    if ((bool?)p["downed"] == true) pawn.health.AddHediff(HediffDefOf.Anesthetic);
                    // hediffs (#1056): extra hediff def names, e.g. a drug high or addiction.
                    foreach (var name in (p["hediffs"] as JArray)?.Select(t => (string)t) ?? Enumerable.Empty<string>())
                        pawn.health.AddHediff(DefDatabase<HediffDef>.GetNamedSilentFail(name) ?? throw new ArgumentException($"No HediffDef {name}."));
                    // injured (#1080): a blunt blow bruises without bleeding.
                    if ((bool?)p["injured"] == true) pawn.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 8f));
                    // stunTicks (#1118): stun the pawn directly for that many ticks.
                    if ((int?)p["stunTicks"] is int stun && stun > 0) pawn.stances.stunner.StunFor(stun, null, addBattleLog: false);
                    pawns.Add(pawn);
                    if (arrival != null && pawn.Faction != Faction.OfPlayer && arrivalCenter == null) arrivalCenter = cell;
                }
                if (arrival != null && hostiles.Count > 0)
                    arrival.Worker.Arrive(hostiles, new IncidentParms { target = map, faction = faction, raidArrivalMode = arrival, spawnCenter = arrivalCenter!.Value, spawnRotation = Rot4.South, points = 500f });
            }
            finally
            {
                Rand.PopState();
            }
            // siege (#1147): the hostiles are a real siege lord camped at the
            // spot; the game's own LordToil_Siege places the blueprints, drops
            // the supplies and sets the builders to work on the frames.
            if (hostiles.Count > 0 && spec["siege"] is JObject siege)
                LordMaker.MakeNewLord(hostiles[0].Faction, new LordJob_Siege(hostiles[0].Faction, Cell(map, siege), (float?)siege["points"] ?? 500f), map, hostiles);
            else if (hostiles.Count > 0)
                LordMaker.MakeNewLord(hostiles[0].Faction, new LordJob_AssaultColony(hostiles[0].Faction, canKidnap: false, canTimeoutOrFlee: false, sappers: (bool?)spec["sappers"] == true, canSteal: false), map, hostiles);
            if (insects.Count > 0)
                LordMaker.MakeNewLord(Faction.OfInsects, new LordJob_AssaultColony(Faction.OfInsects, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, insects);
            if (mechs.Count > 0)
                LordMaker.MakeNewLord(Faction.OfMechanoids, new LordJob_AssaultColony(Faction.OfMechanoids, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, mechs);
            // prisonBreak (#1080): the game's own break, started by the first prisoner.
            if ((bool?)spec["prisonBreak"] == true)
            {
                if (prisoners.Count == 0) throw new ArgumentException("prisonBreak needs a prisoner.");
                PrisonBreakUtility.StartPrisonBreak(prisoners[0], out _, out _, out _, out var escaping);
                if (escaping == null || escaping.Count == 0) throw new InvalidOperationException("The prison break freed no prisoner.");
            }
            var rows = pawns.Select(p => new { id = p.GetUniqueLoadID(),
                side = p.Faction == Faction.OfInsects ? "insect" : p.Faction == Faction.OfMechanoids ? "mech" : p.RaceProps.Animal ? (p.Faction == Faction.OfPlayer ? "animal" : p.InMentalState ? "manhunter" : "wild") : p.Faction == Faction.OfPlayer ? "colonist" : p.HostFaction == Faction.OfPlayer ? "prisoner" : "hostile", kind = p.kindDef.defName,
                x = p.PositionHeld.x, z = p.PositionHeld.z, inPod = !p.Spawned, weapon = p.equipment?.Primary?.def.defName, hostile = p.HostileTo(Faction.OfPlayer),
                lordJob = p.GetLord()?.LordJob?.GetType().Name, health = p.health.summaryHealth.SummaryHealthPercent, apparel = p.apparel?.WornApparelCount ?? 0,
                worn = p.apparel?.WornApparel.Select(a => new { id = a.GetUniqueLoadID(), def = a.def.defName }).ToList() }).ToList();
            return new { success = true, faction = hostiles.FirstOrDefault()?.Faction.def.defName, things, pawns = rows, digest = Digest(map), digestRows = DigestRows(map) };
        }

        private static IntVec3 Cell(Map map, JObject o)
        {
            var cell = new IntVec3((int)o["x"], 0, (int)o["z"]);
            if (!cell.InBounds(map)) throw new ArgumentException($"Cell {cell.x},{cell.z} is out of bounds.");
            return cell;
        }

        private static void Arm(Pawn pawn, string weapon, string stuffName)
        {
            pawn.equipment.DestroyAllEquipment();
            if (weapon == "") return;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(weapon) ?? throw new ArgumentException($"No weapon {weapon}.");
            var stuff = def.MadeFromStuff ? (stuffName == "" ? GenStuff.DefaultStuffFor(def) : DefDatabase<ThingDef>.GetNamed(stuffName)) : null;
            var thing = (ThingWithComps)ThingMaker.MakeThing(def, stuff);
            thing.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Outsider);
            pawn.equipment.AddEquipment(thing);
        }

        // Pirates when the world has them, else the first hostile humanlike
        // faction by def name, so the choice does not depend on list order.
        private static Faction HostileFaction()
        {
            return Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer && !f.def.hidden && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer))
                .OrderBy(f => f.def == FactionDefOf.Pirate ? 0 : 1).ThenBy(f => f.def.defName).FirstOrDefault()
                ?? throw new InvalidOperationException("No hostile humanlike faction for lab hostiles.");
        }

        // What a staging controls, one row per thing and pawn: a pawn is its
        // side, kind, cell, weapon, Shooting and Melee and apparel count.
        // Generated names and backstories (and the non-combat skills a
        // backstory disables) differ between stagings in one world.
        public static List<string> DigestRows(Map map)
        {
            var rows = new List<string>();
            foreach (var thing in map.listerThings.AllThings.Where(t => !(t is Pawn)).OrderBy(t => t.Position.x).ThenBy(t => t.Position.z).ThenBy(t => t.def.defName))
                rows.Add($"{thing.def.defName}:{thing.Stuff?.defName}@{thing.Position.x},{thing.Position.z}");
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.OrderBy(p => p.Position.x).ThenBy(p => p.Position.z))
                rows.Add($"{(pawn.Faction == Faction.OfPlayer ? "c" : "h")}:{pawn.kindDef.defName}@{pawn.Position.x},{pawn.Position.z}:{pawn.equipment?.Primary?.def.defName}"
                    + $":{pawn.skills?.GetSkill(SkillDefOf.Shooting).Level},{pawn.skills?.GetSkill(SkillDefOf.Melee).Level}:{pawn.apparel?.WornApparelCount ?? 0}");
            return rows;
        }

        public static string Digest(Map map)
        {
            var hash = 2166136261u;
            foreach (var row in DigestRows(map)) { foreach (var ch in row) hash = (hash ^ ch) * 16777619u; hash = (hash ^ '|') * 16777619u; }
            return hash.ToString("x8");
        }
    }

    // Every hit on a pawn since the last lab staging (#855): the same
    // Thing.TakeDamage edge CombatInjuryHook and NativeCombatCausality patch,
    // attributed by DamageInfo.Instigator's side. A colonist hitting a
    // colonist is friendly fire. Patched by the first staging; bounded.
    public static class LabDamageLedger
    {
        private const int Cap = 4096;
        private static readonly List<object> rows = new List<object>();
        private static bool patched;
        public static int Dropped { get; private set; }

        public static void Reset()
        {
            rows.Clear();
            Dropped = 0;
            if (patched) return;
            new Harmony("rimgovernor.fixture.lab-damage").Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage), new[] { typeof(DamageInfo) }),
                postfix: new HarmonyMethod(typeof(LabDamageLedger), nameof(Postfix)));
            patched = true;
        }

        public static List<object> Rows() => rows.ToList();

        private static string Side(Thing t) => t == null ? "" : t.Faction == Faction.OfPlayer ? "colonist" : t.HostileTo(Faction.OfPlayer) ? "hostile" : "other";

        public static void Postfix(Thing __instance, DamageInfo dinfo, DamageWorker.DamageResult __result)
        {
            if (!(__instance is Pawn victim) || float.IsNaN(AcceptanceWorld.Lab)) return;
            if (rows.Count >= Cap) { Dropped++; return; }
            var by = dinfo.Instigator;
            rows.Add(new { tick = Find.TickManager.TicksGame, victim = victim.GetUniqueLoadID(), victimSide = Side(victim), instigator = by?.GetUniqueLoadID() ?? "",
                instigatorSide = Side(by), weapon = dinfo.Weapon?.defName ?? "", dealt = __result?.totalDamageDealt ?? 0f, downed = victim.Downed, dead = victim.Dead });
        }
    }

    public sealed class LabStageFixture
    {
        // A tick call holds the main thread; 2000 ticks of a lab skirmish is
        // well under the call ceiling.
        private const int MaxTicks = 2000;

        [Tool("test/lab_stage", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#854): stage a combat lab fixture on a wiped lab in one call, or run synchronous ticks on it. spec is JSON {things:[{def,stuff,x,z,rotation,hostile,hitPoints}], pawns:[{side:colonist|hostile|animal|manhunter|prisoner|insect, index (colonist), kind (PawnKindDef), x, z, weapon, weaponStuff, downed, injured, trained (animal TrainableDefs), apparel, hediffs:[HediffDef]}], roof:{def,minX,minZ,maxX,maxZ}, prisonBreak, sappers}. Hostiles get fixed skills, only the named apparel and an assault lord (a sapper one with sappers, #1149). Replies each staged pawn read back from the map (with worn apparel ids) and a name-free digest. action read instead replies every pawn's cell, side, downed/dead state, current job (def, playerForced, target cell or thing), drafted and fire-at-will, worn shield energy, hediff defs, every player door's hold-open and forbidden flag, and the tick.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Fixture spec JSON (action stage).")] string spec = "{}",
            [ToolParameter(Description = "stage (default), read, or tick: run ticks synchronous game ticks on the paused game, then read.")] string action = "stage",
            [ToolParameter(Description = "Ticks for action tick, 1..2000.")] int ticks = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                if (float.IsNaN(AcceptanceWorld.Lab)) throw new InvalidOperationException("test/lab_stage needs a lab map (test/lab_start first).");
                if (action == "tick")
                {
                    if (ticks < 1 || ticks > MaxTicks) throw new ArgumentException($"ticks must be within 1..{MaxTicks}.");
                    if (!Find.TickManager.Paused) throw new InvalidOperationException("tick needs a paused game.");
                    for (var i = 0; i < ticks; i++) Find.TickManager.DoSingleTick();
                    action = "read";
                }
                if (action == "read")
                    return new { success = true, tick = Find.TickManager.TicksGame, pawns = map.mapPawns.AllPawns.Where(p => p.Spawned || p.Corpse?.Spawned == true).Select(p => new {
                        id = p.GetUniqueLoadID(), side = p.Faction == Faction.OfPlayer ? "colonist" : "hostile", x = p.PositionHeld.x, z = p.PositionHeld.z, downed = p.Downed, dead = p.Dead,
                        fleeing = !p.Dead && (p.MentalStateDef == MentalStateDefOf.PanicFlee || p.CurJobDef == JobDefOf.Flee || p.CurJobDef == JobDefOf.FleeAndCower),
                        job = p.CurJobDef?.defName, playerForced = p.CurJob?.playerForced == true, jobCell = p.CurJob == null || p.CurJob.targetA.HasThing ? null : new { x = p.CurJob.targetA.Cell.x, z = p.CurJob.targetA.Cell.z },
                        jobThing = p.CurJob?.targetA.Thing?.GetUniqueLoadID(), lordJob = p.GetLord()?.LordJob?.GetType().Name, lordToil = p.GetLord()?.CurLordToil?.GetType().Name, drafted = p.Drafted, fireAtWill = p.drafter?.FireAtWill,
                        shield = p.apparel?.WornApparel.Select(a => a.GetComp<CompShield>()).FirstOrDefault(c => c != null)?.Energy,
                        area = p.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label, areaCells = p.playerSettings?.AreaRestrictionInPawnCurrentMap?.TrueCount ?? 0,
                        hediffs = p.health.hediffSet.hediffs.Select(h => h.def.defName).Distinct().ToList() }).ToList(),
                        doors = map.listerBuildings.allBuildingsColonist.OfType<Building_Door>().Select(d => new { x = d.Position.x, z = d.Position.z, holdOpen = d.HoldOpen, forbidden = d.IsForbidden(Faction.OfPlayer) }).ToList(),
                        damage = LabDamageLedger.Rows(), damageDropped = LabDamageLedger.Dropped };
                if (action != "stage") throw new ArgumentException("Unknown action.");
                LabDamageLedger.Reset();
                return LabStage.Stage(map, JObject.Parse(spec ?? "{}"));
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
            [ToolParameter(Description = "Settle a flat tile without rivers, roads or tile mutators when the planet (and biome) offers one (#272).", DefaultValue = false)] bool flat = false,
            [ToolParameter(Description = "How long to wait for def loading to reach the main menu before arming (#1264).")] int timeoutMs = DebugStart.DefaultMainMenuTimeoutMs)
        {
            await DebugStart.WaitForMainMenuAsync(ctx, cancellationToken, timeoutMs).ConfigureAwait(false);
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
