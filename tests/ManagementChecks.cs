using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimBot.Colony;
using RimBot.Models;
using RimBot.Tools;

internal static class ManagementChecks
{
    private sealed class FakeModel : ILanguageModel
    {
        public readonly List<string> Roles=new List<string>();
        public Func<string,JObject,ModelResponse> Respond;
        public LLMProviderType ProviderType=>LLMProviderType.Local;
        public bool SupportsImageOutput=>false;
        public Task<ModelResponse> SendChatRequest(List<ChatMessage> m,string model,string key,int max)=>throw new Exception("Unexpected chat transport");
        public Task<ModelResponse> SendImageRequest(List<ChatMessage> m,string model,string key,int max)=>throw new Exception("Unexpected image transport");
        public string[] GetAvailableModels()=>new[]{"fixture"};
        public Task<ModelResponse> SendToolRequest(List<ChatMessage> messages,List<ToolDefinition> tools,string model,string key,int maxTokens,ThinkingLevel thinking)
        {
            string role=tools.Any(t=>t.Name=="submit_decision")?"administrator":JObject.Parse(tools.Single(t=>t.Name=="submit_proposal").ParametersJson)["properties"]["manager"]["enum"][0].Value<string>();
            Roles.Add(role);
            var context=JObject.Parse(messages.First(m=>m.Role=="user").Content);
            return Task.FromResult(Respond(role,context));
        }
    }
    private static ModelResponse Reply(string name,JObject args)=>new ModelResponse{Success=true,StopReason=StopReason.ToolUse,ToolCalls=new List<ToolCall>{new ToolCall{Id="fixture",Name=name,Arguments=args}}};
    private static JObject Proposal(string role,string action=null,JObject args=null,int priority=50)=>new JObject{
        ["manager"]=role,["priority"]=priority,["summary"]=role+" fixture",["risks"]=new JArray(),
        ["requests"]=action==null?new JArray():new JArray(new JObject{["action"]=action,["args"]=args}),
        ["labor_requests"]=new JArray(),["resource_requests"]=new JObject(),["review_after_hours"]=6};
    private static JObject Decision(JObject context,Func<JObject,string> status=null)=>new JObject{
        ["summary"]="Fixture arbitration",["player_response"]="",["override_commitments"]=new JArray(),["review_after_hours"]=6,
        ["proposals"]=new JArray(((JArray)context["proposals"]).OfType<JObject>().Select(p=>new JObject{["proposalId"]=p["id"],["status"]=status==null?"accept":status(p),["reason"]="Fixture resource and priority decision"}))};
    private static ColonyBlackboard Board()=>new ColonyBlackboard{resources=new JObject{["pawn_hours"]=8,["WoodLog"]=100}};
    private static AdministrationDecision Run(FakeModel model,ColonyBlackboard board,List<JObject> log,params ManagementRole[] roles)
        =>new ManagementCoordinator(model,"fixture","",0,c=>throw new Exception("Unexpected read dispatcher invocation: "+c.Name),()=>Task.FromResult(true),e=>log.Add(e)).Run(board,roles).GetAwaiter().GetResult();
    private static bool Throws(Action action) { try { action(); return false; } catch(ArgumentException) { return true; } }
    public static void Run(Action<bool,string> check)
    {
        // A specialist cannot smuggle a world mutation through the read-tool channel.
        int reads=0; var log=new List<JObject>();
        var model=new FakeModel{Respond=(role,ctx)=>role=="administrator"?Reply("submit_decision",Decision(ctx)):Reply("orders_allow",JObject.Parse("{ids:[123]}"))};
        var coordinator=new ManagementCoordinator(model,"fixture","",0,c=>{reads++;return Task.FromResult("{}");},()=>Task.FromResult(true),e=>log.Add(e));
        var decision=coordinator.Run(Board(),new[]{ManagementRole.Survival}).GetAwaiter().GetResult();
        check(reads==0&&decision.AcceptedActions.Count==0,"Specialist direct mutation reached the dispatcher");
        check(log.Any(e=>e.Value<string>("event")=="validation_failure"),"Rejected specialist action was not logged");

        // Invalid schema and one failed model invocation must not discard other domains.
        foreach(bool providerFailure in new[]{false,true}) {
            log=new List<JObject>();
            model=new FakeModel{Respond=(role,ctx)=>{
                if(role=="administrator") return Reply("submit_decision",Decision(ctx));
                if(role=="survival") { if(providerFailure) throw new InvalidOperationException("Fixture provider failure"); var bad=Proposal(role); bad["priority"]="urgent"; return Reply("submit_proposal",bad); }
                return Reply("submit_proposal",Proposal(role,"research_select",JObject.Parse("{research:'FixtureResearch'}")));
            }};
            decision=Run(model,Board(),log,ManagementRole.Survival,ManagementRole.Development);
            check(decision.Accepted.Count==1&&decision.Accepted[0].manager=="development","One failed specialist aborted remaining managers");
            check(log.Any(e=>e.Value<string>("event")=="validation_failure"&&e.Value<string>("manager")=="Survival"),"Invalid specialist output lost failure telemetry");
        }

        // Administrator rejection protects scarce labor; blindly accepting both is invalid.
        var board=Board();
        var survival=Proposal("survival","orders_allow",JObject.Parse("{ids:[123]}"),100);
        survival["labor_requests"]=JArray.Parse("[{workType:'Growing',hours:8}]");
        var infrastructure=Proposal("infrastructure","architect_build",JObject.Parse("{defName:'Wall',material:'WoodLog',x:10,z:10,rotation:0}"),30);
        infrastructure["labor_requests"]=JArray.Parse("[{workType:'Construction',hours:4}]");
        var proposals=new List<ManagerProposal>{ManagementSchema.ParseProposal(survival,ManagementRole.Survival,board),ManagementSchema.ParseProposal(infrastructure,ManagementRole.Infrastructure,board)};
        check(Throws(()=>ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board)),"Administrator overallocated labor across competing proposals");
        decision=ManagementSchema.ParseDecision(Decision(board.Administration(proposals),p=>p.Value<string>("manager")=="survival"?"accept":"reject"),proposals,board);
        check(decision.AcceptedActions.Count==1&&decision.AcceptedActions[0].action=="orders_allow","Infrastructure rejection did not remove its construction order");

