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

        [Tool("test/construction_skill", Description = "UNSAFE FOR MODEL EXECUTION. Paused 100x100 lab: prepare stages a material-filled wall and quality bed blueprint with a low-skill helper and drafted skilled builder; convert tests exact blueprint-to-frame setting transfer; audit/controls observe vanilla completion and finishing eligibility. No production caller.")]
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
                return new {success=true,minimum=setting?.Minimum,lowRefused,highAllowed=ConstructionSkillGuard.Allows(bed,high),
                    wallProgress=wallCell.GetEdifice(map)?.def==ThingDefOf.Wall || wallCell.GetThingList(map).OfType<Frame>().Any(f=>f.workDone>0), bedPending=bed.Spawned};
            },cancellationToken).ConfigureAwait(false);
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
