using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Models;
using Verse;

namespace RimBot.Colony
{
    public sealed partial class ColonyManager : GameComponent
    {
        private readonly Game owner;
        private readonly ConcurrentQueue<Action> callbacks=new ConcurrentQueue<Action>();
        private static readonly Stopwatch Clock=Stopwatch.StartNew();
        // Shared across saves in this process so switching saves cannot reset the request cap.
        private static readonly ReviewBudget Budget=new ReviewBudget();
        public readonly List<string> History=new List<string>();
        public string Plan="";
        public const string DefaultGoal="Establish and steadily develop a self-sufficient colony: arm capable colonists, secure shelter, create food production and cooking, begin useful research, then improve defenses and infrastructure. Maintain reserves and pursue further projects after basic needs are met.";
        public string Goal=DefaultGoal;
        public bool Automatic => control != ManagerControl.Manual;
        private ManagerControl control = ManagerControl.Manual;
        public ManagerControl Control => control;
        public List<ColonyObjective> Objectives { get; private set; } = new List<ColonyObjective>();
        private double nextObservation;
        private int progressMapId = -1, previousOrders = -1, lastProgressTick;
        private float previousWork;
        private string objectiveKey;
        public string TargetLabel => mapId < 0 ? "Current map" : "Map " + mapId;
        private int reviewLimit, actionLimit, hourlyLimit;
        private readonly List<string> reviewNotes = new List<string>();
        private ToolSession toolSession=new ToolSession();
        private bool steerPending,notificationPending;
        private readonly NotificationFeed notificationFeed=new NotificationFeed();
        private double nextNotificationCheck;
        public string PlayerNotes="";
        public bool Busy { get; private set; }
        public string Status="Manual mode. Enable Automate to manage the colony.";
        public int Tokens { get; private set; }
        public int Requests => Budget.Used(Clock.Elapsed.TotalSeconds);
        private int mapId=-1;
        private int generation;