        // Workforce sees accepted high-priority grants only and must cite one.
        log=new List<JObject>(); bool sawGrant=false;
        model=new FakeModel{Respond=(role,ctx)=>{
            if(role=="administrator") return Reply("submit_decision",Decision(ctx,p=>p.Value<string>("manager")=="infrastructure"?"defer":"accept"));
            if(role=="survival") return Reply("submit_proposal",survival);
            if(role=="infrastructure") return Reply("submit_proposal",infrastructure);
            var grants=(JArray)ctx["acceptedLabor"]; sawGrant=grants.Count==1&&grants[0].Value<int>("priority")==100;
            var workforce=Proposal(role,"work_set_priority",JObject.Parse("{pawnId:123,workType:'Growing',priority:1}"),100);
            workforce["requests"][0]["requestId"]=grants[0]["proposalId"];
            return Reply("submit_proposal",workforce);
        }};
        decision=Run(model,Board(),log,ManagementRole.Survival,ManagementRole.Infrastructure,ManagementRole.Workforce);
        check(sawGrant&&decision.AcceptedActions.Any(a=>a.action=="work_set_priority"),"Workforce did not translate the accepted urgent grant");
        var deniedWork=Proposal("workforce","work_set_priority",JObject.Parse("{pawnId:123,workType:'Construction',priority:1}"));
        deniedWork["requests"][0]["requestId"]="unaccepted";
        check(Throws(()=>ManagementSchema.ParseProposal(deniedWork,ManagementRole.Workforce,Board())),"Workforce accepted an ungranted labor request");
        check(model.Roles.SequenceEqual(new[]{"survival","infrastructure","administrator","workforce","administrator"}),"Managers did not share one sequential client through both arbitration phases");
        check(log.Any(e=>e.Value<string>("event")=="cycle_start")&&log.Any(e=>e.Value<string>("event")=="cycle_end")&&log.Any(e=>e.Value<string>("event")=="administration"),"Cycle telemetry omitted arbitration or lifecycle");

