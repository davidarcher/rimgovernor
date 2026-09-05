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
        private int actionLimit, hourlyLimit;
        private bool steerPending,notificationPending;
        private readonly NotificationFeed notificationFeed=new NotificationFeed();
        private double nextNotificationCheck;
        public string PlayerNotes="";
        public string LatestDirection="";
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
            Scribe_Values.Look(ref LatestDirection,"managerLatestDirection","");
            if(Scribe.mode==LoadSaveMode.PostLoadInit && string.IsNullOrEmpty(LatestDirection)) {
                int latest=PlayerNotes.LastIndexOf("\nPlayer: ",StringComparison.Ordinal);
                if(latest>=0) LatestDirection=PlayerNotes.Substring(latest+9);
            }
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
            if(Scribe.mode==LoadSaveMode.PostLoadInit && discardLoadedBrief) { DayBrief=""; Plan=""; briefTick=-1; discardLoadedBrief=false; }
            SaveTasks();
            SaveDecisions();
            SaveHierarchy();
            // Facts/objective statuses are recalculated from the actual map after loading.
            // The saved control mode determines whether reviews run after loading.
        }
        partial void BenchmarkPlans(ref bool skipPlans);
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
            bool skipPlans=false; BenchmarkPlans(ref skipPlans);
            if(!Automatic || Busy || Find.TickManager.Paused) return;
            var map=mapId<0 ? Find.CurrentMap : Find.Maps.FirstOrDefault(m=>m.uniqueID==mapId);
            if(map==null) { Status="Waiting for the colony map."; return; }
            if(snapshot==null) return;
            if(!skipPlans && !notificationPending && !executionPending && !steerPending && TryStartStrategy(map)) return;
            if(!skipPlans && !notificationPending && !executionPending && !steerPending && TryStartDailyBrief(map,false)) return;
            string fingerprint=ColonyTools.Fingerprint(snapshot)+ColonyObjectives.DecisionKey(Objectives)+Goal;
            if(!notificationPending && !executionPending && !steerPending && fingerprint==lastFingerprint && Find.TickManager.TicksGame<hierarchyNextTick) { if(!Status.StartsWith("Blocked:")) Status="Watching the colony."; return; }
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
            ObserveTasks(map,tick);
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
            foreach(var target in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)))
                if(!target.Position.Fogged(map)) map.GetComponent<ConstructionTargets>().Observe(target);
            snapshot=ColonyTools.Snapshot(map);
            snapshot["playerDirection"]=LatestDirection;
            var anchor=map.listerBuildings.allBuildingsColonist.OfType<RimWorld.Building_Bed>().FirstOrDefault()?.Position??map.GetComponent<ColonyLocation>().Center;
            int gridX=Math.Max(0,Math.Min(map.Size.x-17,anchor.x-8)),gridZ=Math.Max(0,Math.Min(map.Size.z-17,anchor.z-8));
            snapshot["localMap"]=SpatialView.Read(map,gridX,gridZ,Math.Min(17,map.Size.x),Math.Min(17,map.Size.z));
            snapshot["sleepingCapacity"]=ColonyObjectives.SleepingCapacity(facts);
            snapshot["objectives"]=new JArray(Objectives.Select(o=>o.ToJson()));
            snapshot["trackedTasks"]=ActiveTrackedTasks;
            snapshot["workFocus"]=WorkFocus(map);
            snapshot["repeatedObservations"]=decisions.Repeats();
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
            LatestDirection=text;
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
            try { provider=settings.managerProvider==LLMProviderType.Local ? new LocalModel(settings.localUrl, steerPending ? "medium" : settings.strategicReasoningEffort,TimeSpan.FromMinutes(10)) : LLMModelFactory.GetModel(settings.managerProvider); }
            catch(Exception ex) { Status=ex.Message; return; }
            if(mapId!=map.uniqueID) { Plan=""; lastFingerprint=null; decisions.Clear(); }
            mapId=map.uniqueID;
            RefreshObjectives(true);
            lastFingerprint=ColonyTools.Fingerprint(snapshot)+ColonyObjectives.DecisionKey(Objectives)+Goal;
            Budget.StartReview(Clock.Elapsed.TotalSeconds);
            endpointAtStart=settings.ConnectionSignature;
            BeginProgress("Managing the colony");
            bool directed=steerPending, notices=notificationPending, workChanged=executionPending;
            Busy=true; actions=0; steerPending=false; executionPending=false; notificationPending=false;
            bool local=settings.managerProvider==LLMProviderType.Local;
            actionLimit=local ? 0 : 4;
            hourlyLimit=local ? settings.localRequestsPerHour : settings.requestsPerHour;
            int token=++generation;
            Log.Message("[RimBot Debug] Hierarchical review on map "+mapId+" using "+settings.managerProvider+" / "+settings.managerModel);
            StartHierarchy(map,provider,settings.managerModel,settings.GetApiKeyForProvider(settings.managerProvider),local?0:settings.maxTokens,token,directed,notices,workChanged);
        }
        private void WatchCurrentState()
        {
            RefreshObjectives(true);
            lastFingerprint=ColonyTools.Fingerprint(snapshot)+ColonyObjectives.DecisionKey(Objectives)+Goal;
            executionPending=false; Busy=false; Status="Watching the colony.";
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