        private string lastFingerprint;
        private JObject snapshot;
        private int actions;
        private string endpointAtStart;
        public ColonyManager(Game game) { owner=game; }
        public static ColonyManager Current => Verse.Current.Game?.GetComponent<ColonyManager>();
        public override void ExposeData()
        {
            Scribe_Values.Look(ref PlayerNotes,"managerPlayerNotes","");
            Scribe_Values.Look(ref Plan,"colonyManagerPlan","");
            Scribe_Values.Look(ref Goal,"colonyManagerGoal",DefaultGoal);
            if(Scribe.mode==LoadSaveMode.PostLoadInit && Goal=="Keep the colony supplied and organized. Reuse shared facilities and let ordinary jobs finish.") Goal=DefaultGoal;
            Scribe_Values.Look(ref mapId,"colonyManagerMap",-1);
            string savedMode=control.ToString();
            Scribe_Values.Look(ref savedMode,"managerControl","Manual");
            if(Scribe.mode==LoadSaveMode.LoadingVars)
                control=savedMode=="Automate" ? ManagerControl.Automate : ManagerControl.Manual;
            Scribe_Values.Look(ref progressMapId,"objectiveProgressMap",-1);
            Scribe_Values.Look(ref previousOrders,"objectivePreviousOrders",-1);
            Scribe_Values.Look(ref previousWork,"objectivePreviousWork",0f);
            Scribe_Values.Look(ref lastProgressTick,"objectiveLastProgressTick",0);
            SaveStrategyState();
            SaveDailyState();
            // Facts/objective statuses are recalculated from the actual map after loading.
            // The saved control mode determines whether reviews run after loading.
        }
        public override void GameComponentUpdate()
        {
            if(Find.CurrentMap!=null && Clock.Elapsed.TotalSeconds>=nextNotificationCheck) {
                nextNotificationCheck=Clock.Elapsed.TotalSeconds+1;
                if(notificationFeed.Observe(ColonyNotifications.Read())) { notificationPending=true; nextObservation=0; }
            }
            while(callbacks.TryDequeue(out var callback))
            {
                if(Verse.Current.Game!=owner) continue;
                try { callback(); } catch(Exception ex) { Finish("Review failed: "+ex.Message); }
            }
            RefreshObjectives();
            if(!Automatic || Busy || Find.TickManager.Paused) return;
            var map=mapId<0 ? Find.CurrentMap : Find.Maps.FirstOrDefault(m=>m.uniqueID==mapId);
            if(map==null) { Status="Waiting for the colony map."; return; }
            if(snapshot==null) return;
            if(!notificationPending && !executionPending && !steerPending && TryStartStrategy(map)) return;
            if(!notificationPending && !executionPending && TryStartDailyBrief(map,steerPending)) return;
            string fingerprint=ColonyTools.Fingerprint(snapshot)+ColonyObjectives.DecisionKey(Objectives)+Goal;
            if(!notificationPending && !executionPending && !steerPending && fingerprint==lastFingerprint) { if(!Status.StartsWith("Blocked:")) Status="Watching the colony."; return; }
            if(!notificationPending && !executionPending && !steerPending && !Budget.CanReview(Clock.Elapsed.TotalSeconds,RimBotMod.Settings.reviewSeconds)) { Status="Watching the colony."; return; }
            Start(map);
        }
        public void RefreshObjectives(bool force=false)
        {
            double now=Clock.Elapsed.TotalSeconds;
            if(!force && now<nextObservation) return;
            nextObservation=now+10;
            var map=mapId<0 ? Find.CurrentMap : Find.Maps.FirstOrDefault(m=>m.uniqueID==mapId);
            if(map==null) { Objectives.Clear(); snapshot=null; return; }
            var facts=ColonyObserver.Observe(map);
            int tick=Find.TickManager.TicksGame;
            if(progressMapId!=map.uniqueID || facts.PendingOrders!=previousOrders || Math.Abs(facts.ConstructionWork-previousWork)>0.1f || facts.PendingOrders==0)
            {
                progressMapId=map.uniqueID;
                previousOrders=facts.PendingOrders;
                previousWork=facts.ConstructionWork;
                lastProgressTick=tick;
            }
            facts.ConstructionStalled=facts.PendingOrders>0 && tick-lastProgressTick>=15000;
            ObserveStrategy(map,facts);
            var updated=ColonyObjectives.Evaluate(facts);
            string key=ColonyObjectives.DecisionKey(updated);
            if(objectiveKey!=null && key!=objectiveKey)
                foreach(var item in updated)
                {
                    var before=Objectives.FirstOrDefault(o=>o.Id==item.Id);
                    if(before!=null && before.State!=item.State && item.State==ObjectiveState.Blocked) briefRequested=true;
                    if(before!=null && before.State!=item.State) Record(item.Title+": "+item.StateLabel+". "+item.Evidence);
                }
            Objectives=updated; objectiveKey=key;
            snapshot=ColonyTools.Snapshot(map);
            snapshot["objectives"]=new JArray(Objectives.Select(o=>o.ToJson()));
        }
        public void Pause()
        {
            control=ManagerControl.Manual;
            generation++;
            // Keep the request slot occupied until its response/timeout; discard any proposed actions.
            Status=Busy ? "Paused. Waiting for the outstanding request to finish; its actions will be discarded." : "Paused.";
            Record(Status);
        }
        public void Steer(string text)
        {
            text=(text??"").Trim();
            if(text.Length==0) return;
            if(text.Length>600) { Status="Keep directions under 600 characters."; return; }
            PlayerNotes=PlayerNotes+"\nPlayer: "+text;
            if(PlayerNotes.Length>1800) PlayerNotes=PlayerNotes.Substring(PlayerNotes.Length-1800);
            generation++; steerPending=true; lastFingerprint=null; nextObservation=0;
            Record("You: "+text);
            Status=Busy?"Direction queued; replacing the pending decision.":Automatic?"Direction received.":"Direction saved. Enable Automate to act.";
        }
        private void Start(Map map)
        {
            var settings=RimBotMod.Settings;
            if(string.IsNullOrWhiteSpace(settings.managerModel)) { Status="Set the model identifier in RimBot settings first."; return; }
            if(settings.managerProvider!=LLMProviderType.Local && string.IsNullOrWhiteSpace(settings.GetApiKeyForProvider(settings.managerProvider)))
            { Status="Set the selected provider's API key first."; return; }
            ILanguageModel provider;
            try { provider=settings.managerProvider==LLMProviderType.Local ? new LocalModel(settings.localUrl, settings.localReasoningEffort) : LLMModelFactory.GetModel(settings.managerProvider); }
            catch(Exception ex) { Status=ex.Message; return; }
            if(mapId!=map.uniqueID) { Plan=""; lastFingerprint=null; }
            mapId=map.uniqueID;
            RefreshObjectives(true);
            lastFingerprint=ColonyTools.Fingerprint(snapshot)+ColonyObjectives.DecisionKey(Objectives)+Goal;
            Budget.StartReview(Clock.Elapsed.TotalSeconds);
            endpointAtStart=settings.ConnectionSignature;
            Busy=true; actions=0; reviewNotes.Clear(); toolSession=new ToolSession(); steerPending=false; executionPending=false; notificationPending=false;
            bool local=settings.managerProvider==LLMProviderType.Local;
            reviewLimit=local ? 0 : 3;
            actionLimit=local ? 0 : 4;
            hourlyLimit=local ? settings.localRequestsPerHour : settings.requestsPerHour;
            int token=++generation;
            var messages=new List<ChatMessage> {
                new ChatMessage("system",ManagerPrompt.Text+(local ? " Local review: no turn or action cap. " : " Review budget: 3 requests, 4 actions. ")+"Automate mode: valid orders execute directly. Current map facts override stale plan claims. Use tools_search then tools_enable for exact schemas. Newest player direction takes precedence over older plans. Player preference: "+Goal+"\nPlayer messages: "+PlayerNotes),
                new ChatMessage("user",TacticalStrategy()+"\nDaily plan: "+Plan+"\nColony summary: "+snapshot.ToString(Formatting.None)) };
            Log.Message("[RimBot Debug] Review on map "+mapId+" using "+settings.managerProvider+" / "+settings.managerModel);
            Request(map,provider,messages,settings.managerModel,settings.GetApiKeyForProvider(settings.managerProvider),local?0:settings.maxTokens,token,0);
        }
        private void Request(Map map,ILanguageModel provider,List<ChatMessage> messages,string model,string key,int maxTokens,int token,int round,bool retried=false)
        {
            int notificationRevision=notificationFeed.Revision;
            var definitions=toolSession.Definitions();
            int requestChars=LocalModel.BuildRequest(messages,definitions,model,maxTokens).ToString(Formatting.None).Length;
            if(requestChars>18000 || retried)
            {
                RefreshObjectives(true);
                messages=new List<ChatMessage> { messages[0],new ChatMessage("user",TacticalStrategy()+"\nCurrent plan: "+Plan+
                    "\nColony: "+snapshot.ToString(Formatting.None)+"\nEarlier results: "+string.Join("; ",reviewNotes.Skip(Math.Max(0,reviewNotes.Count-8)))+
                    "\nContinue from current facts. Earlier completed orders remain in the game. Do not repeat them.") };
            }
            if(!Budget.TryRequest(Clock.Elapsed.TotalSeconds,hourlyLimit)) { Finish("Hourly request limit reached."); return; }
            Status="Checking the colony…";

            // No game API calls from this worker. All results return through GameComponentUpdate.
            Task.Run(async ()=> {
                ModelResponse response;
                try { response=await provider.SendToolRequest(messages,definitions,model,key,maxTokens,ThinkingLevel.None); }
                catch(Exception ex) { response=ModelResponse.FromError(ex.Message); }
                callbacks.Enqueue(()=> {
                    if(token!=generation || Verse.Current.Game!=owner || !Find.Maps.Contains(map) ||
                        endpointAtStart!=RimBotMod.Settings.ConnectionSignature || Find.TickManager.Paused)
                    { Finish("Review discarded: paused, map removed, or provider settings changed."); return; }
                    Tokens+=response.TokensUsed;
                    if(notificationFeed.Revision!=notificationRevision) {
                        notificationPending=false;
                        Log.Message("[RimBot Debug] New notifications arrived; reevaluating before applying pending orders.");
                        messages.Add(new ChatMessage("user","New player notifications: "+notificationFeed.LatestBatch+"\nCheck these before acting. The pending response was not executed."));
                        Request(map,provider,messages,model,key,maxTokens,token,round,true); return;
                    }
                    if(!response.Success) {
                        string error=response.ErrorMessage??"Unknown provider error";
                        if(!retried && (error.Contains("context") || error.Contains("output limit"))) {
                            Log.Message("[RimBot Debug] Retrying with fresh facts after: "+error);
                            Request(map,provider,messages,model,key,maxTokens,token,round,true); return;
                        }
                        Finish("Provider error: "+error); return;
                    }
                    if(response.StopReason==StopReason.MaxTokens) { Finish("Output limit reached; no actions from this response executed."); return; }
                    if(!string.IsNullOrWhiteSpace(response.Content)) {
                        Log.Message("[RimBot Debug] Model response: "+response.Content);
                        Record(ActivitySummary.Model(response.Content));
                    }
                    var calls=response.ToolCalls;
                    if(calls==null || calls.Count==0) { Busy=false; Status="Watching the colony."; nextObservation=0; return; }
                    if(calls.Count>8) { Finish("Too many tool calls in one response; nothing executed."); return; }
                    messages.Add(new ChatMessage("assistant",response.AssistantParts ?? new List<ContentPart>()));
                    var results=new List<ContentPart>();
                    bool blocked=false;
                    foreach(var call in calls)
                    {
                        string result; bool success=true;
                        try {
                            if(blocked) throw new InvalidOperationException("Review blocked; remaining calls skipped.");
                            if(ColonyTools.IsAction(call.Name) && actionLimit>0 && actions>=actionLimit) throw new InvalidOperationException("World action limit reached; defer to the next review.");
                            if(ColonyTools.IsAction(call.Name)) actions++;
                            if(!toolSession.Available(call.Name)) throw new ArgumentException("Enable this tool before calling it.");
                            if(call.Name=="tools_search") result=ToolSession.Discover(call.Arguments?.Value<string>("search")).ToString(Formatting.None);
                            else if(call.Name=="tools_enable") result=toolSession.Enable(call.Arguments?["names"] as JArray);
                            else result=ColonyTools.Execute(map,call,p=>Plan=p);
                            if(call.Name=="manager_report_blocker") {
                                blocked=true; briefRequested=true;
                                Messages.Message(result,RimWorld.MessageTypeDefOf.RejectInput,false);
                                Status=result;
                            }
                        }
                        catch(Exception ex) { result=ex.Message; success=false; }
                        string display=ActivitySummary.Tool(call,result,success);
                        if(!success || ColonyTools.IsAction(call.Name) || call.Name=="manager_report_blocker") Record(display);
                        reviewNotes.Add(display.Length>180 ? display.Substring(0,180) : display);
                        if(reviewNotes.Count>24) reviewNotes.RemoveAt(0);
                        Log.Message("[RimBot Debug] "+call.Name+" "+call.Arguments?.ToString(Formatting.None)+" => "+result);
                        if(result.Length>10000) result=new JObject{["responseTooLarge"]=true,["instruction"]="Query a smaller page or area. Full result is in the debug log; no partial JSON returned."}.ToString(Formatting.None);
                        results.Add(ContentPart.FromToolResult(call.Id,call.Name,success,result));
                    }
                    messages.Add(new ChatMessage("user",results));
                    if(blocked) { Finish(Status); return; }
                    if(reviewLimit>0 && round+1>=reviewLimit) { Finish("Review finished after "+reviewLimit+" steps. Shared plan retained."); return; }
                    RefreshObjectives(true);
                    messages.Add(new ChatMessage("user","Current colony after these calls: "+snapshot.ToString(Formatting.None)));
                    Request(map,provider,messages,model,key,maxTokens,token,round+1);
                });
            });
        }
        private void Finish(string status) { Busy=false; Status=status; Record(status); nextObservation=0; }
        public void SetControl(ManagerControl value)
        {
            if(control==value) return;
            Pause(); control=value; lastFingerprint=null; nextObservation=0;
            Record(value==ManagerControl.Manual ? "Manual mode: AI decisions are off." : "Automate mode: reviews run automatically and issue player orders.");
        }
        public void Record(string text)
        {
            if(string.IsNullOrWhiteSpace(text)) return;
            if(text.Length>6000) text=text.Substring(0,6000)+" … [truncated]";
            History.Add(DateTime.Now.ToString("HH:mm:ss")+" "+text);
            while(History.Count>100) History.RemoveAt(0);
            Log.Message("[RimBot Manager] "+text);
        }
    }
}
