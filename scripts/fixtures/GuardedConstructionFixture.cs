using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. This fixture never creates resources or edits pawn skills.
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

        [Tool("test/guarded_construction_control", Description = "UNSAFE FOR MODEL EXECUTION. Private prepared-fixture controls: cancel one exact WoodLog Wall blueprint on a returned site using Designator_Cancel; toggle the prepared pawn's actual drafted state; or issue a native ordered Goto. No suppression of game methods or authority hooks.")]
        public async Task<object> Control(IRimBridgeContext ctx,CancellationToken cancellationToken,string operation,
            string colonyId,string loadToken,int mapId,string pawnId=null,string blueprintId=null,int x=0,int z=0)
        {
            return await ctx.MainThread.InvokeAsync<object>(()=>{
                var map=Find.CurrentMap; var identity=Current.Game?.GetComponent<ColonyIdentity>();
                if(Current.Game!=preparedGame||map!=preparedMap||map==null||identity==null||identity.ColonyId!=colonyId
                    ||identity.LoadToken!=loadToken||map.uniqueID!=mapId||!Find.TickManager.Paused)return Refuse("Prepared paused colony/load/map identity changed.");
                if(operation=="cancel") {
                    var blueprint=map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).OfType<Blueprint>()
                        .SingleOrDefault(b=>b.GetUniqueLoadID()==blueprintId);
                    if(blueprint==null||blueprint is Blueprint_Install||blueprint.def.entityDefToBuild!=ThingDefOf.Wall
                        ||blueprint.Stuff!=ThingDefOf.WoodLog||!PreparedSites.Contains(blueprint.Position))return Refuse("Exact prepared WoodLog Wall blueprint unavailable.");
                    var cancel=new Designator_Cancel(); if(!cancel.CanDesignateThing(blueprint).Accepted)return Refuse("Native cancellation refused.");
                    cancel.DesignateThing(blueprint);
                    return new { success=blueprint.Destroyed,operation,blueprintId,tick=Find.TickManager.TicksGame };
                }
                var pawn=preparedPawn;
                if(pawn==null||!pawn.Spawned||pawn.Map!=map||pawn.GetUniqueLoadID()!=pawnId||pawn.Dead||pawn.Downed||pawn.InMentalState)
                    return Refuse("Exact prepared pawn unavailable.");
                if(operation=="draft") {
                    if(pawn.drafter==null)return Refuse("Pawn cannot be drafted.");
                    var before=pawn.drafter.Drafted;pawn.drafter.Drafted=!before;
                    return new { success=pawn.drafter.Drafted!=before,operation,pawnId,before,drafted=pawn.drafter.Drafted,tick=Find.TickManager.TicksGame };
                }
                if(operation=="move") {
                    var cell=new IntVec3(x,0,z);
                    if(!cell.InBounds(map)||cell.Fogged(map)||!cell.Standable(map)||pawn.Position.DistanceToSquared(cell)>144
                        ||!pawn.CanReach(cell,PathEndMode.OnCell,Danger.None))return Refuse("Nearby ordinary move destination unavailable.");
                    var job=JobMaker.MakeJob(JobDefOf.Goto,cell);var accepted=pawn.jobs.TryTakeOrderedJob(job,JobTag.Misc);
                    return new { success=accepted,operation,pawnId,x,z,jobId=job.loadID,currentJobId=pawn.CurJob?.loadID,tick=Find.TickManager.TicksGame };
                }
                return Refuse("Use cancel, draft or move.");
            },cancellationToken).ConfigureAwait(false);
        }
        private static object Refuse(string reason)=>new { success=false,reason };
    }
}
