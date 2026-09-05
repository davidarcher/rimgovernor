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
            { ["name"]="zones_stockpile_designate",["arguments"]=arguments } }) } }),
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
                { ContentPart.FromToolResult("call_1","zones_stockpile_designate",true,"Existing stockpile; no duplicate created.") }) };
            var body=LocalModel.BuildRequest(conversation,ToolCatalog.Definitions(),"test-model",768);
            Check(body["messages"][2]["role"].Value<string>()=="tool","Tool result role wrong");
            Check(body["messages"][2]["tool_call_id"].Value<string>()=="call_1","Tool ID not preserved");
            Check(body["messages"][1]["tool_calls"][0]["function"]["arguments"].Type==JTokenType.String,"Arguments must be serialized JSON");
            Check(body["tools"].Count()==35,"Unexpected tool surface");
            Check(DailyPlanning.Due(-1,0,true,false,false),"Missing daily plan should run");
            Check(!DailyPlanning.Due(0,1000,false,false,false),"Daily plan reruns every review");
            Check(DailyPlanning.Due(0,60000,false,false,false),"Next day plan missing");
            Check(DailyPlanning.Due(0,100,false,true,false),"Player direction should replan promptly");
            Check(!DailyPlanning.Due(0,100,false,false,true),"Repeated blockers cause planning spam");
            Check(DailyPlanning.Due(0,12000,false,false,true),"Persistent blocker never replans");
            Check(DailyPlanning.Parse(new JObject{["plan"]=" Plant rice; finish existing walls. "})=="Plant rice; finish existing walls.","Daily plan parse failed");
            var feed=new NotificationFeed();
            var initial=new JObject{["alerts"]=new JArray(),["letters"]=new JArray(),["messages"]=new JArray()};
            Check(!feed.Observe(initial),"Initial notification snapshot should establish baseline");
            initial["messages"]=new JArray(new JObject{["text"]="Construction botched",["tick"]=10});
            Check(feed.Observe(initial) && feed.Revision==1,"New notification did not wake review");
            Check(!feed.Observe(initial),"Same notification wakes repeated reviews");
            initial["alerts"]=new JArray(new JObject{["label"]="Need food"});
            Check(feed.Observe(initial),"New alert ignored");
            initial["alerts"]=new JArray();
            Check(feed.Observe(initial) && feed.LatestBatch.Contains("clearedAlert"),"Cleared alert ignored");
            var session=new ToolSession();
            Check(session.Definitions().Count==4,"New reviews should not load the entire tool catalog");
            Check(ToolSession.Discover("notifications").Any(t=>t["name"].Value<string>()=="notifications_read"),"Notification tool undiscoverable");
            session.Enable(new JArray("pawns_list","pawns_inspect","items_list","orders_allow","buildings_list","map_inspect"));
            Check(session.Available("pawns_list"),"Enabled tool missing");
            session.Enable(new JArray("plants_sowable"));
            Check(!session.Available("pawns_list") && session.Available("plants_sowable") && session.Definitions().Count==10,"Schema window failed to evict oldest tool");
            bool badToolRejected=false; try { session.Enable(new JArray("invented_tool")); } catch(ArgumentException) { badToolRejected=true; }
            Check(badToolRejected && session.Available("plants_sowable"),"Invalid enable must preserve current tools");
            Check(LocalModel.BuildRequest(conversation,new ToolSession().Definitions(),"test",0).ToString().Length < body.ToString().Length/2,"Schema discovery failed to reduce request size");
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
            var summary=ActivitySummary.Tool(new RimBot.Tools.ToolCall { Name="map_inspect",Arguments=new JObject { ["x"]=1,["z"]=2 } },"[{walkable:true,roofed:false}]",true);
            Check(summary.Contains("1 cells")&&!summary.Contains("walkable\""),"Area activity should be human readable");
            Check(ActivitySummary.Model("{\"summary\":\"Wait for construction\"}")=="Wait for construction","Structured response should show its summary");
            Check(ActivitySummary.Model("{\"internal\":true}")=="","Internal JSON should stay out of activity");
            Check(ActivitySummary.Short("Plant rice.\nThen cook.",180)=="Plant rice. Then cook.","Notes should collapse whitespace");
            Check(ActivitySummary.Short(new string('x',300),180).Length<=180,"Notes exceed display budget");
            Check(LocalModel.BuildRequest(conversation,ToolCatalog.Definitions(),"test",0)["max_tokens"]==null,"Uncapped local request still sends a token cap");
            Check(LocalModel.BuildRequest(conversation,ToolCatalog.Definitions(),"test",768)["max_tokens"].Value<int>()==768,"Explicit fixture token budget lost");
            Check(LocalModel.BuildRequest(conversation,StrategicPlan.Tools(),"test",0,"medium")["reasoning_effort"].Value<string>()=="medium","Strategic reasoning request missing");
            Check(LocalModel.BuildRequest(conversation,ToolCatalog.Definitions(),"test",0,"none")["reasoning_effort"].Value<string>()=="none","Daily request did not disable reasoning");
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
                ["dependsOn"]=new JArray(),["requiredTools"]=new JArray("architect_build"),["steps"]=new JArray("Build a shared room"),
                ["completeWhen"]=new JArray(new JObject { ["metric"]="sheltered_slots",["op"]="atLeast",["value"]=3 }),["state"]="Complete" };
            var planJson=new JObject { ["season"]="Secure shelter and food",["year"]="Reliable production",["threeYears"]="A resilient settlement",["projects"]=new JArray(project) };
            var strategy=StrategicPlan.Parse(planJson.ToString());
            var measured=new Dictionary<string,double>{{"sheltered_slots",0},{"building:Bed",0},{"pending:Bed",1}};
            var permitted=new HashSet<string>{"architect_build"};
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
            "Five colonists; stockpileCount=0; no pending orders. map_inspect is required before creating it. Map is 250x250.") };
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
                if(call.Name=="map_inspect") { inspected=true; result="All 16 cells in the requested 4x4 area are walkable, unzoned, and empty."; }
                else if(call.Name=="zones_stockpile_designate") {
                    Check(inspected,"Model attempted construction before inspection");
                    Check(call.Arguments["x"].Value<int>()==100 && call.Arguments["z"].Value<int>()==100 &&
                        call.Arguments["width"].Value<int>()==4 && call.Arguments["height"].Value<int>()==4,"Wrong stockpile location/size");
                    created++; result="Shared stockpile created. Current stockpileCount=1. Wait for normal hauling.";
                }
                else if(call.Name=="manager_save_plan") { plan=true; result="Plan saved."; }
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
