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
    // Private disposable acceptance only. Prepare uses existing stock; WallLayers stages funded frames.
    public sealed class GuardedConstructionFixture
    {
        private static Game preparedGame;
        private static Map preparedMap;
        private static Pawn preparedPawn;
        private static readonly HashSet<IntVec3> PreparedSites = new HashSet<IntVec3>();

        [Tool("test/guarded_construction_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: un-forbid existing starting wood, set one capable colonist's normal Construct/Haul priorities and Work timetable, return bounded legal WoodLog Wall sites. No spawned resources, skill changes, game tick changes or placement.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int siteCount = 3)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (siteCount < 1 || siteCount > 8) return Refuse("Use 1..8 sites.");
                var wood = map.listerThings.ThingsOfDef(ThingDefOf.WoodLog)
                    .Where(t => t.Spawned && t.stackCount > 0 && !t.Position.Fogged(map) && (t.Faction == null || t.Faction == player))
                    .OrderBy(t => t.thingIDNumber).ToList();
                if (wood.Count == 0) return Refuse("No existing starting WoodLog stacks; fixture will not spawn resources.");
                var pawn = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && p.Faction == player && !p.Dead && !p.Downed
                    && !p.Drafted && !p.InMentalState && p.workSettings != null && p.timetable != null && p.skills != null
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                    && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
                    && p.skills.GetSkill(SkillDefOf.Construction).Level >= ThingDefOf.Wall.constructionSkillPrerequisite)
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault(p => wood.Any(t => p.CanReach(t, PathEndMode.Touch, Danger.None)));
                if (pawn == null) return Refuse("No existing colonist capable of normal construction/hauling with reachable wood.");
                var costs = ThingDefOf.Wall.CostListAdjusted(ThingDefOf.WoodLog, false);
                var woodCost = costs.Where(c => c.thingDef == ThingDefOf.WoodLog).Sum(c => c.count);
                var reachable = wood.Where(t => pawn.CanReach(t, PathEndMode.Touch, Danger.None)
                    && (pawn.playerSettings?.AreaRestrictionInPawnCurrentMap == null || pawn.playerSettings.AreaRestrictionInPawnCurrentMap[t.Position])).ToList();
                if (woodCost <= 0 || reachable.Sum(t => (long)t.stackCount) < (long)woodCost * siteCount)
                    return Refuse("Existing reachable wood cannot cover requested ordinary walls.");
                var sites = new List<IntVec3>();
                foreach (var cell in GenRadial.RadialCellsAround(pawn.Position, 12, true).Take(512)) {
                    if (!cell.InBounds(map) || cell.Fogged(map) || !cell.Standable(map) || cell.GetRoof(map) != null
                        || map.zoneManager.ZoneAt(cell) != null || sites.Any(c => c.DistanceToSquared(cell) < 9)
                        || (pawn.playerSettings?.AreaRestrictionInPawnCurrentMap != null && !pawn.playerSettings.AreaRestrictionInPawnCurrentMap[cell])
                        || cell.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame || t is Pawn || t is Plant || t.def.category == ThingCategory.Item)
                        || !pawn.CanReach(cell, PathEndMode.Touch, Danger.None)
                        || !GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, cell, Rot4.North, map, false, null, null, ThingDefOf.WoodLog).Accepted) continue;
                    sites.Add(cell); if (sites.Count == siteCount) break;
                }
                if (sites.Count != siteCount) return Refuse("Bounded nearby search found too few reachable legal wall cells.");
                var before = reachable.Select(t => new { id = t.GetUniqueLoadID(), count = t.stackCount, forbidden = t.IsForbidden(player) }).ToArray();
                foreach (var stack in reachable) stack.SetForbidden(false, false);
                var constructBefore = pawn.workSettings.GetPriority(WorkTypeDefOf.Construction);
                var haulBefore = pawn.workSettings.GetPriority(WorkTypeDefOf.Hauling);
                var scheduleBefore = Enumerable.Range(0,24).Select(hour => pawn.timetable.GetAssignment(hour).defName).ToArray();
                pawn.workSettings.SetPriority(WorkTypeDefOf.Construction,1);
                pawn.workSettings.SetPriority(WorkTypeDefOf.Hauling,1);
                for (var hour=0;hour<24;hour++) pawn.timetable.SetAssignment(hour,TimeAssignmentDefOf.Work);
                preparedGame=Current.Game; preparedMap=map; preparedPawn=pawn; PreparedSites.Clear(); foreach(var cell in sites)PreparedSites.Add(cell);
                var identity=Current.Game.GetComponent<ColonyIdentity>();
                return new { success=true, colonyId=identity?.ColonyId, loadToken=identity?.LoadToken, mapId=map.uniqueID,
                    tick=Find.TickManager.TicksGame, pawnId=pawn.GetUniqueLoadID(), pawnCell=new { x=pawn.Position.x,z=pawn.Position.z }, constructionSkill=pawn.skills.GetSkill(SkillDefOf.Construction).Level,
                    constructBefore, haulBefore, scheduleBefore,
                    constructPriority=pawn.workSettings.GetPriority(WorkTypeDefOf.Construction), haulPriority=pawn.workSettings.GetPriority(WorkTypeDefOf.Hauling),
                    timetable=Enumerable.Range(0,24).Select(hour=>pawn.timetable.GetAssignment(hour).defName).ToArray(),
                    woodBefore=before, wood=reachable.Select(t=>new { id=t.GetUniqueLoadID(), count=t.stackCount, forbidden=t.IsForbidden(player), x=t.Position.x,z=t.Position.z }).ToArray(),
                    wallCost=costs.Select(c=>new { defName=c.thingDef.defName,count=c.count }).ToArray(),
                    sites=sites.Select(c=>new { defName="Wall", stuff="WoodLog", rotation="ROTATION_NORTH",x=c.x,z=c.z }).ToArray() };
            },cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/wall_layers", Description = "UNSAFE FOR MODEL EXECUTION. Paused disposable lab only. prepare stages a funded, unfinished three-thick 9x9 wall ring with a three-cell open gate at the map centre and normal builder work. audit reads remaining frames, standing walls and colonist escape. controls directly attempts a completion that would strand a neighbouring frame, then completes an isolated frame with a pawn standing on it and reports vanilla relocation and escape.")]
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
                    closing.CompleteConstruction(worker);
                    var refused = closing.Spawned && inner.Spawned;
                    var skipped = !GenConstruct.CanConstruct(closing, worker, true, false, null);
                    // Remove the whole negative control before the independent
                    // pawn-on-frame observation: it must not supply the outcome.
                    foreach (var c in GenAdj.AdjacentCells.Select(d => middle + d).Concat(new[] { middle }))
                        ClearWallCell(map, c);
                    var occupied = FundedFrame(map, middle);
                    worker.Position = middle;
                    worker.Notify_Teleported(true, true);
                    occupied.CompleteConstruction(worker);
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

        [Tool("test/remote_pickup", Description = "UNSAFE FOR MODEL EXECUTION. Paused 100x100 lab (#2517). remote stages an unfunded wall frame at the map centre and three WoodLog stacks 40 cells east inside a stockpile strip (15, then 20 at 8 cells and 20 at 14 cells from the first); local stages a frame 30 cells west with two loose stacks near it and removes the stockpile; audit reads the delivered wood, loose stacks and carriers. Construction priority 1 and Hauling off for every colonist; all other wood is forbidden.")]
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
                var site = action == "local" ? centre + new IntVec3(-30, 0, 0) : centre;
                if (action == "remote" || action == "local") {
                    foreach (var wood in map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).ToList()) wood.SetForbidden(true, false);
                    foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Stockpile>().ToList()) zone.Delete();
                    foreach (var pawn in pawns) {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        pawn.Position = site + new IntVec3(0, 0, -2);
                        pawn.Notify_Teleported(true, true);
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Hauling, 0);
                        for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                    }
                    ClearWallCell(map, site);
                    var frame = (Frame)ThingMaker.MakeThing(ThingDefOf.Wall.frameDef, ThingDefOf.WoodLog);
                    frame.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(frame, site, map);
                    map.areaManager.Home[site] = true;
                    var stacks = action == "remote"
                        ? new[] { (centre + new IntVec3(40, 0, 0), 15), (centre + new IntVec3(40, 0, 8), 20), (centre + new IntVec3(40, 0, -14), 20) }
                        : new[] { (site + new IntVec3(4, 0, 0), 30), (site + new IntVec3(4, 0, 4), 20) };
                    foreach (var (cell, count) in stacks) {
                        ClearWallCell(map, cell);
                        var stack = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        stack.stackCount = count;
                        GenSpawn.Spawn(stack, cell, map);
                        map.areaManager.Home[cell] = true;
                    }
                    if (action == "remote") {
                        var strip = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                        map.zoneManager.RegisterZone(strip);
                        for (var z = -14; z <= 8; z++) strip.AddCell(centre + new IntVec3(40, 0, z));
                    }
                } else if (action != "audit") return Refuse("Unknown remote_pickup action.");
                var built = site.GetThingList(map).OfType<Frame>().FirstOrDefault();
                var cost = ThingDefOf.Wall.CostStuffCount;
                var delivered = built != null ? built.resourceContainer.TotalStackCountOfDef(ThingDefOf.WoodLog)
                    : site.GetEdifice(map)?.def == ThingDefOf.Wall ? cost : 0;
                var loose = map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).Where(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer))
                    .Select(t => new { x = t.Position.x, z = t.Position.z, count = t.stackCount }).ToList();
                return new { success = true, delivered, cost, loose,
                    carrying = map.mapPawns.FreeColonistsSpawned.Count(p => p.carryTracker?.CarriedThing?.def == ThingDefOf.WoodLog),
                    capacity = pawns[0].carryTracker.MaxStackSpaceEver(ThingDefOf.WoodLog),
                    site = new { x = site.x, z = site.z }, centre = new { x = centre.x, z = centre.z } };
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
                    var job = giver.JobOnThing(pawn, site, true);
                    rows.Add(new { job = job?.def.defName, tier = ConstructionSkillGuard.Setting(site)?.Tier, reachable = pawn.CanReach(site, PathEndMode.Touch, Danger.Deadly) });
                }
                return new { success = true, supervisorActive = Supervisor.IsActive, a = rows[0], b = rows[1], c = rows[2], d = rows[3] };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/bench_cleaning",Description = "UNSAFE FOR MODEL EXECUTION. Paused 100x100 lab: stages a fuelled bench with a startable bill and eight old filth near it, then asks the patched WorkGiver_DoBill.JobOnThing for a normal, a Cleaning-priority-0 and a drafted pawn (#2515). Reports the job each would take; changes no pawn orders.")]
        public async Task<object> BenchCleaning(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused || map.Size.x != 100 || map.Size.z != 100) return Refuse("A paused 100x100 disposable lab is required.");
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.workSettings != null && p.drafter != null
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Cleaning)).OrderBy(p => p.thingIDNumber).Take(3).ToList();
                if (pawns.Count < 3) return Refuse("Three colonists able to clean are required.");
                var blood = DefDatabase<ThingDef>.GetNamedSilentFail("Filth_Blood");
                if (blood == null) return Refuse("Filth_Blood unavailable.");
                var origin = map.Center + new IntVec3(-14, 0, 0);
                foreach (var cell in new CellRect(origin.x - 6, origin.z - 6, 13, 13).Cells) {
                    ClearWallCell(map, cell);
                    map.areaManager.Home[cell] = true;
                    map.roofGrid.SetRoof(cell, null);
                }
                foreach (var pawn in pawns) {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    pawn.drafter.Drafted = false;
                    pawn.Position = origin + new IntVec3(0, 0, -3); pawn.Notify_Teleported(true, true);
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Cleaning, 1);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                Thing bench = null; Bill staged = null; WorkGiver_DoBill giver = null;
                foreach (var name in new[] { "CraftingSpot", "FueledStove", "TableButcher" }) {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                    if (def == null) continue;
                    var made = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    made.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(made, origin, map);
                    var fuel = made.TryGetComp<CompRefuelable>(); if (fuel != null) fuel.Refuel(fuel.Props.fuelCapacity);
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    var worker = DefDatabase<WorkGiverDef>.AllDefsListForReading.Where(w => typeof(WorkGiver_DoBill).IsAssignableFrom(w.giverClass)
                        && w.fixedBillGiverDefs != null && w.fixedBillGiverDefs.Contains(def)).Select(w => w.Worker as WorkGiver_DoBill).FirstOrDefault(w => w != null);
                    if (worker != null && made is IBillGiver giverThing) {
                        foreach (var recipe in def.AllRecipes) {
                            var bill = recipe.MakeNewBill();
                            giverThing.BillStack.AddBill(bill);
                            if (worker.JobOnThing(pawns[0], made, false)?.def == JobDefOf.DoBill) { staged = bill; break; }
                            giverThing.BillStack.Delete(bill);
                        }
                    }
                    if (staged != null) { bench = made; giver = worker; break; }
                    made.Destroy();
                }
                if (bench == null) return Refuse("No fixture bench has a startable bill with the lab's stock.");
                var interaction = bench.def.hasInteractionCell ? bench.InteractionCell : bench.Position;
                string Job(Pawn p) { var job = giver.JobOnThing(p, bench, false); return job == null ? "none" : job.def.defName; }
                int Targets(Pawn p) { var job = giver.JobOnThing(p, bench, false); return job?.def == JobDefOf.Clean ? job.GetTargetQueue(TargetIndex.A).Count : 0; }
                var cleanBefore = Job(pawns[0]);
                var spawned = 0;
                foreach (var cell in GenRadial.RadialCellsAround(interaction, 4, true).Where(c => c != bench.Position && c != interaction && c.Standable(map))) {
                    if (spawned == 8) break;
                    if (!FilthMaker.TryMakeFilth(cell, map, blood, 1, FilthSourceFlags.None)) continue;
                    var filth = cell.GetThingList(map).OfType<Filth>().FirstOrDefault(f => f.def == blood);
                    if (filth == null) continue;
                    AccessTools.Field(typeof(Filth), "growTick").SetValue(filth, Find.TickManager.TicksGame - 1000);
                    spawned++;
                }
                if (spawned < 8) return Refuse("Only " + spawned + " filth spawned.");
                pawns[1].workSettings.SetPriority(WorkTypeDefOf.Cleaning, 0);
                pawns[2].drafter.Drafted = true;
                var normal = Job(pawns[0]); var normalTargets = Targets(pawns[0]);
                var disabled = Job(pawns[1]); var disabledTargets = Targets(pawns[1]);
                var drafted = Job(pawns[2]); var draftedTargets = Targets(pawns[2]);
                pawns[2].drafter.Drafted = false;
                return new { success = true, supervisorActive = Supervisor.IsActive, filth = spawned, cleanBefore,
                    normal, normalTargets, disabled, disabledTargets, drafted, draftedTargets };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/care_cleaning", Description = "UNSAFE FOR MODEL EXECUTION. Paused 100x100 lab: stages a bed with an injured patient, a startable surgery bill and eight old filth beside it, then asks the patched WorkGiver_DoBill for the surgery job and the patched tend driver for its finalizer job, for a normal and a Cleaning-priority-0 doctor (#2521). Changes no standing pawn orders.")]
        public async Task<object> CareCleaning(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused || map.Size.x != 100 || map.Size.z != 100) return Refuse("A paused 100x100 disposable lab is required.");
                var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.workSettings != null && p.drafter != null
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Cleaning)).OrderBy(p => p.thingIDNumber).Take(3).ToList();
                if (pawns.Count < 3) return Refuse("Three colonists able to clean are required.");
                var blood = DefDatabase<ThingDef>.GetNamedSilentFail("Filth_Blood");
                if (blood == null) return Refuse("Filth_Blood unavailable.");
                var doctor = pawns[0]; var noClean = pawns[1]; var patient = pawns[2];
                var origin = map.Center + new IntVec3(14, 0, 0);
                foreach (var cell in new CellRect(origin.x - 6, origin.z - 6, 13, 13).Cells) {
                    ClearWallCell(map, cell);
                    map.areaManager.Home[cell] = true;
                    map.roofGrid.SetRoof(cell, null);
                }
                foreach (var pawn in pawns) {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    pawn.drafter.Drafted = false;
                    pawn.Position = origin + new IntVec3(0, 0, -4); pawn.Notify_Teleported(true, true);
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Cleaning, 1);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                bed.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(bed, origin, map, Rot4.North);
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                patient.Position = bed.Position; patient.Notify_Teleported(true, true);
                patient.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
                var torso = patient.health.hediffSet.GetNotMissingParts().FirstOrDefault(p => p.def.defName == "Torso");
                if (torso == null) return Refuse("Patient has no torso.");
                patient.TakeDamage(new DamageInfo(DamageDefOf.Cut, 3f, 999f, -1f, null, torso));
                if (!patient.health.HasHediffsNeedingTend()) return Refuse("Patient needs no tending.");
                var giver = DefDatabase<WorkGiverDef>.GetNamedSilentFail("DoBillsMedicalHumanOperation")?.Worker as WorkGiver_DoBill;
                if (giver == null) return Refuse("Surgery work giver unavailable.");
                string Surgery(Pawn p) { var job = giver.JobOnThing(p, patient, false); return job == null ? "none" : job.def.defName; }
                int SurgeryTargets(Pawn p) { var job = giver.JobOnThing(p, patient, false); return job?.def == JobDefOf.Clean ? job.GetTargetQueue(TargetIndex.A).Count : 0; }
                Bill staged = null;
                foreach (var recipe in patient.def.AllRecipes.Where(r => r.IsSurgery)) {
                    var bill = recipe.MakeNewBill();
                    patient.BillStack.AddBill(bill);
                    if (bill is Bill_Medical medical) {
                        var part = recipe.Worker.GetPartsToApplyOn(patient, recipe).FirstOrDefault();
                        if (part != null) medical.Part = part;
                    }
                    if (Surgery(doctor) == "DoBill") { staged = bill; break; }
                    patient.BillStack.Delete(bill);
                }
                if (staged == null) return Refuse("No surgery bill is startable on the staged patient.");
                var spawned = 0;
                foreach (var cell in GenRadial.RadialCellsAround(bed.Position, 4, true).Where(c => c != bed.Position && c.Standable(map))) {
                    if (spawned == 8) break;
                    if (!FilthMaker.TryMakeFilth(cell, map, blood, 1, FilthSourceFlags.None)) continue;
                    var filth = cell.GetThingList(map).OfType<Filth>().FirstOrDefault(f => f.def == blood);
                    if (filth == null) continue;
                    AccessTools.Field(typeof(Filth), "growTick").SetValue(filth, Find.TickManager.TicksGame - 1000);
                    spawned++;
                }
                if (spawned < 8) return Refuse("Only " + spawned + " filth spawned.");
                noClean.workSettings.SetPriority(WorkTypeDefOf.Cleaning, 0);
                var surgery = Surgery(doctor); var surgeryTargets = SurgeryTargets(doctor);
                var surgeryDisabled = Surgery(noClean); var surgeryDisabledTargets = SurgeryTargets(noClean);
                patient.BillStack.Delete(staged);
                string Tend(Pawn p, out int targets) {
                    targets = 0;
                    p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.TendPatient, patient), JobCondition.InterruptForced);
                    var job = p.jobs.curDriver?.GetFinalizerJob(JobCondition.Succeeded);
                    var other = p.jobs.curDriver?.GetFinalizerJob(JobCondition.Incompletable);
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                    if (other != null) return "finalizer-on-failure";
                    if (job == null) return "none";
                    targets = job.def == JobDefOf.Clean ? job.GetTargetQueue(TargetIndex.A).Count : 0;
                    return job.def.defName;
                }
                var tend = Tend(doctor, out var tendTargets);
                var tendDisabled = Tend(noClean, out var tendDisabledTargets);
                return new { success = true, supervisorActive = Supervisor.IsActive, filth = spawned, surgery, surgeryTargets,
                    surgeryDisabled, surgeryDisabledTargets, tend, tendTargets, tendDisabled, tendDisabledTargets };
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
