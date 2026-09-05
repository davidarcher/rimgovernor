// Compiled ONLY with -p:GameSmoke=true. Never shipped in the production package.
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBot.Colony;
using RimBot.Tools;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBot.Tests
{
    public class GameSmoke : GameComponent
    {
        private int stage;
        private float doneAt;
        public GameSmoke(Game game) { }
        public override void GameComponentUpdate()
        {
            if(!Environment.GetCommandLineArgs().Contains("-rimbot-selftest")) return;
            if(stage==0 && Find.CurrentMap!=null && Find.CurrentMap.mapPawns.FreeColonistsSpawned.Count>0)
            {
                stage=1;
                try {
                    var map=Find.CurrentMap;
                    int pawns=map.mapPawns.FreeColonistsSpawned.Count;
                    var speed=Find.TickManager.CurTimeSpeed;
                    if(ColonyManager.Current.Automatic) throw new Exception("New games should default to Manual");
                    var manager=ColonyManager.Current;
                    manager.RefreshObjectives(true);
                    if(manager.Objectives.Count!=5 || manager.Requests!=0) throw new Exception("Objectives must exist without a model call");
                    manager.SetControl(ManagerControl.Manual);
                    if(manager.Automatic || manager.Busy || manager.Requests!=0) throw new Exception("Manual mode made an AI request");
                    manager.SetControl(ManagerControl.Automate);
                    if(!manager.Automatic) throw new Exception("Automate did not enable reviews");
                    manager.SetControl(ManagerControl.Manual);
                    var summary=ColonyTools.Snapshot(map);
                    var moved=(JObject)summary.DeepClone();
                    moved["colonists"][0]["x"]=0;
                    if(ColonyTools.Fingerprint(summary)!=ColonyTools.Fingerprint(moved)) throw new Exception("Movement triggers reviews");
                    var before=map.zoneManager.AllZones.OfType<Zone_Stockpile>().Count();
                    var spot=CellRect.CenteredOn(map.GetComponent<ColonyLocation>().Center,12).Cells.First(c=> c.x+4<map.Size.x && c.z+4<map.Size.z &&
                        CellRect.FromLimits(c.x,c.z,c.x+3,c.z+3).Cells.All(a=>a.Walkable(map) && a.GetEdifice(map)==null && map.zoneManager.ZoneAt(a)==null));
                    var call=new ToolCall { Id="test",Name="ensure_stockpile",Arguments=new JObject { ["x"]=spot.x,["z"]=spot.z,["width"]=4,["height"]=4 } };
                    var first=ColonyTools.Execute(map,call,p=>{});
                    var second=ColonyTools.Execute(map,call,p=>{});
                    int zones=map.zoneManager.AllZones.OfType<Zone_Stockpile>().Count();
                    if(zones!=Math.Max(before,1)) throw new Exception("Duplicate stockpile created");
                    if(map.mapPawns.FreeColonistsSpawned.Count!=pawns) throw new Exception("Pawn count changed");
                    if(Find.TickManager.CurTimeSpeed!=speed) throw new Exception("Game speed changed");
                    var shelterOptions=map.GetComponent<ShelterPlanner>().Find(3);
                    if(shelterOptions.Count==0) throw new Exception("No shelter options found on quicktest map");
                    var chosen=shelterOptions.FirstOrDefault(o=>o["rockCellsToMine"].Value<int>()==0);
                    if(chosen==null) throw new Exception("No surface shelter option for construction test");
                    var baseCell=map.GetComponent<ColonyLocation>().Center;
                    var woodItems=JObject.Parse(ColonyTools.Execute(map,new ToolCall{Name="find_items",Arguments=new JObject{["x"]=baseCell.x,["z"]=baseCell.z,["defName"]="WoodLog",["limit"]=40}},p=>{}));
                    if(((JArray)woodItems["items"]).Count>0) ColonyTools.Execute(map,new ToolCall{Name="allow_item_ids",Arguments=new JObject{["ids"]=new JArray(woodItems["items"].Select(i=>i["id"]))}},p=>{});
                    var shelterResult=map.GetComponent<ShelterPlanner>().Prepare(chosen["id"].Value<string>());
                    var orderCount=map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count;
                    map.GetComponent<ShelterPlanner>().Prepare(chosen["id"].Value<string>());
                    if(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count!=orderCount) throw new Exception("Repeated shelter order duplicated blueprints");
                    if(ColonyObserver.Observe(map).PendingBedSlots!=3) throw new Exception("Non-bed blueprints counted as sleeping places");
                    Log.Message("[RimBot Smoke] PASS: nearby shelter discovery, normal construction and repeat idempotence. "+shelterResult);
                    var crops=JArray.Parse(ColonyDevelopment.Execute(map,"list_crops",new JObject()));
                    if(crops.Count==0) throw new Exception("No available crop definitions");
                    JArray.Parse(ColonyDevelopment.Execute(map,"list_research",new JObject()));
                    JArray.Parse(ColonyDevelopment.Execute(map,"list_workstations",new JObject()));
                    Log.Message("[RimBot Smoke] PASS: crop, research and workstation discovery.");
                    var farmCell=CellRect.CenteredOn(baseCell,15).Cells.First(c=>c.InBounds(map) && c.x+3<map.Size.x && c.z+3<map.Size.z && CellRect.FromLimits(c.x,c.z,c.x+2,c.z+2).Cells.All(t=>!t.Fogged(map) && t.GetEdifice(map)==null && t.GetTerrain(map).fertility>=0.6f && map.zoneManager.ZoneAt(t)==null && !t.GetThingList(map).Any(v=>v.def.entityDefToBuild!=null)));
                    var farmArgs=new JObject{["x"]=farmCell.x,["z"]=farmCell.z,["width"]=3,["height"]=3,["crop"]="Plant_Rice"};
                    ColonyDevelopment.Execute(map,"ensure_growing_zone",farmArgs);
                    int farms=map.zoneManager.AllZones.OfType<Zone_Growing>().Count();
                    ColonyDevelopment.Execute(map,"ensure_growing_zone",farmArgs);
                    if(map.zoneManager.AllZones.OfType<Zone_Growing>().Count()!=farms) throw new Exception("Growing zone duplicated");
                    // Test fixture only: a completed butcher table isolates bill behavior from construction time.
                    var benchDef=DefDatabase<ThingDef>.GetNamed("TableButcher");
                    var benchCell=CellRect.CenteredOn(baseCell,15).Cells.First(c=>GenConstruct.CanPlaceBlueprintAt(benchDef,c,Rot4.North,map).Accepted);
                    var bench=(Building_WorkTable)GenSpawn.Spawn(ThingMaker.MakeThing(benchDef,ThingDefOf.WoodLog),benchCell,map);
                    bench.SetFaction(Faction.OfPlayer);
                    var butcherArgs=new JObject{["workstationId"]=bench.thingIDNumber,["recipe"]="ButcherCorpseFlesh",["mode"]="forever"};
                    ColonyDevelopment.Execute(map,"set_production_bill",butcherArgs);
                    ColonyDevelopment.Execute(map,"set_production_bill",butcherArgs);
                    if(bench.BillStack.Bills.Count!=1 || ((Bill_Production)bench.BillStack.Bills[0]).repeatMode!=BillRepeatModeDefOf.Forever) throw new Exception("Butcher bill invalid or duplicated");
                    Log.Message("[RimBot Smoke] PASS: crop zone and variable-product butcher bill, both idempotent; only real beds counted.");

                    Log.Message("[RimBot Smoke] PASS: "+pawns+" pawns preserved, one shared stockpile after repeated calls, unchanged movement fingerprint, normal speed, mode-controlled reviews.");
                    Log.Message("[RimBot Smoke] "+first+" / "+second);
                    Log.Message("[RimBot Smoke] PASS: five locally measured objectives, zero model calls in Manual mode, Automate enabled reviews and Manual disabled them.");
                    ColonyManager.Current.Record("Self-test passed: "+pawns+" pawns preserved; repeated stockpile orders did not duplicate the zone.");
                    Find.MainTabsRoot.SetCurrentTab(DefDatabase<MainButtonDef>.GetNamed("RimBotColonyManager"));
                    doneAt=Time.realtimeSinceStartup+3;
                } catch(Exception ex) { Log.Warning("[RimBot Smoke] FAIL: "+ex); stage=3; doneAt=Time.realtimeSinceStartup+2; }
            }
            else if(stage==1 && Time.realtimeSinceStartup>doneAt)
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(GenFilePaths.SaveDataFolderPath,"manager-smoke.png"));
                stage=2; doneAt=Time.realtimeSinceStartup+3;
            }
            else if(stage>=2 && Time.realtimeSinceStartup>doneAt) { stage=4; Application.Quit(); }
        }
    }
}
