using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Colony;
using RimBot.Models;

internal static class Program
{
    private static int assertions;
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); assertions++; }
    private static string Response(string arguments,string finish="tool_calls") => new JObject {
        ["choices"]=new JArray(new JObject { ["finish_reason"]=finish,["message"]=new JObject { ["role"]="assistant",["content"]="",
            ["tool_calls"]=new JArray(new JObject { ["id"]="call_1",["type"]="function",["function"]=new JObject
            { ["name"]="ensure_stockpile",["arguments"]=arguments } }) } }),
        ["usage"]=new JObject { ["prompt_tokens"]=100,["completion_tokens"]=20 } }.ToString();
    private static int Main(string[] args)
    {
        try {
            var budget=new ReviewBudget();
            Check(budget.CanReview(0,60),"First review blocked");
            budget.StartReview(0);
            Check(!budget.CanReview(59,60),"Cooldown bypass");
            Check(budget.CanReview(60,60),"Cooldown never ends");
            Check(budget.TryRequest(0,2) && budget.TryRequest(1,2),"Budget rejects allowed requests");
            Check(!budget.TryRequest(2,2),"Budget permits excess request");
            Check(budget.Used(3600)==1 && budget.TryRequest(3600,2),"Rolling budget incorrect");
            Check(!budget.TryRequest(3600,1),"Lowered cap bypassed");
            var parsed=LocalModel.ParseResponse(Response("{\"x\":10,\"z\":12,\"width\":4,\"height\":4}"));
            Check(parsed.Success && parsed.ToolCalls.Count==1 && parsed.ToolCalls[0].Arguments["x"].Value<int>()==10,"Tool parse failed");
            Check(parsed.InputTokens==100 && parsed.OutputTokens==20 && parsed.TokensUsed==120,"Usage lost");
            Check(!LocalModel.ParseResponse(Response("not json")).Success,"Malformed arguments accepted");
            Check(!LocalModel.ParseResponse(Response("{}","length")).Success,"Truncated actions accepted");
            Check(!LocalModel.ParseResponse("{choices:[]}").Success,"Empty response accepted");
            var duplicate=JObject.Parse(Response("{}"));
            var calls=(JArray)duplicate["choices"][0]["message"]["tool_calls"]; calls.Add(calls[0].DeepClone());
            Check(!LocalModel.ParseResponse(duplicate.ToString()).Success,"Duplicate IDs accepted");
            var conversation=new List<ChatMessage> { new ChatMessage("system",ManagerPrompt.Text),
                new ChatMessage("assistant",parsed.AssistantParts),new ChatMessage("user",new List<ContentPart>
                { ContentPart.FromToolResult("call_1","ensure_stockpile",true,"Existing stockpile; no duplicate created.") }) };
            var body=LocalModel.BuildRequest(conversation,ToolCatalog.Definitions(),"test-model",768);
            Check(body["messages"][2]["role"].Value<string>()=="tool","Tool result role wrong");
            Check(body["messages"][2]["tool_call_id"].Value<string>()=="call_1","Tool ID not preserved");
            Check(body["messages"][1]["tool_calls"][0]["function"]["arguments"].Type==JTokenType.String,"Arguments must be serialized JSON");
            Check(body["tools"].Count()==26,"Unexpected tool surface");
            Check(ManagerPrompt.Text.Split(' ').Length<110,"Prompt grew beyond local-model budget");
            foreach(var t in ToolCatalog.Definitions()) Check(JObject.Parse(t.ParametersJson)["additionalProperties"].Value<bool>()==false,"Unbounded schema");
            bool rejected=false; try { new LocalModel("file:///tmp/model"); } catch(ArgumentException) { rejected=true; }
            Check(rejected,"Non-HTTP endpoint accepted");
            Check(new ReviewBudget().TryRequest(0,0),"Unlimited local budget rejected request");
            var facts=new ColonyFacts { Colonists=3,DailyNutrition=4.8f,Nutrition=12,Builders=1,Doctors=1,Cooks=1 };
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="food").State==ObjectiveState.Satisfied,"Food target calculation");
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="shelter").State==ObjectiveState.Needed,"Missing beds should be needed");
            facts.PendingBedSlots=3;facts.Blueprints=3;
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="shelter").State==ObjectiveState.Ordered,"Queued beds should avoid duplication");
            facts.BedFrames=1;facts.ActiveConstruction=1;
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="shelter").State==ObjectiveState.InProgress,"Building bed should be in progress");
            facts.ConstructionStalled=true;
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="construction").State==ObjectiveState.Blocked,"Stalled orders should be blocked");
            facts.ShelteredSlots=3;
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="shelter").State==ObjectiveState.Satisfied,"Finished shelter should satisfy goal");
            facts.Nutrition=1;facts.CookingOrders=1;
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="food").State==ObjectiveState.Ordered,"Food bill should be recognized");
            facts.Cooks=0;
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="food").State==ObjectiveState.Blocked,"Food bill without cook should be blocked");
            facts.Patients=1;facts.Bleeding=1;
            Check(ColonyObjectives.Evaluate(facts).First().Id=="health","Urgent care must precede buildings");
            facts.Hostiles=1;
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="safety").State==ObjectiveState.Blocked,"Unsupported combat must request player action");
            var key=ColonyObjectives.DecisionKey(ColonyObjectives.Evaluate(facts));facts.Nutrition+=0.01f;
            Check(ColonyObjectives.DecisionKey(ColonyObjectives.Evaluate(facts))==key,"Tiny food changes should not alter decision key");
            var summary=ActivitySummary.Tool(new RimBot.Tools.ToolCall { Name="inspect_area",Arguments=new JObject { ["x"]=1,["z"]=2 } },"[{walkable:true,roofed:false}]",true);
            Check(summary.Contains("1 cells")&&!summary.Contains("walkable\""),"Area activity should be human readable");
            Check(ActivitySummary.Model("{\"summary\":\"Wait for construction\"}")=="Wait for construction","Structured response should show its summary");
            for(int n=0;n<=4;n++) {
                var room=RoomLayout.Create(n);
                Check(room.Count(p=>p.DefName=="Door")==1 && room.Count(p=>p.DefName=="Wall")==23,"Room perimeter must close with one door");
                Check(room.Count(p=>p.DefName=="Bed")==n,"Room bed count changed");
                Check(room.Where(p=>p.DefName=="Bed").All(p=>p.X!=3 && p.Z>1 && p.Z+1<6),"Bed obstructs aisle or perimeter");
                var occupied=new HashSet<string>();
                foreach(var part in room) {
                    Check(occupied.Add(part.X+","+part.Z),"Room orders overlap");
                    if(part.DefName=="Bed") Check(occupied.Add(part.X+","+(part.Z+1)),"Bed footprint overlaps another order");
                }
                var reachable=new HashSet<string>(); var queue=new Queue<Tuple<int,int>>(); queue.Enqueue(Tuple.Create(3,1));
                while(queue.Count>0) {
                    var c=queue.Dequeue(); string id=c.Item1+","+c.Item2;
                    if(c.Item1<1 || c.Item1>5 || c.Item2<1 || c.Item2>5 || occupied.Contains(id) || !reachable.Add(id)) continue;
                    queue.Enqueue(Tuple.Create(c.Item1+1,c.Item2)); queue.Enqueue(Tuple.Create(c.Item1-1,c.Item2));
                    queue.Enqueue(Tuple.Create(c.Item1,c.Item2+1)); queue.Enqueue(Tuple.Create(c.Item1,c.Item2-1));
                }
                Check(room.Where(p=>p.DefName=="Bed").All(p=>reachable.Contains(p.X+","+(p.Z-1))),"Bed cannot be reached from doorway");
            }
            bool roomRejected=false; try { RoomLayout.Create(5); } catch(ArgumentException) { roomRejected=true; }
            Check(roomRejected,"Overcrowded room accepted");
            Check(LocalModel.BuildRequest(conversation,ToolCatalog.Definitions(),"test",0)["max_tokens"]==null,"Uncapped local request still sends a token cap");
            Check(LocalModel.BuildRequest(conversation,ToolCatalog.Definitions(),"test",768)["max_tokens"].Value<int>()==768,"Explicit fixture token budget lost");
            Check(LocalModel.BuildRequest(conversation,StrategicPlan.Tools(),"test",0,"medium")["reasoning_effort"].Value<string>()=="medium","Strategic reasoning request missing");
            Check(LocalModel.BuildRequest(conversation,ToolCatalog.Definitions(),"test",0,"none")["reasoning_effort"].Value<string>()=="none","Daily request did not disable reasoning");
            Check(!PlacementRange.Near(125,125,10,10,4,4),"Corner stockpile accepted");
            Check(!PlacementRange.Near(125,125,50,50,7,7),"Distant room accepted");
            Check(PlacementRange.Near(125,125,130,130,7,7),"Nearby room rejected");
            Check(PlacementRange.Near(50,50,48,48,4,4),"Explicitly relocated base ignored");
            var items=new[] {
                new ItemRecord{Id=3,DefName="WoodLog",Category="material",Forbidden=true,Count=40,X=10,Z=0},
                new ItemRecord{Id=2,DefName="WoodLog",Category="material",Forbidden=false,Count=20,X=1,Z=0},
                new ItemRecord{Id=1,DefName="WoodLog",Category="material",Forbidden=true,Count=60,X=3,Z=0},
                new ItemRecord{Id=4,DefName="Steel",Category="material",Forbidden=true,Count=70,X=0,Z=0}
            };
            var itemResult=ItemQuery.Find(items,"woodlog","all","yes",0,0,0,1);
            Check(itemResult["totalStacks"].Value<int>()==2 && itemResult["totalItems"].Value<int>()==100,"Item filtering counted unrelated or allowed stacks");
            Check(itemResult["items"][0]["id"].Value<int>()==1 && itemResult["nextOffset"].Value<int>()==1,"Items aren't nearest first or pagination lost");
            Check(ItemQuery.Find(items,"WoodLog","all","yes",0,0,1,1)["items"][0]["id"].Value<int>()==3,"Second item page incorrect");
            Check(ItemQuery.Find(items,null,"all","any",10,0,0,40)["items"][0]["id"].Value<int>()==3,"Query origin ignored");
            Check(((JArray)ItemQuery.Find(items,null,"food","any",0,0,0,40)["items"]).Count==0,"Category filtering ignored");
            var project=new JObject { ["id"]="shelter",["title"]="House the colony",["purpose"]="Safe sleeping",["priority"]=1,
                ["dependsOn"]=new JArray(),["requiredTools"]=new JArray("build_room"),["steps"]=new JArray("Build a shared room"),
                ["completeWhen"]=new JArray(new JObject { ["metric"]="sheltered_slots",["op"]="atLeast",["value"]=3 }),["state"]="Complete" };
            var planJson=new JObject { ["season"]="Secure shelter and food",["year"]="Reliable production",["threeYears"]="A resilient settlement",["projects"]=new JArray(project) };
            var strategy=StrategicPlan.Parse(planJson.ToString());
            var measured=new Dictionary<string,double>{{"sheltered_slots",0},{"building:Bed",0},{"pending:Bed",1}};
            var permitted=new HashSet<string>{"build_room"};
            Check(strategy.State(strategy.Projects[0],measured,permitted)=="Ready","Model-claimed completion overrode game facts");
            Check(strategy.Projects[0]["state"]==null,"Untrusted project state persisted");
            measured["sheltered_slots"]=3;
            Check(strategy.State(strategy.Projects[0],measured,permitted)=="Complete","Measured project completion missed");
            measured["sheltered_slots"]=1;
            Check(strategy.State(strategy.Projects[0],measured,permitted)=="Ready","Lost shelter did not reopen project");
            Check(strategy.State(strategy.Projects[0],measured,new HashSet<string>()).StartsWith("Blocked:"),"Unavailable tools not surfaced");
            Check(StrategicPlan.Parse(strategy.Serialize()).Serialize()==strategy.Serialize(),"Strategy round trip lost state");
            var dependant=(JObject)project.DeepClone(); dependant["id"]="next"; dependant["dependsOn"]=new JArray("shelter");
            dependant["completeWhen"]=new JArray(new JObject{["metric"]="building:Bed",["op"]="atLeast",["value"]=4});
            planJson["projects"]=new JArray(project.DeepClone(),dependant);
            strategy=StrategicPlan.Parse(planJson.ToString());
            Check(strategy.State(strategy.Projects[1],measured,permitted)=="Waiting for shelter","Unmet dependency ignored");
            Check(strategy.Tactical(measured,permitted).Count==1,"Blocked dependency leaked into daily execution");
            measured["sheltered_slots"]=3;
            Check(strategy.State(strategy.Projects[1],measured,permitted)=="In progress","Actual pending building work not recognized");
            measured.Remove("building:Bed");
            Check(strategy.State(strategy.Projects[1],measured,permitted).StartsWith("Blocked: cannot verify"),"Unknown metric treated as completion");
            bool invalidPlan=false;
            var cycle=(JObject)planJson.DeepClone(); cycle["projects"][0]["dependsOn"]=new JArray("next");
            try { StrategicPlan.Parse(cycle.ToString()); } catch(ArgumentException) { invalidPlan=true; }
            Check(invalidPlan,"Cyclic strategic dependencies accepted");
            invalidPlan=false; var missing=(JObject)planJson.DeepClone(); missing["projects"][1]["dependsOn"]=new JArray("missing");
            try { StrategicPlan.Parse(missing.ToString()); } catch(ArgumentException) { invalidPlan=true; }
            Check(invalidPlan,"Unknown dependency accepted");
            invalidPlan=false; var duplicatePlan=(JObject)planJson.DeepClone(); duplicatePlan["projects"][1]["id"]="shelter";
            try { StrategicPlan.Parse(duplicatePlan.ToString()); } catch(ArgumentException) { invalidPlan=true; }
            Check(invalidPlan,"Duplicate project IDs accepted");
            Check(StrategicPlan.Tools().Count==1 && StrategicPlan.Tools()[0].Name=="save_strategy","Strategic planner has world mutation tools");
            Check(JObject.Parse(StrategicPlan.Tools()[0].ParametersJson)["additionalProperties"].Value<bool>()==false,"Strategy schema malformed");
            Check(StrategySchedule.Due(false,-1,1,-1,0,"","goal",0,3,0,2,0,0)!=null,"Initial strategy never runs");
            Check(StrategySchedule.Due(true,1,1,0,60000,"goal","goal",3,3,2,2,3,3)==null,"Unchanged colony causes strategic spam");
            Check(StrategySchedule.Due(true,1,2,0,60000,"goal","goal",3,3,2,2,3,3)=="New quadrum","Seasonal planning never runs");
            Check(StrategySchedule.Due(true,1,1,0,1,"goal","new goal",3,3,2,2,3,3)=="Player direction changed","New direction ignored");
            Check(StrategySchedule.Due(true,1,1,0,1,"goal","goal",3,4,2,2,3,3)==null,"Population change bypasses strategic debounce");
            Check(StrategySchedule.Due(true,1,1,0,60000,"goal","goal",3,4,2,2,3,3)=="Colony population changed","Population disruption missed");
            Check(StrategySchedule.Due(true,1,1,0,60000,"goal","goal",3,3,2,0.1,3,3)=="Food reserves collapsed","Food shock missed");
            Check(StrategySchedule.Due(true,1,1,0,60000,"goal","goal",3,3,2,2,3,2)=="Shelter was lost","Shelter destruction missed");

            Func<int,int,ShelterCell[,]> floor=(w,h)=> {
                var cells=new ShelterCell[w,h];
                for(int x=0;x<w;x++) for(int z=0;z<h;z++) cells[x,z]=new ShelterCell{Kind=ShelterCellKind.Floor};
                return cells;
            };
            var ruin=floor(5,5);
            for(int x=0;x<5;x++) for(int z=0;z<5;z++) if(x==0 || z==0 || x==4 || z==4) ruin[x,z].Kind=ShelterCellKind.Wall;
            ruin[2,0].Kind=ShelterCellKind.Door;
            var reused=ShelterGeometry.Evaluate(ruin,3,(x,z)=>z==0);
            Check(reused!=null && reused.Walls.Count==0 && !reused.NewDoor && reused.Beds.Count==3,"Existing ruin not reused or sleeping capacity wrong");
            Check(reused.WoodEstimate==0 && reused.Reused==16,"Reused shelter charged for existing walls/door");
            ruin[4,2].Kind=ShelterCellKind.Floor;
            Check(ShelterGeometry.Evaluate(ruin,3,(x,z)=>z==0).Walls.Count==1,"Ruin gap not filled precisely");
            var leanTo=floor(5,5);
            for(int z=0;z<5;z++) leanTo[0,z].Kind=ShelterCellKind.Rock;
            var leaned=ShelterGeometry.Evaluate(leanTo,3,(x,z)=>z==0);
            Check(leaned!=null && leaned.Reused==5 && leaned.Mine.Count==0 && leaned.Walls.Count==10,"Mountain boundary should be retained, not mined/rebuilt");
            var cave=floor(5,5);
            for(int x=0;x<5;x++) for(int z=0;z<5;z++) cave[x,z].Kind=ShelterCellKind.Rock;
            var excavated=ShelterGeometry.Evaluate(cave,3,(x,z)=>z==0 && x==2);
            Check(excavated!=null && excavated.Mine.Count==10 && excavated.Walls.Count==0 && excavated.NewDoor,"Excavation should remove only interior plus doorway");
            Check(excavated.Mine.All(p=>p.X>0 && p.X<4 && p.Z>0 && p.Z<4 || p.X==2 && p.Z==0),"Excavation removed retained boundary support");
            cave[2,2].Kind=ShelterCellKind.Unknown;
            Check(ShelterGeometry.Evaluate(cave,3,(x,z)=>true)==null,"Planner inspected unknown mountain interior");
            cave[2,2].Kind=ShelterCellKind.Blocked;
            Check(ShelterGeometry.Evaluate(cave,3,(x,z)=>true)==null,"Planner proposed demolishing an obstacle");
            Check(ShelterGeometry.Evaluate(floor(5,5),3,(x,z)=>false)==null,"Shelter without accessible entrance accepted");
            Check(ShelterGeometry.Evaluate(floor(5,5),6,(x,z)=>true)==null,"Overcrowded shelter accepted");
            var furnished=floor(5,5); furnished[1,2].BedSlots=3; furnished[1,2].Occupied=true;
            Check(ShelterGeometry.Evaluate(furnished,3,(x,z)=>true).Beds.Count==0,"Existing sleeping capacity duplicated");
            facts.Hostiles=0;facts.Unarmed=3;
            Check(ColonyObjectives.Evaluate(facts).Single(o=>o.Id=="safety").State==ObjectiveState.Needed,"Unarmed colony incorrectly considered equipped");
            Check(StrategySchedule.Due(true,1,1,0,60000,"goal","goal",3,3,2,2,3,3,true)=="Seasonal projects completed","Finished projects did not trigger further development");

            Console.WriteLine("PASS: "+assertions+" protocol/budget checks. Prompt words: "+ManagerPrompt.Text.Split(' ').Length);
            if(args.Length>0 && args[0]=="--live") Live(args.Length>1?args[1]:"qwen/qwen3.5-9b");
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Live(string model)
    {
        // Synthetic map fixture: NO game calls and NO real colony is modified.
        var provider=new LocalModel("http://localhost:1234/v1");
        var messages=new List<ChatMessage> { new ChatMessage("system",ManagerPrompt.Text),new ChatMessage("user",
            "Objective: create ONE shared 4x4 stockpile at x=100,z=100, then save a plan to wait for hauling. " +
            "Five colonists; stockpileCount=0; no pending orders. inspect_area is required before creating it. Map is 250x250.") };
        int created=0,totalTokens=0; bool inspected=false,plan=false;
        var watch=Stopwatch.StartNew();
        for(int round=0;round<3;round++) {
            var response=provider.SendToolRequest(messages,ToolCatalog.Definitions(),model,"",768,ThinkingLevel.None).GetAwaiter().GetResult();
            Check(response.Success,"Live provider error: "+response.ErrorMessage);
            totalTokens+=response.TokensUsed;
            Console.WriteLine("Live round "+(round+1)+": input="+response.InputTokens+", output="+response.OutputTokens);
            messages.Add(new ChatMessage("assistant",response.AssistantParts));
            if(response.ToolCalls.Count==0) break;
            var results=new List<ContentPart>();
            foreach(var call in response.ToolCalls) {
                string result;
                if(call.Name=="inspect_area") { inspected=true; result="All 16 cells in the requested 4x4 area are walkable, unzoned, and empty."; }
                else if(call.Name=="ensure_stockpile") {
                    Check(inspected,"Model attempted construction before inspection");
                    Check(call.Arguments["x"].Value<int>()==100 && call.Arguments["z"].Value<int>()==100 &&
                        call.Arguments["width"].Value<int>()==4 && call.Arguments["height"].Value<int>()==4,"Wrong stockpile location/size");
                    created++; result="Shared stockpile created. Current stockpileCount=1. Wait for normal hauling.";
                }
                else if(call.Name=="set_plan") { plan=true; result="Plan saved."; }
                else throw new Exception("Unexpected action in stockpile fixture: "+call.Name);
                Console.WriteLine("  "+call.Name+" "+call.Arguments.ToString(Formatting.None));
                results.Add(ContentPart.FromToolResult(call.Id,call.Name,true,result));
            }
            messages.Add(new ChatMessage("user",results));
        }
        Check(created==1,"Expected exactly one shared stockpile request, got "+created);
        Check(plan,"No saved plan");
        Console.WriteLine("PASS: live three-request colony review; one stockpile, saved plan; "+totalTokens+" total tokens in "+watch.Elapsed.TotalSeconds.ToString("F1")+"s.");
    }
}
