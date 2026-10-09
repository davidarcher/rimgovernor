using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Issue #607: the layout/grid case proves the tiered colony layout on
    // the tribal baseline. Prepare finishes Stonecutting so the tech tier
    // reads Masonry, stages the starter hut (FixtureHut) whose south-west
    // corner anchors the layout, and drops wood beside
    // its door; the field the controller then plans is the case's own.
    // Audit reads every finished player wall ring and growing zone back
    // with their cells so the case checks both footprints,
    // and every wall, door, blueprint and frame with its stuff, so the case
    // reads the tier's wall stuff and door def back natively (#637).
    public sealed class LayoutGridFixture
    {
        [Tool("test/layout_grid_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: finish the named research (default Stonecutting) with its prerequisites, build one roofed wood hut with sleepingSpots sleeping spots, drop wood and stoneBlocks blocks of the map's own stone beside its door and move every colonist inside. Plans no field: the field planner sites its own.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "ResearchProjectDef to finish (default Stonecutting).")] string project = "Stonecutting",
            [ToolParameter(Description = "Sleeping spots to lay in the hut; negative lays one per colonist, fewer leaves a bed deficit the capacity goal plans against.")] int sleepingSpots = -1,
            [ToolParameter(Description = "Stone blocks of the map's own stone to drop beside the door; 0 drops none.")] int stoneBlocks = 0,
            [ToolParameter(Description = "South-west corner x of the 9x9 hut the controller's starter search chose (required).")] int siteX = -1,
            [ToolParameter(Description = "South-west corner z of the hut.")] int siteZ = -1,
            [ToolParameter(Description = "Door cell x on the hut's ring; negative puts the door mid east wall.")] int doorX = -1,
            [ToolParameter(Description = "Door cell z on the hut's ring.")] int doorZ = -1,
            [ToolParameter(Description = "Bedroom start (#838): enable Construction on every able colonist, lay wooden beds instead of sleeping spots, give each colonist one, drop 120 survival meals and raise every shell blueprint at once.")] bool builders = false,
            [ToolParameter(Description = "Suite start (#1221): the first uncoupled hut colonist turns Greedy (Ascetic removed), reported as greedyPawn.")] bool greedy = false,
            [ToolParameter(Description = "Expansion start (#1271, with builders): a walled, roofed bedroom per single colonist and per couple (a double bed), each with an end table, on the planned bedroom rooms, each colonist owning and lying in its Bed, the hut beds removed, a campfire with a simple-meal bill and a stockpile in the hut, and two armed colonists.")] bool expansion = false,
            [ToolParameter(Description = "Planned bedroom rooms for expansion: 'x,z,width,height,doorX,doorZ' (interior and door cell) per room, joined by ';'; at least one per single colonist and couple.")] string bedrooms = "")
        {
            var name = string.IsNullOrEmpty(project) ? "Stonecutting" : project;
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var def = DefDatabase<ResearchProjectDef>.GetNamed(name);
                Finish(def);
                var hut = FixtureHut.Build(map, 9, FixtureHut.Site(siteX, siteZ), FixtureHut.Site(doorX, doorZ), sleepingSpots, builders ? "Bed" : "SleepingSpot");
                FixtureHut.DropOutside(map, hut, ThingDefOf.WoodLog, 4 * ThingDefOf.WoodLog.stackLimit);
                // A bedroom case (#838) starts once everyone owns a hut bed,
                // with food for the stage to leave Foothold.
                var owned = 0;
                if (builders) {
                    var spots = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(b => b.GetRoom() == hut.Room).ToList();
                    for (int i = 0; i < spots.Count && i < hut.People.Count; i++)
                        if (spots[i].CompAssignableToPawn.CanAssignTo(hut.People[i]).Accepted) { spots[i].CompAssignableToPawn.TryAssignPawn(hut.People[i]); owned++; }
                    FixtureHut.DropOutside(map, hut, ThingDefOf.MealSurvivalPack, 120);
                }
                if (builders) {
                    // A planned bedroom is furnished with a real Bed, which needs
                    // ComplexFurniture; a sleeping spot never counts as suitable.
                    var furniture = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("ComplexFurniture");
                    if (furniture != null && !furniture.IsFinished) Find.ResearchManager.FinishProject(furniture, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
                    ArmInstantShells();
                }
                var enabled = 0;
                if (builders) foreach (var p in map.mapPawns.FreeColonistsSpawned) {
                    if (p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || p.workSettings.GetPriority(WorkTypeDefOf.Construction) > 0) continue;
                    p.workSettings.SetPriority(WorkTypeDefOf.Construction, 3);
                    enabled++;
                }
                string greedyPawn = null;
                if (greedy && hut.People.Count > 0) {
                    // A colonist with a lover, fiance or spouse is never split
                    // into a single bedroom (#838), so never qualifies for a
                    // suite (#1257): the first uncoupled one turns Greedy.
                    var p = hut.People.FirstOrDefault(x => !LovePartnerRelationUtility.HasAnyLovePartner(x)) ?? hut.People[0];
                    if (p.story.traits.GetTrait(DefDatabase<TraitDef>.GetNamed("Ascetic")) is Trait ascetic) p.story.traits.RemoveTrait(ascetic);
                    if (!p.story.traits.HasTrait(TraitDefOf.Greedy)) p.story.traits.GainTrait(new Trait(TraitDefOf.Greedy));
                    greedyPawn = p.GetUniqueLoadID();
                }
                ThingDef blocks = null;
                if (stoneBlocks > 0) {
                    blocks = StoneBlocks(map);
                    FixtureHut.DropOutside(map, hut, blocks, stoneBlocks);
                }
                object expanded = null;
                if (expansion) {
                    if (!builders) throw new ArgumentException("expansion builds on the builders start.");
                    expanded = Expand(map, hut, bedrooms);
                    owned = map.mapPawns.FreeColonistsSpawned.Count(p => p.ownership?.OwnedBed != null);
                }
                return new { success = true, project = name, finished = def.IsFinished, hut = hut.Summary(),
                    hutOrigin = new { x = hut.Origin.x, z = hut.Origin.z }, hutSize = 9,
                    sleepingSpots = hut.SleepingSpots, colonists = hut.People.Count,
                    stoneBlocks = blocks == null ? 0 : stoneBlocks, stoneBlocksDef = blocks?.defName, builders = enabled, ownedBeds = owned,
                    greedyPawn, expansion = expanded, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Expand (#1271) stages the colony one review short of MaintainHousing's
        // expansion phase: every single colonist in a bedroom of their own and
        // every couple sharing one with a double bed, each with an end table, on the planned bedroom rooms
        // (so no bedroom step is owed and the plan's other rooms stay free),
        // each lying in its bed (so the sleeping review records the use),
        // indoor capacity exactly the colonists (the hut beds go), and Foothold's remaining exits met (a cooking bill, a food
        // stockpile, two armed fighters).
        private static object Expand(Map map, FixtureHut.Result hut, string bedrooms)
        {
            var player = Faction.OfPlayer;
            var people = hut.People;
            // Couples (reciprocal love partners in the hut) share a room.
            var groups = new List<List<Pawn>>();
            foreach (var p in people) {
                if (groups.Any(g => g.Contains(p))) continue;
                var partner = LovePartnerRelationUtility.ExistingMostLikedLovePartner(p, false);
                var paired = partner != null && people.Contains(partner) && !groups.Any(g => g.Contains(partner))
                    && LovePartnerRelationUtility.ExistingMostLikedLovePartner(partner, false) == p;
                groups.Add(paired ? new List<Pawn> { p, partner } : new List<Pawn> { p });
            }
            var rooms = (bedrooms ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Split(',').Select(int.Parse).ToArray()).ToList();
            if (rooms.Count < groups.Count || rooms.Any(r => r.Length != 6))
                throw new ArgumentException($"{rooms.Count} planned bedrooms for {groups.Count} households: '{bedrooms}'.");
            foreach (var bed in map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(b => b.GetRoom() == hut.Room).ToList())
                bed.Destroy(DestroyMode.Vanish);
            var wallDef = ThingDef.Named("Wall");
            var doorDef = ThingDef.Named("Door");
            var bedDef = ThingDef.Named("Bed");
            var doubleBedDef = ThingDef.Named("DoubleBed");
            var endTableDef = ThingDef.Named("EndTable");
            var tables = 0;
            var owned = new List<KeyValuePair<Pawn, Building_Bed>>();
            var doors = new List<IntVec3>();
            var interiors = new List<CellRect>();
            for (int g = 0; g < groups.Count; g++) {
                var r = rooms[g];
                var interior = new CellRect(r[0], r[1], r[2], r[3]);
                var door = new IntVec3(r[4], 0, r[5]);
                var ring = interior.ExpandedBy(1);
                foreach (var c in ring.Cells) {
                    if (!c.InBounds(map)) throw new InvalidOperationException($"Planned bedroom {interior} runs off the map.");
                    var edge = !interior.Contains(c);
                    var edifice = c.GetEdifice(map);
                    // A wall shared with a neighbouring bedroom already stands.
                    if (edge && c != door && edifice != null && edifice.def == wallDef && edifice.Faction == player) continue;
                    foreach (var t in c.GetThingList(map).ToList())
                        if (t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building) t.Destroy(DestroyMode.Vanish);
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    if (!edge) continue;
                    var b = (Building)ThingMaker.MakeThing(c == door ? doorDef : wallDef, ThingDefOf.WoodLog);
                    b.SetFaction(player); GenSpawn.Spawn(b, c, map);
                }
                doors.Add(door);
                interiors.Add(interior);
                // One bed per household, head north, farthest from the door,
                // clear of the door's neighbours: a Bed for a single
                // colonist, a DoubleBed both partners own for a couple (a
                // couple without one is packed onto a double bed first, #843).
                var def = groups[g].Count == 2 ? doubleBedDef : bedDef;
                var sites = interior.Cells.Where(c => GenAdj.OccupiedRect(c, Rot4.North, def.size).Cells.All(o => interior.Contains(o) && !o.AdjacentToCardinal(door)))
                    .OrderByDescending(c => c.DistanceToSquared(door)).ToList();
                if (sites.Count == 0) throw new InvalidOperationException($"Planned bedroom {interior} holds no {def.defName} site.");
                var bedThing = (Building_Bed)ThingMaker.MakeThing(def, ThingDefOf.WoodLog);
                bedThing.SetFaction(player); GenSpawn.Spawn(bedThing, sites[0], map, Rot4.North, WipeMode.Vanish);
                foreach (var p in groups[g]) owned.Add(new KeyValuePair<Pawn, Building_Bed>(p, bedThing));
                // An end table: the only piece a Camp/Masonry 3x4 bedroom's
                // template holds besides the bed, so no room quality upgrade
                // (#814) holds MaintainHousing in its bedroom phase.
                var bedCells = bedThing.OccupiedRect();
                var table = interior.Cells.Where(c => !bedCells.Contains(c) && !c.AdjacentToCardinal(door))
                    .OrderBy(c => bedCells.ClosestCellTo(c).DistanceToSquared(c)).ThenByDescending(c => c.DistanceToSquared(door)).FirstOrDefault();
                if (!table.IsValid || !interior.Contains(table)) throw new InvalidOperationException($"Planned bedroom {interior} holds no EndTable site.");
                var endTable = ThingMaker.MakeThing(endTableDef, ThingDefOf.WoodLog);
                endTable.SetFaction(player); GenSpawn.Spawn(endTable, table, map, Rot4.North, WipeMode.Vanish);
                tables++;
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            var asleep = 0;
            for (int i = 0; i < owned.Count; i++) {
                var p = owned[i].Key; var bed = owned[i].Value;
                var room = bed.GetRoom();
                if (room == null || !room.ProperRoom || room.OpenRoofCount > 0) throw new InvalidOperationException($"Bedroom {i} at {bed.Position} is not an enclosed roofed room.");
                room.Temperature = 21f;
                if (!bed.CompAssignableToPawn.CanAssignTo(p).Accepted) throw new InvalidOperationException($"{p.LabelShort} cannot own the bed at {bed.Position}.");
                bed.CompAssignableToPawn.TryAssignPawn(p);
                p.jobs?.StopAll();
                p.Position = RestUtility.GetBedSleepingSlotPosFor(p, bed); p.Notify_Teleported(true, true);
                // Tired enough to stay in bed past the first reviews.
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 0.1f;
                p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
                if (p.CurrentBed() == bed) asleep++;
            }
            var beds = owned.Select(o => o.Value).Distinct().Count();
            var roles = owned.Select(o => o.Value.GetRoom()?.Role?.defName).ToList();
            var campfire = FixtureHut.SpawnInside(map, hut, ThingDef.Named("Campfire"));
            ((IBillGiver)campfire).BillStack.AddBill(DefDatabase<RecipeDef>.GetNamed("CookMealSimple").MakeNewBill());
            campfire.TryGetComp<CompRefuelable>()?.Refuel(1000f);
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in hut.Interior.Where(c => c.GetFirstBuilding(map) == null)) zone.AddCell(c);
            // No spare place (#1271): the expansion step furnishes any free,
            // unzoned indoor cell with a spot before it raises a ring, so the
            // hut is all stockpile and each bedroom's free floor an empty
            // (store-nothing) stockpile.
            var filled = 0;
            foreach (var interior in interiors) {
                var filler = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                filler.settings.filter.SetDisallowAll();
                map.zoneManager.RegisterZone(filler);
                foreach (var c in interior.Cells.Where(c => c.GetFirstBuilding(map) == null)) { filler.AddCell(c); filled++; }
            }
            var armed = people.Count(p => p.equipment?.Primary != null);
            var weapon = ThingDef.Named("MeleeWeapon_Club");
            foreach (var p in people.Where(p => p.equipment != null && p.equipment.Primary == null && !p.WorkTagIsDisabled(WorkTags.Violent))) {
                if (armed >= 2) break;
                p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(weapon, ThingDefOf.WoodLog));
                armed++;
            }
            var couples = groups.Count(g => g.Count == 2);
            return new { beds, tables, rooms = groups.Count, asleep, roles,
                doors = doors.Select(d => new { x = d.x, z = d.z }).ToList(), campfire = campfire.Position.ToString(),
                stockpile = zone.Cells.Count, bedroomFloorZoned = filled, armed, couples };
        }


        // Rooms (#2076) stages already-built rooms on the planned rectangles a
        // running case read from the journal: a wood-walled, roofed room with
        // its door per interior, so the census holds an enclosed room on each
        // planned centre. Nothing is furnished.
        [Tool("test/layout_rooms_stage", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture (#2076): raise a finished, roofed, wood-walled room with a wood door round each given interior, clearing plants, items and buildings off the ground first. A wall a neighbouring room already holds stays.")]
        public async Task<object> Rooms(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "'x,z,width,height,doorX,doorZ' (interior and door cell on its wall ring) per room, joined by ';'.")] string rooms)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                var specs = (rooms ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(r => r.Split(',').Select(int.Parse).ToArray()).ToList();
                if (specs.Count == 0 || specs.Any(r => r.Length != 6)) throw new ArgumentException($"Want 'x,z,width,height,doorX,doorZ' rooms, got '{rooms}'.");
                var player = Faction.OfPlayer;
                var wallDef = ThingDef.Named("Wall");
                var doorDef = ThingDef.Named("Door");
                var raised = new List<object>();
                foreach (var r in specs) {
                    var interior = new CellRect(r[0], r[1], r[2], r[3]);
                    var door = new IntVec3(r[4], 0, r[5]);
                    var ring = interior.ExpandedBy(1);
                    if (!ring.Contains(door) || interior.Contains(door)) throw new ArgumentException($"Door {door} is not on the wall ring of {interior}.");
                    foreach (var c in ring.Cells) {
                        if (!c.InBounds(map)) throw new InvalidOperationException($"Room {interior} runs off the map.");
                        var edge = !interior.Contains(c);
                        var edifice = c.GetEdifice(map);
                        if (edge && c != door && edifice != null && edifice.def == wallDef && edifice.Faction == player) continue;
                        foreach (var t in c.GetThingList(map).ToList())
                            if (t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building) t.Destroy(DestroyMode.Vanish);
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                        if (!edge) continue;
                        var b = (Building)ThingMaker.MakeThing(c == door ? doorDef : wallDef, ThingDefOf.WoodLog);
                        b.SetFaction(player); GenSpawn.Spawn(b, c, map);
                    }
                    raised.Add(new { x = interior.minX, z = interior.minZ, width = interior.Width, height = interior.Height });
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var enclosed = specs.Select(r => {
                    var room = new IntVec3(r[0] + r[2] / 2, 0, r[1] + r[3] / 2).GetRoom(map);
                    return room != null && room.ProperRoom && room.OpenRoofCount == 0 && !room.TouchesMapEdge;
                }).ToList();
                return new { success = enclosed.All(e => e), rooms = raised, enclosed, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Suite growth (#1221): a royal title raises the pawn's bedroom
        // target past what their suite was sized for. Needs Royalty; a save
        // recorded without it holds no Empire, so one is generated.
        [Tool("test/layout_grid_title", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: give an exact colonist an Empire royal title (default Baron) without rewards or letters, generating the Empire faction when the save holds none. Requires Royalty.")]
        public async Task<object> Title(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Colonist unique load id.")] string pawn,
            [ToolParameter(Description = "RoyalTitleDef to grant (default Baron).")] string title = "Baron")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (!ModsConfig.RoyaltyActive) throw new InvalidOperationException("Royalty is not active.");
                var map = Find.CurrentMap;
                var p = map?.mapPawns.FreeColonistsSpawned.SingleOrDefault(x => x.GetUniqueLoadID() == pawn)
                    ?? throw new InvalidOperationException("No spawned colonist " + pawn + ".");
                var def = DefDatabase<RoyalTitleDef>.GetNamed(string.IsNullOrEmpty(title) ? "Baron" : title);
                var empire = Find.FactionManager.OfEmpire;
                var generated = false;
                if (empire == null) {
                    empire = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(FactionDefOf.Empire));
                    Find.FactionManager.Add(empire);
                    generated = true;
                }
                if (p.royalty == null) p.royalty = new Pawn_RoyaltyTracker(p);
                p.royalty.SetTitle(empire, def, false, false, false);
                var held = p.royalty.GetCurrentTitle(empire);
                return new { success = held == def, pawn, title = held?.defName, empireGenerated = generated, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        // StoneBlocks is the block definition of the map's own stone, the
        // stuff a Masonry shell is built from: the first natural rock type of
        // the tile, granite where the tile names none.
        private static ThingDef StoneBlocks(Map map)
        {
            foreach (var rock in Find.World.NaturalRockTypesIn(map.Tile)) {
                var blocks = DefDatabase<ThingDef>.GetNamedSilentFail("Blocks" + rock.defName);
                if (blocks != null) return blocks;
            }
            var granite = DefDatabase<ThingDef>.GetNamedSilentFail("BlocksGranite");
            if (granite == null) throw new InvalidOperationException("No stone block def available in this ruleset.");
            return granite;
        }

        // InstantShells (#838): once armed, every player wall, door, autodoor
        // or embrasure blueprint is raised through the game's own path
        // (blueprint to frame, materials in, CompleteConstruction by a
        // colonist) within a tick of being placed, so a bedroom case watches
        // the planner's shell plan complete without minutes of hauling.
        private static bool instantShells, instantHook;

        internal static void ArmInstantShells()
        {
            instantShells = true;
            if (instantHook) return;
            new Harmony("rimgovernor.fixture.instant-shells").Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
                postfix: new HarmonyMethod(typeof(LayoutGridFixture), nameof(RaiseShells)));
            new Harmony("rimgovernor.fixture.instant-shells-jobs").Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)),
                prefix: new HarmonyMethod(typeof(LayoutGridFixture), nameof(HoldJobSearch)));
            instantHook = true;
        }

        // Completing a wall ends the worker's job, and the tracker then runs a
        // full think-tree search for the next one: thousands of searches per tick
        // on a long wall. The next tracker tick finds the pawn a job anyway.
        private static bool raising;

        private static void HoldJobSearch(ref bool startNewJob)
        {
            if (raising) startNewJob = false;
        }

        private static void RaiseShells()
        {
            if (!instantShells) return;
            raising = true;
            try { RaiseShellsNow(); } finally { raising = false; }
        }

        private static void RaiseShellsNow()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            var worker = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Dead);
            if (worker == null) return;
            var pending = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).OfType<Blueprint_Build>()
                .Where(b => b.Faction == Faction.OfPlayer && b.def.entityDefToBuild is ThingDef d && (Shell(d.defName) || d.defName == "Bed")).ToList();
            foreach (var blueprint in pending) {
                if (!blueprint.TryReplaceWithSolidThing(worker, out var solid, out _) || !(solid is Frame frame)) continue;
                foreach (var cost in frame.TotalMaterialCost()) {
                    var need = cost.count - frame.resourceContainer.TotalStackCountOfDef(cost.thingDef);
                    if (need <= 0) continue;
                    var stack = ThingMaker.MakeThing(cost.thingDef);
                    stack.stackCount = need;
                    frame.resourceContainer.TryAdd(stack, true);
                }
                frame.CompleteConstruction(worker);
            }
            // Vanilla marks an enclosed room for roofing and waits on a builder;
            // the planner sites beds only under a roof, so roof it at once too.
            foreach (var cell in map.areaManager.BuildRoof.ActiveCells.ToList())
                if (!cell.Roofed(map)) map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            // A cold night would read every bed unsafe (below the comfort band)
            // and hand the case to the temperature family; hold rooms mild.
            foreach (var room in map.regionGrid.AllRooms)
                if (!room.UsesOutdoorTemperature) room.Temperature = 21f;
        }

        [Tool("test/layout_grid_audit", Description = "Private read-only fixture: every finished player wall, door, autodoor or embrasure cell with its stuff, every such blueprint and frame with the stuff it is to be built from, and every growing zone with its cells and crop.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return new { success = false, reason = "A disposable colony map is required." };
                var walls = map.listerBuildings.allBuildingsColonist
                    .Where(b => Shell(b.def.defName))
                    .Select(b => new { x = b.Position.x, z = b.Position.z, def = b.def.defName, stuff = b.Stuff?.defName }).ToList();
                // A ring still under construction reads back the same way: the
                // stuff a blueprint or frame carries is the stuff the wall
                // becomes, so the tier's style is visible before the last wall
                // is finished.
                var planned = map.listerThings.AllThings
                    .Where(t => t.Spawned && (t.Faction == null || t.Faction.IsPlayer) && (t is Blueprint_Build || t is Frame))
                    .Select(t => new { thing = t, built = ((t as Blueprint_Build)?.def.entityDefToBuild ?? (t as Frame)?.def.entityDefToBuild) as ThingDef })
                    .Where(row => row.built != null && Shell(row.built.defName))
                    .Select(row => new { x = row.thing.Position.x, z = row.thing.Position.z, def = row.built.defName,
                        stuff = ((row.thing as Blueprint_Build)?.stuffToUse ?? (row.thing as Frame)?.Stuff)?.defName,
                        frame = row.thing is Frame }).ToList();
                var zones = map.zoneManager.AllZones.OfType<Zone_Growing>()
                    .Select(z => new { id = z.ID, loadId = z.GetUniqueLoadID(), label = z.label, crop = z.GetPlantDefToGrow()?.defName,
                        cells = z.Cells.Select(c => new { x = c.x, z = c.z }).ToList() }).ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, walls, planned, zones };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Shell names the definitions a room boundary is built from.
        private static bool Shell(string defName) =>
            defName == "Wall" || defName == "Door" || defName == "Autodoor" || defName == "Embrasure";

        private static void Finish(ResearchProjectDef project)
        {
            if (project.IsFinished) return;
            if (project.prerequisites != null) foreach (var p in project.prerequisites) Finish(p);
            Find.ResearchManager.FinishProject(project, false);
        }
    }
}
