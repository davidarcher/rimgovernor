using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    // The fixture primitives every fixture build carries (#3393), implemented once: the current and paused map
    // guards, "x,z;x,z" cell lists, site search, area clearing, cell writes, wall rooms, pawn generation, work and
    // timetable loops, the lab hostiles' faction and arming, and the staging digest. test/stage (StageFixture)
    // composes them from a typed spec; the older single-purpose fixtures call them directly. Also here: the blank lab
    // (LabStart), the starter hut (FixtureHut) and the frozen needs (FrozenNeeds) other fixtures share.
    public static class FixtureKit
    {
        public static Map CurrentMap() => Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");

        // A setup that must not race the clock.
        public static Map PausedMap()
        {
            var map = CurrentMap();
            if (!Find.TickManager.Paused) throw new InvalidOperationException("A paused map is required.");
            return map;
        }

        // Def names arrive as strings from the acceptance harness, across the process boundary, so there is no typed
        // route to the def: resolve by name and refuse an unknown one.
        public static T Def<T>(string name) where T : Def =>
            DefDatabase<T>.GetNamedSilentFail(name ?? "") ?? throw new ArgumentException($"No {typeof(T).Name} named {name}.");

        // The stuff for def: the named one, or the def's default when name is empty; null for an unstuffed def.
        public static ThingDef Stuff(ThingDef def, string name) =>
            !def.MadeFromStuff ? null : string.IsNullOrEmpty(name) ? GenStuff.DefaultStuffFor(def) : Def<ThingDef>(name);

        // "x,z;x,z" (spaces allowed) as cells; an empty string is no cells and a malformed pair refuses.
        public static List<IntVec3> Cells(string text)
        {
            var cells = new List<IntVec3>();
            foreach (var pair in (text ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var xz = pair.Split(',');
                if (xz.Length != 2 || !int.TryParse(xz[0].Trim(), out var x) || !int.TryParse(xz[1].Trim(), out var z))
                    throw new ArgumentException($"Expected x,z cells joined by ';', got \"{pair}\".");
                cells.Add(new IntVec3(x, 0, z));
            }
            return cells;
        }

        // A fixture contract record as a tool reply: in the contract's System.Text.Json shape, since the host would
        // re-serialise a record with Newtonsoft.
        public static Newtonsoft.Json.Linq.JObject Reply<T>(T value) =>
            Newtonsoft.Json.Linq.JObject.Parse(RimGovernor.Fixtures.Contracts.FixtureJson.Serialize(value));

        // The spawned thing (a pawn too) with this load id, as a StageReply names it; none refuses.
        public static T Spawned<T>(Map map, string id) where T : Thing =>
            map.listerThings.AllThings.OfType<T>().FirstOrDefault(t => t.GetUniqueLoadID() == id)
                ?? map.mapPawns.AllPawnsSpawned.OfType<T>().FirstOrDefault(p => p.GetUniqueLoadID() == id)
                ?? throw new ArgumentException($"No spawned {typeof(T).Name} {id}.");

        public static IntVec3 InBounds(Map map, IntVec3 cell) =>
            cell.InBounds(map) ? cell : throw new ArgumentException($"Cell {cell.x},{cell.z} is out of bounds.");

        // The first free colonist by thing id that is neither dead, downed, drafted nor in a mental state.
        public static Pawn FirstFreeColonist(Map map) =>
            map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState)
                .OrderBy(p => p.thingIDNumber).FirstOrDefault() ?? throw new InvalidOperationException("No free colonist on the map.");

        // Site search: a width x height rect cornered at corner, or with a radius the first cell radially nearest it
        // whose rect lies in bounds and passes ok at every cell. None refuses.
        public static CellRect FindSite(Map map, IntVec3 corner, int width, int height, int radius, Func<IntVec3, bool> ok)
        {
            if (width < 1 || height < 1) throw new ArgumentException("A site needs a positive width and height.");
            bool Fits(IntVec3 c) { var rect = new CellRect(c.x, c.z, width, height); return rect.InBounds(map) && rect.Cells.All(ok); }
            if (radius <= 0)
                return Fits(corner) ? new CellRect(corner.x, corner.z, width, height) : throw new InvalidOperationException($"The {width}x{height} site at {corner.x},{corner.z} is off the map or not as required.");
            foreach (var c in GenRadial.RadialCellsAround(corner, radius, true))
                if (Fits(c)) return new CellRect(c.x, c.z, width, height);
            throw new InvalidOperationException($"No {width}x{height} site as required within {radius} of {corner.x},{corner.z}.");
        }

        // Clear area. Things removes every non-pawn thing touching the rect (natural rock included; an indestructible
        // thing is despawned), and repeats while a destroyed holder (a casket, a crate) drops what it held. Roofs
        // also drops the pending collapses the removed rock queued.
        public static void Clear(Map map, CellRect rect, bool things, bool pawns, bool zones, bool roofs, bool fog, bool snow, bool home)
        {
            var whole = rect.minX <= 0 && rect.minZ <= 0 && rect.maxX >= map.Size.x - 1 && rect.maxZ >= map.Size.z - 1;
            List<Thing> Left()
            {
                var source = whole ? map.listerThings.AllThings.Concat(map.mapPawns.AllPawnsSpawned) : rect.Cells.Where(c => c.InBounds(map)).SelectMany(c => c.GetThingList(map));
                return source.Where(t => !t.Destroyed && (t is Pawn ? pawns : things)).Distinct().ToList();
            }
            for (var pass = 0; ; pass++)
            {
                var left = Left();
                if (left.Count == 0) break;
                if (pass == 10) throw new InvalidOperationException($"{left.Count} things survived 10 clearing passes, e.g. {left[0].def.defName}.");
                foreach (var pawn in left.OfType<Pawn>())
                    if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
                foreach (var thing in left.Where(t => !(t is Pawn)))
                {
                    if (thing.Destroyed) continue;
                    if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                    else if (thing.Spawned) thing.DeSpawn();
                }
            }
            if (zones)
            {
                if (whole) foreach (var zone in map.zoneManager.AllZones.ToList()) zone.Delete();
                else foreach (var c in rect.Cells) c.GetZone(map)?.RemoveCell(c);
            }
            if (whole && fog) map.fogGrid.ClearAllFog();
            if (whole && home) map.areaManager.Home.Clear();
            foreach (var c in rect.Cells)
            {
                if (!c.InBounds(map)) continue;
                if (roofs) map.roofGrid.SetRoof(c, null);
                if (snow) map.snowGrid.SetDepth(c, 0f);
                if (!whole && fog) map.fogGrid.Unfog(c);
                if (!whole && home) map.areaManager.Home[c] = false;
            }
            if (roofs) map.roofCollapseBuffer.Clear();
        }

        // Set terrain, removing any foundation under it.
        public static void SetTerrain(Map map, IntVec3 cell, TerrainDef terrain)
        {
            if (map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] != null) map.terrainGrid.RemoveFoundation(cell, false);
            map.terrainGrid.SetTerrain(cell, terrain);
        }

        // A finished building of def owned by faction (null: no one, or the def cannot have one) at cell.
        public static Thing Spawn(Map map, ThingDef def, ThingDef stuff, IntVec3 cell, Faction faction, Rot4 rotation)
        {
            var thing = ThingMaker.MakeThing(def, stuff);
            if (faction != null && def.CanHaveFaction) thing.SetFaction(faction);
            return GenSpawn.Spawn(thing, cell, map, rotation);
        }

        public sealed class BuiltRoom
        {
            public List<Thing> Walls = new List<Thing>();
            public List<Thing> Doors = new List<Thing>();
            public List<IntVec3> Interior = new List<IntVec3>();
        }

        // Build room: a ring of finished player walls of stuff around rect with a door of stuff at each door cell
        // (on the ring, not a corner), and with roof a constructed roof over the interior; regions are rebuilt so the
        // room exists at once.
        public static BuiltRoom BuildRoom(Map map, CellRect rect, ThingDef stuff, ICollection<IntVec3> doors, bool roof)
        {
            if (rect.Width < 3 || rect.Height < 3 || !rect.InBounds(map)) throw new ArgumentException($"A room needs an in-bounds rect of at least 3x3, got {rect}.");
            foreach (var door in doors)
                if (!rect.IsOnEdge(door) || rect.IsCorner(door)) throw new ArgumentException($"Door {door.x},{door.z} is not on the room's ring.");
            var built = new BuiltRoom();
            foreach (var c in rect.EdgeCells)
            {
                if (doors.Contains(c)) built.Doors.Add(Spawn(map, ThingDefOf.Door, stuff, c, Faction.OfPlayer, Rot4.North));
                else built.Walls.Add(Spawn(map, ThingDefOf.Wall, stuff, c, Faction.OfPlayer, Rot4.North));
            }
            built.Interior = rect.ContractedBy(1).Cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            if (roof) foreach (var c in built.Interior) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            return built;
        }

        public static void RemoveBadHediffs(Pawn pawn)
        {
            foreach (var hediff in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(hediff);
        }

        public static void RemoveTraits(Pawn pawn)
        {
            foreach (var trait in pawn.story.traits.allTraits.ToList()) pawn.story.traits.RemoveTrait(trait);
        }

        // Every work type the pawn can do at priority.
        public static void SetAllWork(Pawn pawn, int priority)
        {
            pawn.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
            foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, priority);
        }

        public static void SetWork(Pawn pawn, WorkTypeDef work, int priority)
        {
            if (priority > 0 && pawn.WorkTypeIsDisabled(work)) throw new ArgumentException($"{pawn.LabelShort} cannot do {work.defName}.");
            pawn.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
            pawn.workSettings.SetPriority(work, priority);
        }

        public static void SetTimetable(Pawn pawn, TimeAssignmentDef assignment)
        {
            if (assignment == null) throw new ArgumentException("That time assignment needs an expansion this game lacks.");
            for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, assignment);
        }

        // Pirates when the world has them, else the first hostile humanlike faction by def name, so the choice does
        // not depend on list order.
        public static Faction HostileFaction()
        {
            return Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer && !f.def.hidden && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer))
                .OrderBy(f => f.def == FactionDefOf.Pirate ? 0 : 1).ThenBy(f => f.def.defName).FirstOrDefault()
                ?? throw new InvalidOperationException("No hostile humanlike faction for staged hostiles.");
        }

        // The pawn's equipment becomes weapon (none when empty), of normal quality.
        public static void Arm(Pawn pawn, string weapon, string stuffName)
        {
            pawn.equipment.DestroyAllEquipment();
            if (string.IsNullOrEmpty(weapon)) return;
            var def = Def<ThingDef>(weapon);
            var thing = (ThingWithComps)ThingMaker.MakeThing(def, Stuff(def, stuffName));
            thing.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Outsider);
            pawn.equipment.AddEquipment(thing);
        }

        // What a staging controls, one row per thing and pawn: a pawn is its side, kind, cell, weapon, Shooting and
        // Melee and apparel count. Generated names and backstories (and the non-combat skills a backstory disables)
        // differ between stagings in one world.
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

        public static string Digest(IEnumerable<string> rows)
        {
            var hash = 2166136261u;
            foreach (var row in rows) { foreach (var ch in row) hash = (hash ^ ch) * 16777619u; hash = (hash ^ '|') * 16777619u; }
            return hash.ToString("x8");
        }

        // Research: finish the project itself (prerequisites untouched), with no dialog or letter.
        public static void FinishResearch(ResearchProjectDef project)
        {
            if (!project.IsFinished) Find.ResearchManager.FinishProject(project, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
        }

        // Research: set an unfinished project's progress to fraction of its base cost. ResearchManager has no public
        // progress setter, so its private progress table is written by name.
        public static void SetResearchProgress(ResearchProjectDef project, float fraction)
        {
            if (fraction < 0f || fraction >= 1f) throw new ArgumentException($"Research {project.defName}: progress must be within 0..1 (exclusive of 1; finish it instead).");
            if (project.IsFinished) throw new InvalidOperationException($"Research {project.defName} is already finished.");
            var progress = AccessTools.Field(typeof(ResearchManager), "progress")?.GetValue(Find.ResearchManager) as Dictionary<ResearchProjectDef, float>
                ?? throw new InvalidOperationException("ResearchManager.progress is unavailable.");
            progress[project] = project.baseCost * fraction;
        }

        // World: pin the outdoor and seasonal temperature (and hold the weather) through the lab start's patches.
        public static void PinOutdoorTemperature(float celsius)
        {
            if (float.IsNaN(celsius) || celsius < -100f || celsius > 100f) throw new ArgumentException($"Outdoor temperature {celsius} is not within -100..100.");
            LabStart.EnsurePatched();
            AcceptanceWorld.SetLab(celsius);
        }

        // World: move the game clock forward to the start of local hour on map (the next one; none when it is that
        // hour's start already).
        public static void AdvanceToHour(Map map, int hour)
        {
            if (hour < 0 || hour > 23) throw new ArgumentException($"Hour {hour} is not within 0..23.");
            var now = GenLocalDate.DayPercent(map) * GenDate.HoursPerDay;
            var ticks = (int)Math.Round(((hour - now + GenDate.HoursPerDay) % GenDate.HoursPerDay) * GenDate.TicksPerHour);
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ticks);
        }

        // Fire incident: def executed now through its own worker, forced, over the storyteller's default parms for its
        // category. A trader kind picks the first non-hostile faction carrying it. Returns the pawns that arrived.
        public static List<Pawn> FireIncident(Map map, IncidentDef def, TraderKindDef traderKind)
        {
            var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.forced = true;
            if (traderKind != null)
            {
                parms.traderKind = traderKind;
                parms.faction = Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && !f.defeated && !f.HostileTo(Faction.OfPlayer)
                    && (f.def.caravanTraderKinds.Contains(traderKind) || f.def.visitorTraderKinds.Contains(traderKind)))
                    ?? throw new InvalidOperationException($"No non-hostile faction trades as {traderKind.defName}.");
            }
            // IncidentWorker caches CanFireNow per game tick in private fields that start as tick 0 = false, so a worker
            // never asked before answers false on a tick-0 lab without checking; forget the cached tick first.
            AccessTools.Field(typeof(IncidentWorker), "lastCheckCanRunTick").SetValue(def.Worker, -1);
            if (!def.Worker.CanFireNow(parms)) throw new InvalidOperationException($"The game cannot fire {def.defName} now.");
            var before = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            if (!def.Worker.TryExecute(parms)) throw new InvalidOperationException($"{def.defName} did not apply.");
            return map.mapPawns.AllPawnsSpawned.Where(x => !before.Contains(x)).OrderBy(x => x.thingIDNumber).ToList();
        }
    }

    // The blank lab map: a fixture case spawns what it needs at known
    // coordinates on it instead of searching a random world for a site.
    // Wipe turns the loaded map into flat Soil with nothing on it but N
    // fixture-made adult colonists, pins clear weather and a fixed outdoor
    // temperature (persisted through AcceptanceWorld.LabTemperature, so a
    // reloaded lab save keeps them), and quiets the storyteller. Every draw
    // runs under a fixed Rand seed, so the same map in gives the same lab.
    public static class LabStart
    {
        public const float Temperature = 21f;
        public const int DefaultColonists = 3, MaxColonists = 12, SkillLevel = 8;
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
            FixtureKit.Clear(map, new CellRect(0, 0, map.Size.x, map.Size.z), things: true, pawns: true, zones: true, roofs: true, fog: true, snow: true, home: true);
            foreach (var cell in map.AllCells) FixtureKit.SetTerrain(map, cell, TerrainDefOf.Soil);
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
        internal static Pawn Colonist(int index)
        {
            for (var tries = 0; tries < 50; tries++)
            {
                var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.PlayerStarter,
                    forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: true, colonistRelationChanceFactor: 0f,
                    allowAddictions: false, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f, fixedGender: index % 2 == 0 ? Gender.Male : Gender.Female,
                    fixedIdeo: Faction.OfPlayer.ideos?.PrimaryIdeo, forceBaselinerChance: 1f, developmentalStages: DevelopmentalStage.Adult);
                var pawn = PawnGenerator.GeneratePawn(request);
                FixtureKit.RemoveTraits(pawn);
                pawn.Notify_DisabledWorkTypesChanged();
                if (pawn.CombinedDisabledWorkTags != WorkTags.None) { Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard); continue; }
                FixtureKit.RemoveBadHediffs(pawn);
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

    // The starter hut the ladder fixtures stage so a development rung is not
    // held behind a whole shell build: one square ring of finished player
    // wood walls with a single east door, roofed, a sleeping spot per
    // colonist along its south rows and every colonist moved inside. The
    // room is a proper enclosed Barracks/Room the facility catalog lets host
    // a workshop or laboratory bench, and the rows above the spots stay free
    // for whatever the case's ladder furnishes.
    public static class FixtureHut
    {
        public sealed class Result
        {
            public IntVec3 Origin;
            public IntVec3 Door;
            // Outward is the step from the door away from the room.
            public IntVec3 Outward;
            public Room Room;
            public List<IntVec3> Interior;
            public List<Pawn> People;
            public int SleepingSpots;

            public object Summary() => new { origin = Origin.ToString(), door = Door.ToString(), room = Room.ID, cells = Room.CellCount, sleepingSpots = SleepingSpots };
        }

        // Build raises a size x size ring (size-2 square inside) and
        // furnishes it as above. site is the south-west corner the
        // controller's own starter search chose and door the door cell
        // it chose on the ring, or mid east wall when null; the fixture never
        // searches for a site of its own. Natural rock on the ring
        // stays as wall, rock inside is cleared as mining would, and plants
        // and items anywhere in the square are removed. spots caps the
        // sleeping spots laid: negative is one per colonist, a smaller count
        // leaves the bed deficit a capacity goal then plans against. bed is
        // the bed laid (a sleeping spot when null). Throws when no site is given.
        public static Result Build(Map map, int size, IntVec3? site, IntVec3? doorCell, int spots = -1, ThingDef bed = null)
        {
            var player = Faction.OfPlayerSilentFail;
            if (player == null) throw new InvalidOperationException("No player faction.");
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed)
                .OrderBy(p => p.thingIDNumber).Take(8).ToList();
            if (people.Count < 1) throw new InvalidOperationException("No colonist to house.");
            var spotDef = bed ?? ThingDefOf.SleepingSpot;

            if (site == null) throw new InvalidOperationException("No fixture hut site given: pass the starter site (siteX/siteZ).");
            var origin = site.Value;
            var rect = new CellRect(origin.x, origin.z, size, size);
            if (!rect.InBounds(map)) throw new InvalidOperationException($"Fixture hut site {origin} runs off the map.");
            var door = doorCell ?? new IntVec3(origin.x + size - 1, 0, origin.z + size / 2);
            if (!rect.IsOnEdge(door) || rect.IsCorner(door)) throw new InvalidOperationException($"Fixture hut door {door} is not on its ring.");
            var outward = door.x == rect.maxX ? IntVec3.East : door.x == rect.minX ? IntVec3.West : door.z == rect.maxZ ? IntVec3.North : IntVec3.South;
            foreach (var c in rect.Cells) {
                foreach (var t in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) t.Destroy(DestroyMode.Vanish);
                var edifice = c.GetEdifice(map);
                var rock = edifice != null && edifice.def.building?.isNaturalRock == true;
                if (rock && rect.IsOnEdge(c) && c != door) {
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    continue;
                }
                edifice?.Destroy(DestroyMode.Vanish);
                if (c == door) FixtureKit.Spawn(map, ThingDefOf.Door, ThingDefOf.WoodLog, c, player, Rot4.North);
                else if (rect.IsOnEdge(c)) FixtureKit.Spawn(map, ThingDefOf.Wall, ThingDefOf.WoodLog, c, player, Rot4.North);
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            var interior = rect.ContractedBy(1).Cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            var width = size - 2;
            // A sleeping spot is a 1x2 footprint: one per cell of the south
            // row (rows 1-2), the overflow from the west end of row 3 (rows
            // 3-4), so none overlaps and the rows above stay free.
            var wanted = spots < 0 ? people.Count : Math.Max(0, Math.Min(spots, people.Count));
            var spotCells = interior.Take(width).Concat(interior.Skip(2 * width)).Take(wanted).ToList();
            foreach (var spotCell in spotCells) {
                var spot = ThingMaker.MakeThing(spotDef, spotDef.MadeFromStuff ? ThingDefOf.WoodLog : null);
                spot.SetFaction(player); GenSpawn.Spawn(spot, spotCell, map, Rot4.North, WipeMode.Vanish);
                if (spot.Position != spotCell) throw new InvalidOperationException($"Sleeping spot landed at {spot.Position}, not {spotCell}.");
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            var room = interior[interior.Count / 2].GetRoom(map);
            if (room == null || !room.ProperRoom || room.TouchesMapEdge || room.OpenRoofCount > 0)
                throw new InvalidOperationException($"Fixture hut is not an enclosed roofed room: room={room?.ID} proper={room?.ProperRoom} edge={room?.TouchesMapEdge} openRoof={room?.OpenRoofCount} cells={room?.CellCount}");
            room.Temperature = 21f;
            var laid = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Count(b => b.GetRoom() == room);
            if (laid != wanted) throw new InvalidOperationException($"{laid} sleeping spots stand in the hut, not {wanted}.");
            var free = interior.Skip(interior.Count - people.Count).ToList();
            for (int i = 0; i < people.Count; i++) {
                var pawn = people[i];
                pawn.jobs?.StopAll();
                pawn.Position = free[i]; pawn.Notify_Teleported(true, true);
            }
            return new Result { Origin = origin, Door = door, Outward = outward, Room = room, Interior = interior, People = people, SleepingSpots = laid };
        }

        // Site is the cell a fixture's x/z arguments name, null when x is
        // negative (Build then refuses: no site given).
        public static IntVec3? Site(int x, int z) => x < 0 ? (IntVec3?)null : new IntVec3(x, 0, z);

        // DropOutside places count of def two cells outside the door,
        // unforbidden: the materials a rung costs that supply is not asked
        // to find on the tribal baseline.
        public static int DropOutside(Map map, Result hut, ThingDef def, int count)
        {
            var placed = 0;
            while (placed < count) {
                var stack = Math.Min(def.stackLimit, count - placed);
                var thing = ThingMaker.MakeThing(def); thing.stackCount = stack;
                if (!GenPlace.TryPlaceThing(thing, hut.Door + hut.Outward * 2, map, ThingPlaceMode.Near)) throw new InvalidOperationException("No drop site for " + def.defName);
                thing.SetForbidden(false, false);
                placed += stack;
            }
            return placed;
        }

        // SpawnInside spawns a finished player building of def on the first
        // interior cell (south-west first, rows above the sleeping spots)
        // whose footprint is free and standable, facing north.
        public static Thing SpawnInside(Map map, Result hut, ThingDef def)
        {
            var sites = hut.Interior.Where(c => GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map).Accepted
                && GenAdj.OccupiedRect(c, Rot4.North, def.size).Cells.All(x => hut.Interior.Contains(x) && x.Standable(map) && x.GetFirstBuilding(map) == null && x.GetFirstPawn(map) == null)).ToList();
            if (sites.Count == 0) throw new InvalidOperationException("No site inside the fixture hut for " + def.defName);
            return FixtureKit.Spawn(map, def, GenStuff.DefaultStuffFor(def), sites[0], Faction.OfPlayer, Rot4.North);
        }
    }

    // A colonist that wanders off to eat, sleep or break mid-assertion reruns a
    // harness for a reason unrelated to it. Freezing pins every colonist need at
    // its maximum after each needs tick except the ones the scenario keeps live
    // (Food for a cooking harness, Rest for a sleeping one). This bends
    // "preserve normal game rules" for the test game only: test/freeze_needs
    // (FreezeNeedsFixture) applies it, never rimgovernor-native, and the reply
    // names what was frozen so a report cannot pass silently because nobody
    // ever ate or slept.
    public static class FrozenNeeds
    {
        private static bool patched;
        private static Game game;
        private static HashSet<string> keep = new HashSet<string>();

        public static bool Active => game != null && ReferenceEquals(game, Current.Game);
        public static IEnumerable<string> Keep => keep.OrderBy(k => k);

        public static object Apply(IEnumerable<string> keepDefs)
        {
            var set = new HashSet<string>(keepDefs ?? Enumerable.Empty<string>());
            foreach (var name in set) FixtureKit.Def<NeedDef>(name);
            keep = set; game = Current.Game;
            EnsurePatched();
            var pinned = 0;
            foreach (var pawn in Colonists()) pinned += Pin(pawn);
            return new { success = true, frozen = true, keep = Keep.ToArray(), colonists = Colonists().Count(), pinned,
                needs = Inspect() };
        }

        public static object Release()
        {
            game = null; keep = new HashSet<string>();
            return new { success = true, frozen = false, needs = Inspect() };
        }

        public static object[] Inspect()
        {
            return Colonists().Select(p => (object)new {
                pawn = p.GetUniqueLoadID(), name = p.LabelShort,
                needs = p.needs.AllNeeds.Select(n => new { def = n.def.defName, level = n.CurLevelPercentage, frozen = Active && !keep.Contains(n.def.defName) }).ToArray(),
            }).ToArray();
        }

        private static IEnumerable<Pawn> Colonists()
        {
            if (Current.Game == null) return Enumerable.Empty<Pawn>();
            return Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).Where(p => p.needs != null);
        }

        private static int Pin(Pawn pawn)
        {
            var pinned = 0;
            foreach (var need in pawn.needs.AllNeeds)
            {
                if (keep.Contains(need.def.defName)) continue;
                need.CurLevelPercentage = 1f;
                pinned++;
            }
            return pinned;
        }

        private static void EnsurePatched()
        {
            if (patched) return;
            // Need.NeedInterval is overridden per need without calling base,
            // so the pin runs after the whole tracker's interval instead.
            new Harmony("rimgovernor.test.freeze-needs").Patch(
                AccessTools.Method(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.NeedsTrackerTickInterval)),
                postfix: new HarmonyMethod(typeof(FrozenNeeds), nameof(AfterTick)));
            patched = true;
        }

        private static void AfterTick(Pawn ___pawn)
        {
            if (!Active || ___pawn == null || ___pawn.needs == null || !___pawn.IsColonist || ___pawn.Faction != Faction.OfPlayer) return;
            Pin(___pawn);
        }
    }
}
