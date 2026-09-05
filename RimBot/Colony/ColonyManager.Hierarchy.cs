using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Models;
using RimBot.Tools;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public sealed partial class ColonyManager {
        private int hierarchyNextTick, hierarchyPeriodicTick;
        private JObject hierarchyPrevious;
        private JArray hierarchyCommitments=new JArray(), hierarchyRecent=new JArray();
        private string hierarchySave="";
        private void SaveHierarchy() {
            if(Scribe.mode==LoadSaveMode.Saving) hierarchySave=new JObject{["commitments"]=hierarchyCommitments,["recent"]=hierarchyRecent}.ToString(Formatting.None);
            Scribe_Values.Look(ref hierarchySave,"managementBlackboard","");
            if(Scribe.mode==LoadSaveMode.PostLoadInit) {
                try { var saved=string.IsNullOrEmpty(hierarchySave)?new JObject():JObject.Parse(hierarchySave); hierarchyCommitments=saved["commitments"] as JArray??new JArray(); hierarchyRecent=saved["recent"] as JArray??new JArray(); }
                catch(Exception ex) { hierarchyCommitments=new JArray(); hierarchyRecent=new JArray(); Log.Warning("[RimBot] Blackboard reset: "+ex.Message); }
                hierarchyNextTick=0; hierarchyPeriodicTick=0;
            }
        }
        private bool ManagementCurrent(Map map,int token)=>token==generation&&Verse.Current.Game==owner&&Find.Maps.Contains(map)&&Automatic&&!Find.TickManager.Paused&&endpointAtStart==RimBotMod.Settings.ConnectionSignature;
        private Task<T> OnManagementThread<T>(Map map,int token,Func<T> action) {
            var completion=new TaskCompletionSource<T>();
            callbacks.Enqueue(()=> { try { if(!ManagementCurrent(map,token)) throw new OperationCanceledException("Colony changed or management paused"); completion.SetResult(action()); } catch(Exception ex) { completion.SetException(ex); } });
            return completion.Task;
        }
        private ColonyBlackboard BuildBlackboard(Map map) {
            var facts=ColonyObserver.Observe(map); int tick=Find.TickManager.TicksGame;
            foreach(var expired in hierarchyCommitments.OfType<JObject>().Where(c=>c.Value<int?>("mapId")!=map.uniqueID || (c.Value<int?>("untilTick")??0)<=tick).ToList()) expired.Remove();
            var pawns=map.mapPawns.FreeColonistsSpawned;
            var board=new ColonyBlackboard{goals=Goal+"\n"+TacticalStrategy(),direction=LatestDirection};
            board.summary=StateTransfer.Select(snapshot,"mapId","base","colonistCount","sleepingCapacity","activeResearch","growingZones","blueprints","frames","buildings");
            board.summary["availablePawns"]=pawns.Count(p=>!p.Downed&&!p.Drafted);
            board.summary["foodDays"]=facts.FoodDays; board.summary["bleeding"]=facts.Bleeding; board.summary["downed"]=facts.Downed; board.summary["temperatureInjuries"]=facts.TemperatureInjuries;
            board.summary["outdoorTemperatureC"]=map.mapTemperature.OutdoorTemp;
            board.summary["temperatureBand"]=(int)Math.Floor(map.mapTemperature.OutdoorTemp/5);
            board.summary["visibleHostileCount"]=facts.Hostiles;
            board.summary["hostilityNote"]="Faction hostility alone is not an active attack or reason to block construction.";
            var power=new JArray(map.listerBuildings.allBuildingsColonist.Select(b=>new{Building=b,Power=b.TryGetComp<CompPowerTrader>()}).Where(p=>p.Power!=null).Take(20).Select(p=>new JObject{["id"]=p.Building.thingIDNumber,["defName"]=p.Building.def.defName,["powered"]=p.Power.PowerOn}));
            board.summary["unpoweredBuildings"]=power.Count(p=>p.Value<bool>("powered")==false);
            board.pawnAvailability=new JObject{["count"]=pawns.Count,["available"]=pawns.Count(p=>!p.Downed&&!p.Drafted),["horizonHours"]=24,["note"]="Pawn-hours are estimated planning capacity, not guaranteed work time. Drafted/downed pawns are excluded."};
            board.workTypes=new JArray(DefDatabase<WorkTypeDef>.AllDefsListForReading.Select(w=>w.defName));
            board.resources["pawn_hours"]=pawns.Count(p=>!p.Downed&&!p.Drafted)*8;
            foreach(var group in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t=>t.def.category==ThingCategory.Item&&!t.Position.Fogged(map)&&!t.IsForbidden(Faction.OfPlayer)).GroupBy(t=>t.def.defName)) board.resources[group.Key]=group.Sum(t=>t.stackCount);
            board.forbiddenSupplies=new JArray(map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t=>t.def.category==ThingCategory.Item&&!t.Position.Fogged(map)&&t.IsForbidden(Faction.OfPlayer)).Select(t=>new JObject{["id"]=t.thingIDNumber,["defName"]=t.def.defName,["count"]=t.stackCount,["x"]=t.Position.x,["z"]=t.Position.z}));
            board.incidents=new JArray(new JObject{["type"]="medical",["bleeding"]=facts.Bleeding,["downed"]=facts.Downed,["temperatureInjuries"]=facts.TemperatureInjuries},new JObject{["type"]="notifications",["data"]=snapshot["notifications"]?.DeepClone()});
            board.commitments=(JArray)hierarchyCommitments.DeepClone(); board.recentDecisions=new JArray(hierarchyRecent.OfType<JObject>().Select(r=>StateTransfer.Select(r,"summary","orders","problems","tick")));
            var colonists=new JArray(pawns.Take(30).Select(p=>new JObject{["id"]=p.thingIDNumber,["name"]=p.LabelShort,["mood"]=p.needs?.mood?.CurLevel,["food"]=p.needs?.food?.CurLevel,["rest"]=p.needs?.rest?.CurLevel,["downed"]=p.Downed,["drafted"]=p.Drafted,["idle"]=p.mindState.IsIdle,["job"]=p.CurJob?.def.defName,["canFight"]=!p.WorkTagIsDisabled(WorkTags.Violent)}));
            board.details["survival"]=StateTransfer.Select(snapshot,"growingZones","sleepingCapacity","looseItemsTop20"); board.details["survival"]["colonists"]=colonists.DeepClone();
            board.details["infrastructure"]=StateTransfer.Select(snapshot,"localMap","sleepingCapacity","workFocus","pendingWork","stockpiles","buildings"); board.details["infrastructure"]["power"]=power;
            board.details["security"]=StateTransfer.Select(snapshot,"colonists","visibleHostileFactionPawns");
            board.details["workforce"]=StateTransfer.Select(snapshot,"workFocus","pendingWork"); board.details["workforce"]["colonists"]=colonists.DeepClone();
            board.details["development"]=StateTransfer.Select(snapshot,"activeResearch","buildings","growingZones");
            // Execution outcomes belong to shared state; approved is not built/completed.
            foreach(ManagementRole role in Enum.GetValues(typeof(ManagementRole))) board.details[role.ToString().ToLowerInvariant()]["trackedWork"]=new JArray(taskLedger.View(map.uniqueID).Where(TaskLedger.IsActive).Where(t=>ManagementTools.Owns(role,t.Value<string>("tool"))).Take(16));
            return board;
        }
        private ManagementEvent ManagementEvents(ColonyBlackboard board,bool direction,bool notices) {
            int tick=Find.TickManager.TicksGame;
            ManagementEvent result=ManagementEvent.None;
            if(hierarchyPrevious==null||tick>=hierarchyPeriodicTick) { result|=ManagementEvent.Periodic; hierarchyPeriodicTick=tick+60000; }
            if(direction) result|=ManagementEvent.Direction;
            if(notices) result|=ManagementEvent.Notifications;
            Func<string,bool> changed=k=>hierarchyPrevious==null||!JToken.DeepEquals(board.summary[k],hierarchyPrevious[k]);
            if(changed("foodDays")&&(board.summary.Value<double>("foodDays")<2)) result|=ManagementEvent.Food;
            if(changed("bleeding")||changed("downed")) result|=ManagementEvent.Medical;
            if(changed("temperatureInjuries")||changed("temperatureBand")) result|=ManagementEvent.Temperature;
            if(changed("unpoweredBuildings")) result|=ManagementEvent.Power;
            if(changed("visibleHostileCount")) result|=ManagementEvent.Threat;
            if(changed("colonistCount")||changed("availablePawns")) result|=ManagementEvent.Pawns;
            if(executionPending||changed("blueprints")||changed("frames")||changed("buildings")||changed("sleepingCapacity")) result|=ManagementEvent.Work;
            hierarchyPrevious=(JObject)board.summary.DeepClone();
            return result;
        }
        private readonly Dictionary<ManagementRole,int> managerNextTicks=new Dictionary<ManagementRole,int>();
        private void StartHierarchy(Map map,ILanguageModel provider,string model,string key,int maxTokens,int token,bool direction,bool notices,bool workChanged) {
            var board=BuildBlackboard(map); board.replyRequested=direction; int tick=Find.TickManager.TicksGame;
            var events=ManagementEvents(board,direction,notices); if(workChanged) events|=ManagementEvent.Work;
            var selected=ManagerRegistry.Select(events);
            foreach(var due in managerNextTicks.Where(p=>tick>=p.Value).Select(p=>p.Key)) if(!selected.Contains(due)) selected.Add(due);
            if(selected.Count==0) { hierarchyNextTick=tick+2500; WatchCurrentState(); return; }
            foreach(var role in selected) managerNextTicks[role]=tick+2500;
            int notificationRevision=notificationFeed.Revision;
            string cycle=Guid.NewGuid().ToString("N"); var started=Clock.Elapsed.TotalSeconds;
            Action<JObject> telemetry=row=>callbacks.Enqueue(()=> {
                row["cycle"]=cycle; Log.Message("[RimBot Hierarchy] "+row.ToString(Formatting.None));
                if(token!=generation) return;
                if(row.Value<string>("event")=="model_start") ProgressPhase="Reviewing "+row.Value<string>("manager").ToLowerInvariant();
                if(row.Value<string>("event")=="model") { Tokens+=(row.Value<int?>("inputTokens")??0)+(row.Value<int?>("outputTokens")??0); }
                if(row.Value<string>("event")=="proposal") {
                    var p=row["proposal"]; string role=p.Value<string>("manager");
                    if(Enum.TryParse(role,true,out ManagementRole parsed)) managerNextTicks[parsed]=tick+(int)(p.Value<double>("review_after_hours")*2500);
                    if((p["requests"] as JArray)?.Count>0 || (p["labor_requests"] as JArray)?.Count>0) Record("Considering: "+ActivitySummary.Short(p.Value<string>("summary"),260));
                }
                if(row.Value<string>("event")=="validation_failure") { ReviewFailures++; Record(row.Value<string>("manager")+": "+ActivitySummary.Short(row.Value<string>("error"),200)); }
            });
            var coordinator=new ManagementCoordinator(provider,model,key,maxTokens,
                call=>OnManagementThread(map,token,()=> {
                    // Defense in depth: never dispatch a mutation through a specialist query.
                    if(!ManagementTools.IsRead(call.Name)||ColonyTools.IsAction(call.Name)) throw new ArgumentException("Specialists cannot execute actions");
                    string result=ColonyTools.Execute(map,call,p=>{}); TotalToolCalls++; StepProgress(call.Name,true);
                    Log.Message("[RimBot Debug] "+call.Name+" "+call.Arguments?.ToString(Formatting.None)+" => "+result); return result;
                }),
                ()=>OnManagementThread(map,token,()=> {
                    if(!Budget.TryRequest(Clock.Elapsed.TotalSeconds,hourlyLimit)) return false;
                    ReviewRequests++; Status="Reviewing colony work…"; return true;
                }),telemetry,direction,()=>OnManagementThread(map,token,()=>BuildBlackboard(map)));
            Task.Run(async()=> {
                try {
                    var decision=await coordinator.Run(board,selected);
                    await OnManagementThread(map,token,()=> {
                        if(notificationFeed.Revision!=notificationRevision) notificationPending=true;
                        // Validate again against current resource availability before any mutation.
                        var current=BuildBlackboard(map); current.acceptedLabor=board.acceptedLabor; current.actionContexts=board.actionContexts; current.replyRequested=direction; current.laborGrantsFixed=board.laborGrantsFixed;
                        if(new[]{"downed","bleeding","temperatureInjuries"}.Any(field=>current.summary.Value<int>(field)>board.summary.Value<int>(field))) {
                            notificationPending=true; Finish("New medical emergency; revising orders before execution."); return false;
                        }
                        var accepted=decision.Accepted;
                        var approvedJson=decision.Json();
                        var all=accepted.Concat(decision.proposals.Where(p=>p.status!="accept").Select(p=>new ManagerProposal{id=p.proposalId})).ToList();
                        decision=ManagementSchema.ParseDecision(approvedJson,all,current);
                        var outcomes=new JArray(); var failedParents=new HashSet<string>();
                        foreach(var proposal in decision.Accepted.OrderBy(p=>p.manager=="workforce"?1:0).ThenByDescending(p=>p.priority)) {
                            foreach(var action in proposal.requests) {
                                var call=action.Call(); string result; bool success=true;
                                try {
                                    if(action.requestId!=null&&failedParents.Contains(action.requestId)) throw new ArgumentException("Parent work order failed; allocation deferred");
                                    if(actionLimit>0&&actions>=actionLimit) throw new ArgumentException("Cloud action budget reached");
                                    if(!Enum.TryParse(proposal.manager,true,out ManagementRole role)||!ManagementTools.Owns(role,call.Name)) throw new ArgumentException("Action violates domain ownership");
                                    ManagementSchema.Validate(call.Arguments,JObject.Parse(ToolCatalog.Definitions().Single(t=>t.Name==call.Name).ParametersJson));
                                    result=ColonyTools.Execute(map,call,p=>Plan=p); actions++;
                                } catch(Exception ex) { success=false; result=ex.Message; failedParents.Add(proposal.id); }
                                TotalToolCalls++; StepProgress(call.Name,success);
                                if(call.Name=="architect_build"&&success&&(call.Arguments["toX"]!=null||call.Arguments["toZ"]!=null)) foreach(var placement in ConstructionLayout.Expand(call.Arguments)) taskLedger.Record(map.uniqueID,Find.TickManager.TicksGame,new ToolCall{Name=call.Name,Arguments=placement},result,true);
                                else taskLedger.Record(map.uniqueID,Find.TickManager.TicksGame,call,result,success);
                                Record(ActivitySummary.Tool(call,result,success));
                                outcomes.Add(new JObject{["proposalId"]=proposal.id,["requestId"]=action.requestId,["action"]=call.Name,["args"]=call.Arguments,["success"]=success,["result"]=ActivitySummary.Short(result,500)});
                            }
                        }
                        foreach(var old in hierarchyCommitments.OfType<JObject>().Where(c=>decision.override_commitments.Contains(c.Value<string>("id"))).ToList()) old.Remove();
                        foreach(var proposal in decision.Accepted.Where(p=>p.manager!="workforce"&&!failedParents.Contains(p.id)&&(p.requests.Count>0||p.labor_requests.Count>0))) {
                            bool staffed=outcomes.Any(o=>o.Value<string>("requestId")==proposal.id && o.Value<bool>("success"));
                            var reservation=proposal.requests.Count>0?JObject.FromObject(proposal.resource_requests):new JObject();
                            if(staffed) reservation["pawn_hours"]=proposal.labor_requests.Sum(l=>l.hours);
                            hierarchyCommitments.Add(new JObject{["id"]=proposal.id,["mapId"]=map.uniqueID,["manager"]=proposal.manager,["summary"]=proposal.summary,["priority"]=proposal.priority,["pending_labor"]=!staffed && proposal.labor_requests.Count>0,["labor_requests"]=JArray.FromObject(proposal.labor_requests),["resource_requests"]=reservation,["untilTick"]=Find.TickManager.TicksGame+(int)(proposal.review_after_hours*2500)});
                        }
                        foreach(var ongoing in hierarchyCommitments.OfType<JObject>().Where(c=>c.Value<bool?>("pending_labor")==true && outcomes.Any(o=>o.Value<string>("requestId")==c.Value<string>("id")&&o.Value<bool>("success")))) {
                            ongoing["pending_labor"]=false; ongoing["resource_requests"]["pawn_hours"]=(ongoing["labor_requests"] as JArray??new JArray()).Sum(l=>l.Value<double>("hours"));
                        }
                        hierarchyRecent.Insert(0,new JObject{["summary"]=decision.summary,["orders"]=outcomes.Count(o=>o.Value<bool>("success")),["problems"]=new JArray(outcomes.OfType<JObject>().Where(o=>!o.Value<bool>("success")).Take(3).Select(o=>StateTransfer.Select(o,"action","result"))),["tick"]=Find.TickManager.TicksGame}); while(hierarchyRecent.Count>8) hierarchyRecent.RemoveAt(hierarchyRecent.Count-1);
                        Plan=decision.summary; if(direction&&!string.IsNullOrWhiteSpace(decision.player_response)) Record("Colony: "+decision.player_response);
                        else Record(ActivitySummary.Short(decision.summary,300));
                        hierarchyNextTick=Math.Min(Find.TickManager.TicksGame+(int)(decision.review_after_hours*2500),managerNextTicks.Count>0?managerNextTicks.Values.Min():int.MaxValue);
                        Log.Message("[RimBot Hierarchy] "+new JObject{["event"]="execution",["cycle"]=cycle,["decision"]=decision.Json(),["outcomes"]=outcomes,["totalLatencyMs"]=(Clock.Elapsed.TotalSeconds-started)*1000}.ToString(Formatting.None));
                        WatchCurrentState(); return true;
                    });
                } catch(Exception ex) { callbacks.Enqueue(()=> { hierarchyNextTick=Find.TickManager.TicksGame+2500; Finish("Colony review stopped: "+ActivitySummary.Short(ex.Message,250)); }); }
            });
        }
    }
}