        // An explicit security override releases a development reservation, not game work.
        board=Board(); board.commitments=JArray.Parse("[{id:'development-reservation',manager:'development',resource_requests:{pawn_hours:8}}]");
        var security=Proposal("security","pawns_set_drafted",JObject.Parse("{pawnId:123,enabled:true}"),100);
        security["resource_requests"]=JObject.Parse("{pawn_hours:4}");
        proposals=new List<ManagerProposal>{ManagementSchema.ParseProposal(security,ManagementRole.Security,board)};
        var emergency=Decision(board.Administration(proposals));
        check(Throws(()=>ManagementSchema.ParseDecision(emergency,proposals,board)),"Security silently ignored an existing resource reservation");
        emergency["override_commitments"]=new JArray("development-reservation");
        decision=ManagementSchema.ParseDecision(emergency,proposals,board);
        check(decision.AcceptedActions.Single().action=="pawns_set_drafted"&&decision.override_commitments.Count==1,"Security could not explicitly override development");

        // Event selection remains deterministic and local to affected domains.
        check(ManagerRegistry.Select(ManagementEvent.Food).SequenceEqual(new[]{ManagementRole.Survival,ManagementRole.Workforce}),"Food change invoked unrelated managers");
        check(ManagerRegistry.Select(ManagementEvent.Threat).SequenceEqual(new[]{ManagementRole.Workforce,ManagementRole.Security}),"Threat event selection is incorrect");
        check(ManagerRegistry.Select(ManagementEvent.None).Count==0&&ManagerRegistry.Select(ManagementEvent.Periodic).Count==5,"Periodic/empty event registry regressed");

        // Compatible projects both survive arbitration; coordinator returns data, no execution.
        log=new List<JObject>();
        model=new FakeModel{Respond=(role,ctx)=>role=="administrator"?Reply("submit_decision",Decision(ctx)):Reply("submit_proposal",role=="survival"?Proposal(role,"orders_allow",JObject.Parse("{ids:[123]}")):Proposal(role,"research_select",JObject.Parse("{research:'FixtureResearch'}")))};
        decision=Run(model,Board(),log,ManagementRole.Survival,ManagementRole.Development);
        check(decision.AcceptedActions.Count==2&&decision.proposals.All(p=>p.status=="accept"),"Compatible proposals were unnecessarily serialized or dropped");
        check(model.Roles.SequenceEqual(new[]{"survival","development","administrator"}),"Shared model client did not receive every domain and Administrator call");

