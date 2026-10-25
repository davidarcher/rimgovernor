using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only: the wall/* cases' native construction probes.
    public sealed class GuardedConstructionFixture
    {
        [Tool("test/wall_layers", Description = "UNSAFE FOR MODEL EXECUTION. Paused disposable lab only. prepare stages a funded, unfinished three-thick 9x9 wall ring with a three-cell open gate at the map centre and normal builder work. audit reads remaining frames, standing walls and colonist escape. controls arms the completion guard as a running supervisor would, directly attempts a completion that would strand a neighbouring frame, then completes an isolated frame with a pawn standing on it and reports vanilla relocation and escape.")]
        public async Task<object> WallLayers(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "audit")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused || map.Size.x != 100 || map.Size.z != 100)
                    return Refuse("A paused 100x100 disposable lab is required.");
                var centre = map.Center;
                var ring = new List<IntVec3>();
                for (var x = -4; x <= 4; x++)
                    for (var z = -4; z <= 4; z++)
                        if ((Math.Abs(x) >= 2 || Math.Abs(z) >= 2) && !(x == 0 && z <= -2))
                            ring.Add(centre + new IntVec3(x, 0, z));
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && p.workSettings != null).ToList();
                if (pawns.Count == 0) return Refuse("No capable lab builder.");
                if (action == "prepare") {
                    foreach (var pawn in pawns) {
                        pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                        pawn.Position = centre + new IntVec3(0, 0, -7);
                        pawn.Notify_Teleported(true, true);
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                        for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                    }
                    foreach (var cell in ring) {
                        ClearWallCell(map, cell);
                        FundedFrame(map, cell);
                        map.areaManager.Home[cell] = true;
                    }
                } else if (action == "controls") {
                    var worker = pawns[0];
                    var middle = centre + new IntVec3(12, 0, 0);
                    var outlet = middle + IntVec3.North;
                    foreach (var d in GenAdj.AdjacentCells) {
                        var c = middle + d;
                        ClearWallCell(map, c);
                        if (c == outlet) continue;
                        var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                        wall.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(wall, c, map);
                    }
                    ClearWallCell(map, middle);
                    var inner = FundedFrame(map, middle);
                    var closing = FundedFrame(map, outlet);
                    worker.Position = outlet + IntVec3.North;
                    worker.Notify_Teleported(true, true);
                    closing.workDone = closing.WorkToBuild;
                    // The guard is armed only under a running supervisor; this paused probe arms it itself.
                    var previous = WallLayerGuard.Active;
                    bool refused, skipped;
                    Frame occupied;
                    try {
                        WallLayerGuard.Active = () => true;
                        closing.CompleteConstruction(worker);
                        refused = closing.Spawned && inner.Spawned;
                        // A completed frame is destroyed: CanConstruct would read its null map.
                        skipped = closing.Spawned && !GenConstruct.CanConstruct(closing, worker, true, false, null);
                        // Remove the whole negative control before the independent
                        // pawn-on-frame observation: it must not supply the outcome.
                        foreach (var c in GenAdj.AdjacentCells.Select(d => middle + d).Concat(new[] { middle }))
                            ClearWallCell(map, c);
                        occupied = FundedFrame(map, middle);
                        worker.Position = middle;
                        worker.Notify_Teleported(true, true);
                        occupied.CompleteConstruction(worker);
                    } finally { WallLayerGuard.Active = previous; }
                    return new { success = true, refused, skipped, completed = !occupied.Spawned
                        && middle.GetEdifice(map)?.def == ThingDefOf.Wall,
                        relocated = worker.Position != middle, pawnStandable = worker.Position.Standable(map),
                        escaped = map.reachability.CanReachMapEdge(worker.Position, TraverseParms.For(worker)),
                        before = new { x = middle.x, z = middle.z }, after = new { x = worker.Position.x, z = worker.Position.z } };
                } else if (action != "audit") return Refuse("Unknown wall_layers action.");
                var frames = ring.SelectMany(c => c.GetThingList(map)).OfType<Frame>().ToList();
                return new { success = true, expected = ring.Count, frames = frames.Count,
                    funded = frames.Count(f => f.TotalMaterialCost().All(cost => f.resourceContainer.TotalStackCountOfDef(cost.thingDef) >= cost.count)),
                    standable = frames.Count(f => f.Position.Standable(map)),
                    standing = ring.Count(c => c.GetEdifice(map)?.def == ThingDefOf.Wall),
                    trapped = map.mapPawns.FreeColonistsSpawned.Count(p => !map.reachability.CanReachMapEdge(p.Position, TraverseParms.For(p))) };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static int replaceViolations, replaceTicks;
        private static string replaceFirstViolation = "";

        [Tool("test/wall_replacement", Description = "UNSAFE FOR MODEL EXECUTION. Paused 100x100 lab (#2529). prepare stages a roofed 5x5 room inside a 7x7 steel wall ring at the map centre and a lone roofed steel pillar 20 cells east, builders outside the ring, and clears the violation count. run ticks the game n single ticks (each audited: the target cell impassable, the room still enclosed with 25 cells and no reach to the map edge); a replacement Frame is funded the tick it appears so only construction work remains. audit reads the cell without ticking.")]
        public async Task<object> WallReplacement(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "audit", int x = 0, int z = 0, int ticks = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || (action != "audit" && !Find.TickManager.Paused) || map.Size.x != 100 || map.Size.z != 100)
                    return Refuse("A paused 100x100 disposable lab is required.");
                var centre = map.Center;
                var target = new IntVec3(x, 0, z);
                if (action == "prepare") {
                    var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed
                        && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && p.workSettings != null).ToList();
                    if (pawns.Count == 0) return Refuse("No capable lab builder.");
                    foreach (var pawn in pawns) {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        pawn.Position = centre + new IntVec3(0, 0, -6);
                        pawn.Notify_Teleported(true, true);
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                        for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                    }
                    for (var dx = -3; dx <= 3; dx++)
                        for (var dz = -3; dz <= 3; dz++) {
                            var cell = centre + new IntVec3(dx, 0, dz);
                            ClearWallCell(map, cell);
                            if (Math.Abs(dx) == 3 || Math.Abs(dz) == 3) SpawnPlayerWall(map, cell);
                            else map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                            map.areaManager.Home[cell] = true;
                        }
                    var pillar = centre + new IntVec3(20, 0, 0);
                    foreach (var cell in GenAdj.CellsAdjacent8Way(new TargetInfo(pillar, map)).Concat(new[] { pillar })) {
                        ClearWallCell(map, cell);
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    }
                    SpawnPlayerWall(map, pillar);
                    replaceViolations = replaceTicks = 0; replaceFirstViolation = "";
                } else if (action == "run") {
                    var interior = centre;
                    for (var i = 0; i < ticks; i++) {
                        Find.TickManager.DoSingleTick();
                        replaceTicks++;
                        foreach (var frame in target.GetThingList(map).OfType<Frame>().ToList())
                            if (frame.resourceContainer.TotalStackCount < 1)
                                foreach (var cost in frame.TotalMaterialCost()) {
                                    var stack = ThingMaker.MakeThing(cost.thingDef);
                                    stack.stackCount = cost.count;
                                    frame.resourceContainer.TryAdd(stack, true);
                                }
                        var room = interior.GetRoom(map);
                        string? problem = target.Impassable(map) ? null : "target cell passable";
                        if (problem == null && (room == null || !room.ProperRoom || room.TouchesMapEdge || room.CellCount != 25)) problem = "room not enclosed";
                        if (problem == null && map.reachability.CanReachMapEdge(interior, TraverseParms.For(TraverseMode.PassDoors))) problem = "interior reaches the map edge";
                        if (problem != null) { replaceViolations++; if (replaceFirstViolation.Length == 0) replaceFirstViolation = "tick " + Find.TickManager.TicksGame + ": " + problem; }
                        if (target.GetEdifice(map) is Building done && !(done is Frame) && done.Stuff == ThingDefOf.WoodLog && done.def == ThingDefOf.Wall) break;
                    }
                } else if (action != "audit") return Refuse("Unknown wall_replacement action.");
                var edifice = target.GetEdifice(map);
                var built = edifice != null && !(edifice is Frame);
                var frameThing = target.GetThingList(map).OfType<Frame>().FirstOrDefault();
                return new { success = true, centre = new { x = centre.x, z = centre.z }, ticks = replaceTicks, violations = replaceViolations, firstViolation = replaceFirstViolation,
                    impassable = target.Impassable(map), builtDef = built && edifice != null ? edifice.def.defName : "", builtStuff = built && edifice != null ? edifice.Stuff?.defName ?? "" : "",
                    blueprint = target.GetThingList(map).OfType<Blueprint_Build>().Any(),
                    frameDef = frameThing?.def.defName ?? "", frameStandable = frameThing != null && frameThing.def.passability == Traversability.Standable };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static void SpawnPlayerWall(Map map, IntVec3 cell)
        {
            var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
            wall.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(wall, cell, map);
        }

        // The site the last remote_pickup stage framed; audit reads it.
        private static IntVec3? pickupSite;

        [Tool("test/remote_pickup",Description = "UNSAFE FOR MODEL EXECUTION. Paused 100x100 lab (#2517, #3046). remote stages a cold 5x5 wooden hut (3x3 roofed interior, door mid south, a lit campfire, no bed) whose mid east wall is an unfunded wood wall frame at the map centre, and three WoodLog stacks 40 cells east of the frame, each in its own one-cell stockpile (15, then 20 at 8 cells and 20 at 14 cells from the first), with a stockpile on the 3x7 cells east of the frame for the surplus, so vanilla hauling moves no stack; local stages the same hut 30 cells west, with two loose stacks 4 cells east of its frame, and removes the stockpiles; audit reads the last staged frame's delivered wood, the loose stacks and the carriers. A brain order or vanilla delivery may deliver; each stage destroys all other wood.")]
        public async Task<object> RemotePickup(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "audit")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || (action != "audit" && !Find.TickManager.Paused) || map.Size.x != 100 || map.Size.z != 100)
                    return Refuse("A paused 100x100 disposable lab is required.");
                var centre = map.Center;
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && p.workSettings != null).ToList();
                if (pawns.Count == 0) return Refuse("No capable lab builder.");
                if (action == "remote" || action == "local") pickupSite = action == "local" ? centre + new IntVec3(-30, 0, 0) : centre;
                else if (action != "audit") return Refuse("Unknown remote_pickup action.");
                if (pickupSite is not { } site) return Refuse("Stage remote or local first.");
                // The hut's south-west corner: the frame is the middle of its east wall.
                var origin = site - new IntVec3(4, 0, 2);
                if (action != "audit") {
                    // Every other log goes, the last phase's included: the brain unforbids stock it may use.
                    foreach (var wood in map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).ToList()) wood.Destroy(DestroyMode.Vanish);
                    foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Stockpile>().ToList()) zone.Delete();
                    FixtureHut.Build(map, 5, origin, origin + new IntVec3(2, 0, 0), 0);
                    site.GetEdifice(map)?.Destroy(DestroyMode.Vanish);
                    var frame = (Frame)ThingMaker.MakeThing(ThingDefOf.Wall.frameDef, ThingDefOf.WoodLog);
                    frame.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(frame, site, map);
                    var fire = FixtureKit.Spawn(map, ThingDefOf.Campfire, null, origin + new IntVec3(3, 0, 3), Faction.OfPlayer, Rot4.North);
                    var fuel = fire.TryGetComp<CompRefuelable>(); fuel.Refuel(fuel.Props.fuelCapacity);
                    foreach (var cell in CellRect.CenteredOn(site, 6).ClipInsideMap(map).Cells) map.areaManager.Home[cell] = true;
                    foreach (var pawn in pawns) {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        pawn.Position = site + new IntVec3(2, 0, 0);
                        pawn.Notify_Teleported(true, true);
                        pawn.needs.food.CurLevelPercentage = 1;
                        pawn.needs.rest.CurLevelPercentage = 1;
                        if (pawn.needs.joy != null) pawn.needs.joy.CurLevelPercentage = 1;
                        for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                    }
                    var stacks = action == "remote"
                        ? new[] { (site + new IntVec3(40, 0, 0), 15), (site + new IntVec3(40, 0, 8), 20), (site + new IntVec3(40, 0, -14), 20) }
                        : new[] { (site + new IntVec3(4, 0, 0), 30), (site + new IntVec3(4, 0, 4), 20) };
                    foreach (var (cell, count) in stacks) {
                        ClearWallCell(map, cell);
                        var stack = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        stack.stackCount = count;
                        GenSpawn.Spawn(stack, cell, map);
                        map.areaManager.Home[cell] = true;
                    }
                    if (action == "remote") {
                        // One stockpile per stack and one beside the frame, all of one priority: vanilla hauls no stack
                        // between them, and the surplus lands in (or is hauled to) the frame's own.
                        void Stockpile(IEnumerable<IntVec3> cells) {
                            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                            map.zoneManager.RegisterZone(zone);
                            foreach (var cell in cells) zone.AddCell(cell);
                        }
                        foreach (var (cell, _) in stacks) Stockpile(new[] { cell });
                        var beside = CellRect.FromLimits(site.x + 1, site.z - 3, site.x + 3, site.z + 3).Cells.ToList();
                        foreach (var cell in beside) ClearWallCell(map, cell);
                        Stockpile(beside);
                    }
                }
                var built = site.GetThingList(map).OfType<Frame>().FirstOrDefault();
                var cost = ThingDefOf.Wall.CostStuffCount;
                var delivered = built != null ? built.resourceContainer.TotalStackCountOfDef(ThingDefOf.WoodLog)
                    : site.GetEdifice(map)?.def == ThingDefOf.Wall ? cost : 0;
                var loose = map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).Where(t => t.Spawned)
                    .Select(t => new { x = t.Position.x, z = t.Position.z, count = t.stackCount }).ToList();
                return new { success = true, delivered, cost, loose,
                    carrying = map.mapPawns.FreeColonistsSpawned.Count(p => p.carryTracker?.CarriedThing?.def == ThingDefOf.WoodLog),
                    capacity = pawns[0].carryTracker.MaxStackSpaceEver(ThingDefOf.WoodLog),
                    site = new { x = site.x, z = site.z }, interior = new { x = origin.x + 1, z = origin.z + 1 },
                    door = new { x = origin.x + 2, z = origin.z } };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/construction_skill",Description = "UNSAFE FOR MODEL EXECUTION. Paused 100x100 lab: prepare stages a material-filled wall and quality bed blueprint with a low-skill helper and drafted skilled builder; convert tests exact blueprint-to-frame setting transfer; audit/controls observe vanilla completion and finishing eligibility. No production caller.")]
        public async Task<object> ConstructionSkill(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "audit")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || (action != "audit" && !Find.TickManager.Paused) || map.Size.x != 100 || map.Size.z != 100) return Refuse("Paused lab required for fixture writes.");
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.skills != null
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).OrderBy(p => p.thingIDNumber).ToList();
                if (pawns.Count < 2) return Refuse("Two capable fixture pawns required.");
                var low = pawns[0]; var high = pawns[1];
                var wallCell = map.Center + new IntVec3(5,0,0);
                var bedCell = map.Center + new IntVec3(9,0,0);
                if (action == "prepare") {
                    foreach (var pawn in pawns) {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        pawn.drafter.Drafted = pawn != low;
                        pawn.Position = map.Center; pawn.Notify_Teleported(true,true);
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction,1);
                        for (var hour=0;hour<24;hour++) pawn.timetable.SetAssignment(hour,TimeAssignmentDefOf.Work);
                    }
                    low.skills.GetSkill(SkillDefOf.Construction).Level=2;
                    high.skills.GetSkill(SkillDefOf.Construction).Level=9;
                    ClearWallCell(map,wallCell); ClearWallCell(map,bedCell); ClearWallCell(map,bedCell+IntVec3.North);
                    FundedFrame(map,wallCell); map.areaManager.Home[wallCell]=true;
                    var bp=GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Bed,bedCell,map,Rot4.North,Faction.OfPlayer,ThingDefOf.WoodLog);
                    return new { success=true, target=bp.GetUniqueLoadID(), x=bedCell.x,z=bedCell.z };
                }
                var site=bedCell.GetThingList(map).FirstOrDefault(t => t is Blueprint_Build || t is Frame);
                if (action=="convert") {
                    if (!(site is Blueprint_Build bp)) return Refuse("Expected bed blueprint.");
                    var before=ConstructionSkillGuard.Setting(bp);
                    if (before==null || before.Minimum!=9) return Refuse("Minimum must be attached through Actions/Apply first.");
                    if (!bp.TryReplaceWithSolidThing(low,out var converted,out _ ) || !(converted is Frame frame)) return Refuse("Vanilla conversion failed.");
                    foreach (var cost in frame.TotalMaterialCost()) { var stack=ThingMaker.MakeThing(cost.thingDef); stack.stackCount=cost.count; frame.resourceContainer.TryAdd(stack,true); }
                    site=frame;
                }
                if (!(site is Frame bed)) return Refuse("Expected bed frame.");
                var setting=ConstructionSkillGuard.Setting(bed);
                var giver=new WorkGiver_ConstructFinishFrames { def=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.giverClass==typeof(WorkGiver_ConstructFinishFrames)) };
                var lowRefused=!giver.HasJobOnThing(low,bed,true);
                if (action=="controls") {
                    bed.workDone=bed.WorkToBuild;
                    bed.CompleteConstruction(low);
                    var lowCompletionRefused=bed.Spawned;
                    bed.CompleteConstruction(high);
                    var completed=!bed.Spawned && bedCell.GetEdifice(map)?.def==ThingDefOf.Bed;
                    var built=bedCell.GetEdifice(map);
                    if (built!=null) built.Destroy();
                    var replacement=GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Bed,bedCell,map,Rot4.North,Faction.OfPlayer,ThingDefOf.WoodLog);
                    return new {success=true, lowRefused, lowCompletionRefused, completed, noLeak=ConstructionSkillGuard.Setting(replacement)==null};
                }
                if (action!="convert" && action!="audit") return Refuse("Unknown action.");
                return new {success=true,minimum=setting?.Minimum,tier=setting?.Tier,target=bed.GetUniqueLoadID(),lowRefused,highAllowed=ConstructionSkillGuard.Allows(bed,high),
                    wallProgress=wallCell.GetEdifice(map)?.def==ThingDefOf.Wall || wallCell.GetThingList(map).OfType<Frame>().Any(f=>f.workDone>0), bedPending=bed.Spawned};
            },cancellationToken).ConfigureAwait(false);
        }

        // Sites A..D sit in one column six cells apart so no site is a nearby needer of another (#2523).
        private static IntVec3 TierCell(Map map, int index) => map.Center + new IntVec3(6, 0, -12 + 6 * index);

        [Tool("test/construction_tier_gate", Description = "UNSAFE FOR MODEL EXECUTION. Paused 100x100 lab: prepare stages five steel (limited) and fifty wood plus four wall blueprints (A steel tier 0, B steel tier 5, C wood tier 5, D steel untiered); query asks the patched delivery work-giver whether one colonist is handed a delivery job for each; enclose walls in A so it is unreachable. Re-tiering goes through Actions/Apply. No production caller.")]
        public async Task<object> ConstructionTierGateFixture(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "query")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || (action != "query" && !Find.TickManager.Paused) || map.Size.x != 100 || map.Size.z != 100) return Refuse("Paused lab required for fixture writes.");
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.skills != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn == null) return Refuse("A capable colonist is required.");
                if (action == "prepare") {
                    for (var i = 0; i < 4; i++)
                        foreach (var cell in GenAdj.CellsAdjacent8Way(new TargetInfo(TierCell(map, i), map)).Concat(new[] { TierCell(map, i) })) ClearWallCell(map, cell);
                    var stock = map.Center + new IntVec3(-3, 0, 0);
                    foreach (var offset in new[] { 0, 2 }) ClearWallCell(map, stock + new IntVec3(0, 0, offset));
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    pawn.Position = map.Center; pawn.Notify_Teleported(true, true);
                    var steel = ThingMaker.MakeThing(ThingDefOf.Steel); steel.stackCount = ThingDefOf.Wall.CostStuffCount;
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog); wood.stackCount = 50;
                    GenSpawn.Spawn(steel, stock, map); GenSpawn.Spawn(wood, stock + new IntVec3(0, 0, 2), map);
                    var sites = new List<object>();
                    var tiers = new[] { 0, 5, 5, ConstructionSkillSetting.None };
                    for (var i = 0; i < 4; i++) {
                        var cell = TierCell(map, i);
                        if (!GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, cell, Rot4.North, map, false, null, null, i == 2 ? ThingDefOf.WoodLog : ThingDefOf.Steel).Accepted)
                            return Refuse("Wall site " + i + " is not placeable.");
                        var bp = GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, cell, map, Rot4.North, Faction.OfPlayer, i == 2 ? ThingDefOf.WoodLog : ThingDefOf.Steel);
                        if (tiers[i] != ConstructionSkillSetting.None) ConstructionSkillGuard.SetTier(bp, tiers[i]);
                        sites.Add(new { x = cell.x, z = cell.z, target = bp.GetUniqueLoadID() });
                    }
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    return new { success = true, sites };
                }
                if (action == "enclose") {
                    foreach (var cell in GenAdj.CellsAdjacent8Way(new TargetInfo(TierCell(map, 0), map))) {
                        var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                        wall.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(wall, cell, map);
                    }
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                } else if (action != "query") return Refuse("Unknown action.");
                var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d => d.giverClass == typeof(WorkGiver_ConstructDeliverResourcesToBlueprints)).Worker as WorkGiver_Scanner;
                var rows = new List<object>();
                for (var i = 0; i < 4; i++) {
                    var site = TierCell(map, i).GetThingList(map).FirstOrDefault(t => t is Blueprint_Build);
                    if (site == null) return Refuse("Site " + i + " is gone.");
                    var job = (giver ?? throw new InvalidOperationException("Construction work giver unavailable.")).JobOnThing(pawn, site, true);
                    rows.Add(new { job = job?.def.defName, tier = ConstructionSkillGuard.Setting(site)?.Tier, reachable = pawn.CanReach(site, PathEndMode.Touch, Danger.Deadly) });
                }
                return new { success = true, supervisorActive = Supervisor.IsActive, a = rows[0], b = rows[1], c = rows[2], d = rows[3] };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static void ClearWallCell(Map map, IntVec3 cell)
        {
            foreach (var thing in cell.GetThingList(map).Where(t => !(t is Pawn)).ToList()) thing.Destroy();
        }
        private static Frame FundedFrame(Map map, IntVec3 cell)
        {
            var frame = (Frame)ThingMaker.MakeThing(ThingDefOf.Wall.frameDef, ThingDefOf.WoodLog);
            frame.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(frame, cell, map);
            foreach (var cost in frame.TotalMaterialCost()) {
                var stack = ThingMaker.MakeThing(cost.thingDef);
                stack.stackCount = cost.count;
                frame.resourceContainer.TryAdd(stack, true);
            }
            return frame;
        }
        private static object Refuse(string reason)=>new { success=false,reason };
    }
}
