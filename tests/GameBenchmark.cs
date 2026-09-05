// Test assembly only. Fixture setup is never compiled into the shipping mod.
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBot.Colony;
using RimBot.Models;
using RimBot.Tools;
using RimWorld;
using UnityEngine;
using Verse;
namespace RimBot.Colony
{
    public sealed partial class ColonyManager
    {
        partial void BenchmarkPlans(ref bool skipPlans) { skipPlans=Environment.GetCommandLineArgs().Contains("-rimbot-benchmark"); }
    }
}
namespace RimBot.Tests
{
    [StaticConstructorOnStartup]
    public static class BenchmarkBackground
    {
        static BenchmarkBackground() { if(Environment.GetCommandLineArgs().Contains("-rimbot-benchmark")) { Prefs.RunInBackground=true; Application.runInBackground=true; var driver=new GameObject("RimBot benchmark loading"); UnityEngine.Object.DontDestroyOnLoad(driver); driver.AddComponent<BenchmarkLoading>(); } }
    }
    public sealed class BenchmarkLoading : MonoBehaviour
    {
        // Native long events otherwise wait for an OnGUI repaint in a hidden window.
        public void Update() { LongEventHandler.SetCurrentEventText(""); }
    }
    public sealed class GameBenchmark : GameComponent
    {
        private static bool loaded;
        private bool started,done;
        private float startTime;
        private int startTick,initialPawns;
        private int[] originalBlueprintIds;
        private const string Fixture="RimBot-bed-shortage-v1";
        private string ResultPath=>Path.Combine(GenFilePaths.SaveDataFolderPath,"benchmark-result.json");
        public GameBenchmark(Game game) { }
        public override void GameComponentUpdate()
        {
            if(!Environment.GetCommandLineArgs().Contains("-rimbot-benchmark") || done || Find.CurrentMap==null) return;
            try {
                var map=Find.CurrentMap;
                var manager=ColonyManager.Current;
                if(!loaded) {
                    loaded=true;
                    if(File.Exists(GenFilePaths.FilePathForSavedGame(Fixture))) {
                        LongEventHandler.QueueLongEvent(()=>GameDataSaveLoader.LoadGame(Fixture),"LoadingLongEvent",false,null); return;
                    }
                    Setup(map); manager.SetControl(ManagerControl.Manual);
                    GameDataSaveLoader.SaveGame(Fixture);
                    // First run also reloads the fixture, so all measured runs follow the same path.
                    LongEventHandler.QueueLongEvent(()=>GameDataSaveLoader.LoadGame(Fixture),"LoadingLongEvent",false,null); return;
                }
                if(LongEventHandler.AnyEventNowOrWaiting) return;
                if(!started) {
                    File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath,"benchmark-started.txt"),DateTime.UtcNow.ToString("O"));
                    started=true; startTime=Time.realtimeSinceStartup; startTick=Find.TickManager.TicksGame;
                    initialPawns=map.mapPawns.FreeColonistsSpawned.Count;
                    var original=map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Where(t=>t.def.entityDefToBuild==ThingDefOf.Bed).ToList();
                    originalBlueprintIds=original.Select(t=>t.thingIDNumber).ToArray();
                    foreach(var target in original) map.GetComponent<ConstructionTargets>().Observe(target);
                    RimBotMod.Settings.managerProvider=LLMProviderType.Local;
                    var mode=Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith("-rimbot-reasoning="));
                    RimBotMod.Settings.localReasoningEffort=mode==null?"none":mode.Substring("-rimbot-reasoning=".Length);
                    manager.Goal="Finish the two existing bed blueprints using normal colony work. Assess and allow needed supplies. Do not add buildings or pursue other projects. Report a blocker if work cannot proceed.";
                    manager.Plan=""; manager.SetControl(ManagerControl.Automate);
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Fast;
                    Log.Message("[RimBot Benchmark] START bed-shortage-v1; model="+RimBotMod.Settings.managerModel);
                }
                int built=map.listerBuildings.allBuildingsColonist.Count(b=>b.def==ThingDefOf.Bed);
                if(map.mapPawns.FreeColonistsSpawned.Count<initialPawns) { Complete(false,"Colonist lost",built); return; }
                if(built>=3) {
                    foreach(int oldId in originalBlueprintIds) {
                        var hint=JObject.Parse(map.GetComponent<ConstructionTargets>().Missing(oldId));
                        if(!hint["currentObjects"].Any(t=>t.Value<string>("stage")=="built" && t.Value<string>("defName")=="Bed")) throw new Exception("Missing current built bed at original blueprint site");
                        bool rejected=false;
                        try { SelectionInspection.Read(map,new JObject{["targetId"]=oldId}); } catch(ArgumentException ex) { rejected=JObject.Parse(ex.Message).Value<string>("error")=="target_gone"; }
                        if(!rejected) throw new Exception("Stale target was silently accepted");
                    }
                    Complete(true,"Three actual beds completed; stale blueprint queries report current built beds",built); return;
                }
                if(Time.realtimeSinceStartup-startTime>600 || manager.TotalToolCalls>=100) { Complete(false,"Time or tool-call budget exceeded",built); return; }
            } catch(Exception ex) { Complete(false,ex.ToString(),0); }
        }
        private void Setup(Map map)
        {
            if(map.mapPawns.FreeColonistsSpawned.Count!=3) throw new Exception("Fixture requires three starting colonists");
            var capable=map.mapPawns.FreeColonistsSpawned.Where(p=>!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).ToList();
            if(capable.Count==0) throw new Exception("No capable constructor in quicktest seed");
            foreach(var pawn in capable) pawn.workSettings.SetPriority(WorkTypeDefOf.Construction,1);
            var origin=map.mapPawns.FreeColonistsSpawned[0].Position;
            var area=CellRect.CenteredOn(origin,16).Cells.First(c=>c.InBounds(map) &&
                CellRect.FromLimits(c.x,c.z,c.x+7,c.z+5).Cells.All(t=>t.InBounds(map) && !t.Fogged(map) && t.Standable(map) && t.GetEdifice(map)==null && t.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light)));
            foreach(var wood in map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).ToList()) wood.Destroy();
            var bed=ThingMaker.MakeThing(ThingDefOf.Bed,ThingDefOf.WoodLog); bed.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(bed,area,map,Rot4.North);
            for(int i=1;i<=2;i++) ColonyTools.Execute(map,new ToolCall{Name="architect_build",Arguments=new JObject{
                ["defName"]="Bed",["material"]="WoodLog",["x"]=area.x+i*2,["z"]=area.z,["rotation"]=0}},p=>{});
            for(int i=0;i<3;i++) {
                var wood=ThingMaker.MakeThing(ThingDefOf.WoodLog); wood.stackCount=i==0?36:50;
                GenSpawn.Spawn(wood,new IntVec3(area.x+i*2,0,area.z+4),map);
                wood.SetForbidden(i!=0,false);
            }
            Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
        }
        private void Complete(bool pass,string reason,int beds)
        {
            done=true;
            var m=ColonyManager.Current; m?.SetControl(ManagerControl.Manual);
            File.WriteAllText(ResultPath,new JObject{["passed"]=pass,["reason"]=reason,["fixture"]=Fixture,
                ["model"]=RimBotMod.Settings.managerModel,["reasoning"]=RimBotMod.Settings.localReasoningEffort,
                ["builtBeds"]=beds,["toolCalls"]=m?.TotalToolCalls,["repeatedResults"]=m?.RepeatedToolResults,
                ["recoveries"]=m?.RecoveryRequests,["tokens"]=m?.Tokens,
                ["gameTicks"]=Find.TickManager.TicksGame-startTick,["seconds"]=Time.realtimeSinceStartup-startTime}.ToString());
            Log.Message("[RimBot Benchmark] "+(pass?"PASS: ":"FAIL: ")+reason);
            Application.Quit();
        }
    }
}
