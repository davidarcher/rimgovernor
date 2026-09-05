using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Models;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public sealed partial class ColonyManager
    {
        public StrategicPlan Strategy { get; private set; }
        public string StrategyStatus="A seasonal plan will be created when Automate is enabled.";
        private string strategyJson="",strategyGoal="";
        private int strategyMap=-1,strategyQuadrum=-1,strategyTick=-1,strategyRetryTick;
        private int strategyPopulation,strategyShelter;
        private float strategyFood;
        private double strategyRetryTime;
        private readonly Dictionary<string,double> strategicMetrics=new Dictionary<string,double>();
        private readonly HashSet<string> availableTools=new HashSet<string>(ToolCatalog.Definitions().Select(t=>t.Name));
        private string strategicCalendar="";
        private bool discardLoadedBrief;
        private void SaveStrategyState()
        {
            if(Scribe.mode==LoadSaveMode.Saving) strategyJson=Strategy?.Serialize()??"";
            Scribe_Values.Look(ref strategyJson,"strategicPlan","");
            Scribe_Values.Look(ref strategyGoal,"strategicGoal","");
            Scribe_Values.Look(ref strategyMap,"strategicMap",-1);
            Scribe_Values.Look(ref strategyQuadrum,"strategicQuadrum",-1);
            Scribe_Values.Look(ref strategyTick,"strategicTick",-1);
            Scribe_Values.Look(ref strategyRetryTick,"strategicRetryTick",0);
            Scribe_Values.Look(ref strategyPopulation,"strategicPopulation",0);
            Scribe_Values.Look(ref strategyShelter,"strategicShelter",0);
            Scribe_Values.Look(ref strategyFood,"strategicFood",0f);
            if(Scribe.mode==LoadSaveMode.PostLoadInit && !string.IsNullOrEmpty(strategyJson)) {
                try { Strategy=StrategicPlan.Parse(strategyJson); StrategyStatus="Saved strategy loaded; progress is checked against the colony."; }
                catch(Exception ex) { Strategy=null; discardLoadedBrief=true; Log.Warning("[RimBot Manager] Saved strategy rejected; replanning required: "+ex.Message); }
            }
        }
        private void ObserveStrategy(Map map,ColonyFacts facts)
        {
            if(strategyMap>=0 && strategyMap!=map.uniqueID) { Strategy=null; strategyTick=-1; strategyQuadrum=-1; strategyRetryTick=0; }
            strategyMap=map.uniqueID;
            strategicMetrics.Clear();
            strategicMetrics["colonists"]=facts.Colonists;
            strategicMetrics["armed_colonists"]=map.mapPawns.FreeColonistsSpawned.Count(p=>p.equipment?.Primary!=null);
            strategicMetrics["capable_fighters"]=map.mapPawns.FreeColonistsSpawned.Count(p=>!p.WorkTagIsDisabled(WorkTags.Violent));
            strategicMetrics["growing_cells"]=map.zoneManager.AllZones.OfType<Zone_Growing>().Sum(g=>g.Cells.Count);
            strategicMetrics["configured_food_bills"]=map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().Sum(t=>t.BillStack.Bills.Count(b=>b.recipe.products?.Any(p=>p.thingDef.IsNutritionGivingIngestible && !p.thingDef.IsDrug)==true));
            strategicMetrics["research_active"]=Find.ResearchManager.GetProject()!=null?1:0;
            strategicMetrics["sheltered_slots"]=facts.ShelteredSlots;
            strategicMetrics["regular_bed_slots"]=facts.RegularBedSlots;
            strategicMetrics["pending_bed_slots"]=facts.PendingBedSlots;
            strategicMetrics["food_days"]=facts.FoodDays;
            strategicMetrics["hostiles"]=facts.Hostiles;
            strategicMetrics["patients"]=facts.Patients;
            strategicMetrics["medical_emergencies"]=facts.Bleeding+facts.Downed+facts.TemperatureInjuries;
            strategicMetrics["stockpiles"]=map.zoneManager.AllZones.OfType<Zone_Stockpile>().Count();
            strategicMetrics["food_bills"]=facts.CookingOrders;
            strategicMetrics["pending_orders"]=facts.PendingOrders;
            foreach(var group in map.listerBuildings.allBuildingsColonist.GroupBy(b=>b.def.defName)) strategicMetrics["building:"+group.Key]=group.Count();
            foreach(var group in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                .Where(t=>t.def.entityDefToBuild!=null).GroupBy(t=>t.def.entityDefToBuild.defName)) strategicMetrics["pending:"+group.Key]=group.Count();
            // Fill requested valid counters with zero: absent buildings are not unknown metrics.
            if(Strategy!=null) foreach(var metric in Strategy.Projects.SelectMany(p=>p["completeWhen"]).Select(c=>c["metric"].Value<string>()).Distinct()) {
                if(metric.StartsWith("building:")) {
                    var def=DefDatabase<ThingDef>.GetNamedSilentFail(metric.Substring(9));
                    if(def?.category==ThingCategory.Building && !strategicMetrics.ContainsKey(metric)) strategicMetrics[metric]=0;
                } else if(metric.StartsWith("research:")) {
                    var def=DefDatabase<ResearchProjectDef>.GetNamedSilentFail(metric.Substring(9));
                    if(def!=null) strategicMetrics[metric]=def.IsFinished?1:0;
                }
            }
            var location=Find.WorldGrid.LongLatOf(map.Tile);
            strategicCalendar=GenDate.Quadrum(Find.TickManager.TicksAbs,location.x).ToString()+", latitude "+location.y.ToString("F0")+"; world quadrum "+(Find.TickManager.TicksAbs/900000)+", "+(15-(Find.TickManager.TicksAbs%900000)/60000)+
                " days to next quadrum; biome "+map.Biome.label+"; outdoor temperature "+map.mapTemperature.OutdoorTemp.ToString("F0")+" C. Each quadrum is 15 days, year 60 days. Adapt to actual climate, not an assumed northern winter.";
        }
        public string ProjectState(JToken project) => Strategy?.State(project,strategicMetrics,availableTools)??"Unplanned";
        private string TacticalStrategy()
        {
            string direction=string.IsNullOrEmpty(LatestDirection)?"":"Current player direction: "+LatestDirection+"\nAddress this direction before background projects unless an immediate emergency intervenes. Use game facts to recognize when it is fulfilled.\n";
            if(Strategy==null) return direction+"No strategic plan yet. Handle immediate measured needs; report unsupported actions.";
            return direction+"Today: "+DayBrief+"\nSeason direction: "+Strategy.Season+"\nReady projects: "+StateTransfer.Projects(Strategy.Tactical(strategicMetrics,availableTools)).ToString(Formatting.None)+
                "\nOther projects remain saved. Do not recreate completed work. Urgent needs take precedence; only game measurements establish completion.";
        }
        public void RegenerateStrategy()
        {
            if(Busy) { StrategyStatus="Wait for the current AI request to finish before regenerating strategy."; return; }
            var map=mapId<0?Find.CurrentMap:Find.Maps.FirstOrDefault(m=>m.uniqueID==mapId);
            if(map==null) { StrategyStatus="Open a colony map first."; return; }
            RefreshObjectives(true);
            if(!TryStartStrategy(map,true)) StrategyStatus="Cannot regenerate yet: check the model/provider settings and hourly request allowance.";
        }
        private bool TryStartStrategy(Map map,bool force=false)
        {
            int tick=Find.TickManager.TicksGame,quadrum=Find.TickManager.TicksAbs/900000;
            if(!force && (tick<strategyRetryTick || Clock.Elapsed.TotalSeconds<strategyRetryTime)) return false;
            string reason=force?"Player requested a new strategy":StrategySchedule.Due(Strategy!=null,strategyQuadrum,quadrum,strategyTick,tick,strategyGoal,Goal,strategyPopulation,
                (int)strategicMetrics["colonists"],strategyFood,strategicMetrics["food_days"],strategyShelter,(int)strategicMetrics["sheltered_slots"],Strategy!=null && Strategy.Projects.Count>0 && Strategy.Projects.All(p=>ProjectState(p)=="Complete"));
            if(reason==null && strategyTick>=0 && tick-strategyTick>=420000) reason="Weekly review";
            if(reason==null) return false;
            // Aggregate pawn counts are observations, not gates on planning or unrelated work.
            var settings=RimBotMod.Settings;
            bool local=settings.managerProvider==LLMProviderType.Local;
            if(string.IsNullOrWhiteSpace(settings.managerModel) || (!local && string.IsNullOrWhiteSpace(settings.GetApiKeyForProvider(settings.managerProvider)))) return false;
            if(!force && !Budget.CanReview(Clock.Elapsed.TotalSeconds,settings.reviewSeconds)) return false;
            ILanguageModel provider;
            try { provider=local?new LocalModel(settings.localUrl,settings.strategicReasoningEffort,TimeSpan.FromMinutes(10)):LLMModelFactory.GetModel(settings.managerProvider); }
            catch(Exception ex) { StrategyStatus=ex.Message; strategyRetryTime=Clock.Elapsed.TotalSeconds+60; return false; }
            if(!Budget.TryRequest(Clock.Elapsed.TotalSeconds,local?settings.localRequestsPerHour:settings.requestsPerHour)) return false;
            Budget.StartReview(Clock.Elapsed.TotalSeconds);
            mapId=map.uniqueID;
            BeginProgress("Planning the season"); ReviewRequests=1;
            Busy=true;
            int token=++generation;
            string signature=settings.ConnectionSignature,goal=Goal,model=settings.managerModel,key=settings.GetApiKeyForProvider(settings.managerProvider);
            int population=(int)strategicMetrics["colonists"],shelter=(int)strategicMetrics["sheltered_slots"];
            float food=(float)strategicMetrics["food_days"];
            StrategyStatus="Planning the next season…"; Status=StrategyStatus; Log.Message("[RimBot Debug] Planning: "+reason);
            var messages=new List<ChatMessage> {
                new ChatMessage("system","You are the colony's strategic planner. Reason about priorities, constraints, climate, resources and dependencies, then call save_strategy once. Do not issue world orders. " +
                    "Write concise colony notes: short verb-led project titles, one sentence per horizon, and brief concrete steps. No preamble, motivational language, model/tool jargon or repeated explanations. Example title: Plant the first crop. Example season: Grow rice and finish a wood-fired kitchen before winter. Plan concrete achievable projects for the next 15 days, directional milestones for the next year, and a flexible three-year ambition. Project goals are open-ended, not restricted to the five survival indicators. " +
                    "Keep useful existing projects/IDs; do not duplicate facilities. Express furniture quantities as total capacity targets, not repeatable batches. Separate bed capacity from enclosing/roofing existing sleeping places: a sheltered_slots shortfall alone never establishes a need for more beds. Basic shelter and two days of food are a survival floor, not the end of colony development. Under the default direction plan sustainable food, cooking/butchering, useful research and infrastructure once urgent needs are covered. Respect explicit player limits or different priorities. Forbidden items and hostile presence are not cleanup backlogs. Allow selected needed supplies only after assessing their locations; avoid creating jobs in hostile areas. Do not plan map-wide unforbidding or extermination by default. Provide observable completion conditions. Do not equate a stove with a functioning kitchen or a bed with a clinic. Name unsupported capabilities and unverifiable criteria honestly. " +
                    "Only these metrics are currently measured: armed_colonists, capable_fighters, growing_cells, configured_food_bills, research_active (1 selected/0 none), colonists, regular_bed_slots (built ordinary sleeping capacity), pending_bed_slots (capacity already ordered), sheltered_slots (built ordinary slots in enclosed fully roofed rooms), food_days, hostiles (visible standing pawns hostile to the player faction, NOT a count of active attackers), patients, medical_emergencies, stockpiles, food_bills (currently active bills), pending_orders, building:ExactDefName (built player structures), research:ExactDefName (1 finished/0 unfinished). Unknown metrics block verification. " +
                    "Tools available to daily execution: "+string.Join("; ",ToolCatalog.Definitions().Select(t=>t.Name+": "+ActivitySummary.Short(t.Description,100)))),
                new ChatMessage("user","Trigger: "+reason+"\nPlayer direction: "+goal+"\nRecent player messages (newest wins): "+PlayerNotes+"\nCalendar/climate: "+strategicCalendar+"\nColony: "+StateTransfer.Colony(snapshot,true).ToString(Formatting.None)+
                    "\nMeasured counters: "+JObject.FromObject(strategicMetrics).ToString(Formatting.None)+"\nPrevious strategy: "+(Strategy?.Serialize()??"none"))
            };
            Log.Message(StateTransfer.Sizes("strategy",snapshot,StateTransfer.Colony(snapshot,true)));
            Task.Run(async ()=> {
                ModelResponse response;
                try { response=await provider.SendToolRequest(messages,StrategicPlan.Tools(),model,key,local?0:8192,ThinkingLevel.Medium); }
                catch(Exception ex) { response=ModelResponse.FromError(ex.Message); }
                callbacks.Enqueue(()=> {
                    if(token!=generation || Verse.Current.Game!=owner || !Find.Maps.Contains(map) || signature!=RimBotMod.Settings.ConnectionSignature || goal!=Goal) {
                        Finish("Strategic response discarded after mode, direction or provider changed."); return;
                    }
                    Tokens+=response.TokensUsed;
                    Log.Message("[RimBot Context] strategy provider tokens: input="+response.InputTokens+", output="+response.OutputTokens);
                    try {
                        if(!response.Success || response.StopReason==StopReason.MaxTokens) throw new ArgumentException(response.ErrorMessage??"Planner output was incomplete.");
                        if(response.ToolCalls==null || response.ToolCalls.Count!=1 || response.ToolCalls[0].Name!="save_strategy") throw new ArgumentException("Planner must return exactly one save_strategy call; no plan replaced.");
                        var candidate=StrategicPlan.Parse(response.ToolCalls[0].Arguments.ToString(Formatting.None));
                        Strategy=candidate; strategyGoal=goal; strategyTick=tick; strategyQuadrum=quadrum;
                        strategyPopulation=population; strategyShelter=shelter; strategyFood=food; strategyRetryTick=0;
                        StrategyStatus="Season plan updated.";
                        Log.Message("[RimBot Debug] Strategy: "+candidate.Serialize());
                        RefreshObjectives(true);
                        // Project states are visible in the plan; do not repeat them in the activity feed.
                        lastFingerprint=null; briefRequested=true; briefTick=-1;
                        Finish(StrategyStatus);
                    } catch(Exception ex) {
                        strategyRetryTick=Find.TickManager.TicksGame+60000; strategyRetryTime=Clock.Elapsed.TotalSeconds+300;
                        StrategyStatus="Strategic planning failed: "+ex.Message+" Existing plan retained; daily management continues. Retry after one game day and five real minutes.";
                        Finish(StrategyStatus);
                    }
                });
            });
            return true;
        }
    }
}