        // Accepted labor urgency must survive translation into native numeric priorities.
        board=Board();
        var highJson=Proposal("survival",priority:100); highJson["labor_requests"]=JArray.Parse("[{workType:'Growing',hours:2}]");
        var lowJson=Proposal("infrastructure",priority:20); lowJson["labor_requests"]=JArray.Parse("[{workType:'Construction',hours:2}]");
        var high=ManagementSchema.ParseProposal(highJson,ManagementRole.Survival,board);
        var low=ManagementSchema.ParseProposal(lowJson,ManagementRole.Infrastructure,board);
        board.acceptedLabor=new JArray(new[]{high,low}.Select(p=>new JObject{["proposalId"]=p.id,["priority"]=p.priority,["labor_requests"]=JArray.FromObject(p.labor_requests)}));
        var assignments=Proposal("workforce");
        assignments["requests"]=new JArray(
            new JObject{["action"]="work_set_priority",["requestId"]=high.id,["args"]=JObject.Parse("{pawnId:123,workType:'Growing',priority:4}")},
            new JObject{["action"]="work_set_priority",["requestId"]=low.id,["args"]=JObject.Parse("{pawnId:123,workType:'Construction',priority:1}")});
        var staffing=ManagementSchema.ParseProposal(assignments,ManagementRole.Workforce,board);
        proposals=new List<ManagerProposal>{high,low,staffing};
        check(Throws(()=>ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board)),"Workforce reversed accepted urgency through native priority numbers");
        staffing.requests[0].args["priority"]=0;
        check(Throws(()=>ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board)),"Workforce disabled urgent work while accepting lower-priority work");
        staffing.requests[0].args["priority"]=1; staffing.requests[1].args["priority"]=4;
        check(ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board).AcceptedActions.Count==2,"Correct native priority ordering was rejected");

        // Actor identity hidden behind native handles still participates in arbitration.
        board.actionContexts["security-handle"]=JObject.Parse("{pawnId:123}");
        board.actionContexts["work-handle"]=JObject.Parse("{pawnId:123}");
        var combat=ManagementSchema.ParseProposal(Proposal("security","pawns_order",JObject.Parse("{actionId:'security-handle'}")),ManagementRole.Security,board);
        var workOrder=Proposal("workforce","pawns_order",JObject.Parse("{actionId:'work-handle'}")); workOrder["requests"][0]["requestId"]=high.id;
        var laborOrder=ManagementSchema.ParseProposal(workOrder,ManagementRole.Workforce,board);
        proposals=new List<ManagerProposal>{high,combat,laborOrder};
        check(Throws(()=>ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board)),"Opaque native handles bypassed Security/Workforce pawn conflict checks");
        board.actionContexts["work-handle"]["pawnId"]=456;
        check(ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board).AcceptedActions.Count==2,"Different pawns were incorrectly treated as competing orders");
        check(Throws(()=>ManagementSchema.ParseProposal(Proposal("security","pawns_order",JObject.Parse("{actionId:'invented'}")),ManagementRole.Security,board)),"Undiscovered native action ID entered proposal");

        var doubleLabor=Proposal("survival"); doubleLabor["labor_requests"]=JArray.Parse("[{workType:'Growing',hours:2}]"); doubleLabor["resource_requests"]=JObject.Parse("{pawn_hours:2}");
        check(Throws(()=>ManagementSchema.ParseProposal(doubleLabor,ManagementRole.Survival,board)),"Labor reserved twice through both resource fields");
        board=Board(); board.replyRequested=true; proposals=new List<ManagerProposal>();
        var answer=Decision(board.Administration(proposals));
        check(Throws(()=>ManagementSchema.ParseDecision(answer,proposals,board)),"Pending player direction received no response");
        answer["player_response"]="Finish the shelter first.";
        check(ManagementSchema.ParseDecision(answer,proposals,board).player_response=="Finish the shelter first.","Concrete player reply was rejected");

        // A complete layout is one proposal with all 24 orders preserved, not an eight-call slice.
        board=Board(); var layout=Proposal("infrastructure");
        layout["requests"]=new JArray(Enumerable.Range(0,24).Select(i=>new JObject{["action"]="architect_build",["args"]=new JObject{["defName"]="Wall",["material"]="WoodLog",["x"]=10+i,["z"]=10,["rotation"]=0}}));
        log=new List<JObject>();
        model=new FakeModel{Respond=(role,ctx)=>role=="administrator"?Reply("submit_decision",Decision(ctx)):Reply("submit_proposal",layout)};
        decision=Run(model,board,log,ManagementRole.Infrastructure);
        check(decision.AcceptedActions.Count==24&&decision.AcceptedActions.Select(a=>a.args.Value<int>("x")).SequenceEqual(Enumerable.Range(10,24)),"Accepted layout was capped, truncated or reordered");
        check(model.Roles.Count==2,"Batched construction unexpectedly required one model request per placement");
        // Accepted Allow orders fund construction in this cycle; deferred Allow does not.
        board=Board(); board.resources["WoodLog"]=36;
        board.forbiddenSupplies=JArray.Parse("[{id:901,defName:'WoodLog',count:100,x:10,z:10},{id:902,defName:'WoodLog',count:50,x:20,z:20}]");
        var unlock=ManagementSchema.ParseProposal(Proposal("survival","orders_allow",JObject.Parse("{ids:[901]}")),ManagementRole.Survival,board);
        var fundedJson=Proposal("infrastructure","architect_build",JObject.Parse("{defName:'Bed',material:'WoodLog',x:12,z:12,rotation:0}"));
        fundedJson["resource_requests"]=JObject.Parse("{WoodLog:54}");
        var funded=ManagementSchema.ParseProposal(fundedJson,ManagementRole.Infrastructure,board);
        proposals=new List<ManagerProposal>{unlock,funded};
        check(board.AvailableFor(proposals).Value<double>("WoodLog")==136,"Accepted specific Allow did not credit the discovered forbidden supply");
        check(ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board).AcceptedActions.Count==2,"Accepted Allow could not fund same-cycle bed construction");
        check(Throws(()=>ManagementSchema.ParseDecision(Decision(board.Administration(proposals),p=>p.Value<string>("manager")=="survival"?"defer":"accept"),proposals,board)),"Deferred Allow incorrectly funded construction");
        check(board.resources.Value<double>("WoodLog")==36,"Supply projection mutated observed resource state");
        var areaUnlock=ManagementSchema.ParseProposal(Proposal("survival","orders_allow_area",JObject.Parse("{x:10,z:10,width:10,height:10}")),ManagementRole.Survival,board);
        check(board.AvailableFor(new[]{areaUnlock}).Value<double>("WoodLog")==136,"Allow area missed included supplies or included exclusive edge cells");
        check(board.AvailableFor(new[]{unlock,areaUnlock}).Value<double>("WoodLog")==136,"Overlapping Allow commands credited a stack more than once");
        proposals=new List<ManagerProposal>{areaUnlock,funded};
        check(ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board).AcceptedActions.Count==2,"Accepted area Allow could not fund construction");

        // Final arbitration cannot add a labor grant after staffing has already been planned.
        board=Board(); board.laborGrantsFixed=true;
        var lateJson=Proposal("survival"); lateJson["labor_requests"]=JArray.Parse("[{workType:'Growing',hours:2}]");
        var late=ManagementSchema.ParseProposal(lateJson,ManagementRole.Survival,board);
        proposals=new List<ManagerProposal>{late};
        check(Throws(()=>ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board)),"Final arbitration accepted labor Workforce never reviewed");
        check(ManagementSchema.ParseDecision(Decision(board.Administration(proposals),p=>"defer"),proposals,board).Accepted.Count==0,"Final arbitration could not defer new labor");
        board.acceptedLabor=new JArray(new JObject{["proposalId"]=late.id,["labor_requests"]=JArray.FromObject(late.labor_requests)});
        check(ManagementSchema.ParseDecision(Decision(board.Administration(proposals)),proposals,board).Accepted.Count==1,"Final arbitration rejected previously reviewed labor");
        board.workTypes=new JArray("Growing","Construction");
        check(ManagementSchema.ParseProposal(lateJson,ManagementRole.Survival,board).labor_requests.Single().workType=="Growing","Discovered native work type rejected");
        lateJson["labor_requests"][0]["workType"]="InventedFarmManager";
        check(Throws(()=>ManagementSchema.ParseProposal(lateJson,ManagementRole.Survival,board)),"Invented work type passed native work-type enum");
        // Reuse the actual adapter schemas while roles apply narrower ownership.
        var tools=ToolCatalog.Definitions();
        check(ManagementTools.Actions(ManagementRole.Infrastructure).Any(t=>t.Name=="architect_build"&&t.ParametersJson==tools.Single(c=>c.Name==t.Name).ParametersJson),"Infrastructure diverged from native adapter schema");
        check(ManagementTools.Actions(ManagementRole.Security).Any(t=>t.Name=="pawns_order"&&JObject.Parse(t.ParametersJson)["required"].Values<string>().Contains("actionId")),"Security lost the native action-handle contract");
        check(!ManagementTools.Owns(ManagementRole.Survival,"work_set_priority")&&!ManagementTools.Owns(ManagementRole.Infrastructure,"work_set_priority"),"Non-Workforce manager owns global work allocation");
        foreach(ManagementRole role in Enum.GetValues(typeof(ManagementRole))) check(ManagementTools.Queries(role).All(t=>ManagementTools.IsRead(t.Name)),"Specialist query set contains mutations");
    }
}



