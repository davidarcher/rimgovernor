using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Tools;
namespace RimBot.Colony
{
    public enum ManagementRole { Survival, Workforce, Infrastructure, Security, Development }
    [Flags] public enum ManagementEvent { None=0, Periodic=1, Food=2, Medical=4, Temperature=8, Power=16, Threat=32, Pawns=64, Work=128, Direction=256, Notifications=512 }
    public sealed class ProposalRisk { public string type; public double severity, time_horizon_hours; }
    public sealed class LaborRequest { public string workType; public double hours; }
    public sealed class ProposedAction {
        public string action, requestId;
        public JObject args;
        public ToolCall Call()=>new ToolCall{Id=Guid.NewGuid().ToString("N"),Name=action,Arguments=(JObject)args.DeepClone()};
    }
    public sealed class ManagerProposal {
        public string id, manager, summary;
        public int priority;
        public List<ProposalRisk> risks=new List<ProposalRisk>();
        public List<ProposedAction> requests=new List<ProposedAction>();
        public List<LaborRequest> labor_requests=new List<LaborRequest>();
        public Dictionary<string,double> resource_requests=new Dictionary<string,double>();
        public double review_after_hours;
        public JObject Json()=>JObject.FromObject(this);
    }
    public sealed class ProposalDisposition { public string proposalId, status, reason; }
    public sealed class AdministrationDecision {
        public string summary, player_response;
        public List<ProposalDisposition> proposals=new List<ProposalDisposition>();
        public List<string> override_commitments=new List<string>();
        public double review_after_hours;
        [JsonIgnore] public List<ProposedAction> AcceptedActions=new List<ProposedAction>();
        [JsonIgnore] public List<ManagerProposal> Accepted=new List<ManagerProposal>();
        public JObject Json()=>JObject.FromObject(this);
    }
    // Only observations and intentions cross this boundary. No Map, pawn, delegate or executor.
    public sealed class ColonyBlackboard {
        public JObject summary=new JObject(), resources=new JObject(), pawnAvailability=new JObject();
        public JArray incidents=new JArray(), commitments=new JArray(), recentDecisions=new JArray();
        public string goals="", direction="";
        public bool replyRequested, laborGrantsFixed;
        public JObject actionContexts=new JObject();
        public JArray workTypes=new JArray();
        public JArray forbiddenSupplies=new JArray();
        public JObject AvailableFor(IEnumerable<ManagerProposal> proposals) {
            var available=(JObject)resources.DeepClone();
            var calls=proposals.SelectMany(p=>p.requests).ToList();
            foreach(var supply in forbiddenSupplies.OfType<JObject>()) {
                int id=supply.Value<int>("id"),x=supply.Value<int>("x"),z=supply.Value<int>("z");
                bool allowed=calls.Any(a=>a.action=="orders_allow" && (a.args["ids"] as JArray)?.Values<int>().Contains(id)==true || a.action=="orders_allow_area" && x>=a.args.Value<int>("x") && z>=a.args.Value<int>("z") && (long)x<a.args.Value<int>("x")+(long)a.args.Value<int>("width") && (long)z<a.args.Value<int>("z")+(long)a.args.Value<int>("height"));
                if(allowed) { string name=supply.Value<string>("defName"); available[name]=(available.Value<double?>(name)??0)+supply.Value<int>("count"); }
            }
            return available;
        }
        public JObject details=new JObject();
        public JArray acceptedLabor=new JArray();
        public JObject Shared()=>new JObject{["summary"]=summary.DeepClone(),["resources"]=resources.DeepClone(),
            ["pawnAvailability"]=pawnAvailability.DeepClone(),["incidents"]=incidents.DeepClone(),["goals"]=goals,["playerDirection"]=direction,["workTypes"]=workTypes.DeepClone()};
        public JObject Context(ManagementRole role) {
            var o=Shared(); o["domain"]=details[role.ToString().ToLowerInvariant()]?.DeepClone()??new JObject();
            string name=role.ToString().ToLowerInvariant();
            o["commitments"]=new JArray(commitments.OfType<JObject>().Where(c=>c.Value<string>("manager")==name || role==ManagementRole.Workforce));
            o["recentDecisions"]=new JArray(recentDecisions.Take(8));
            if(role==ManagementRole.Workforce) o["acceptedLabor"]=acceptedLabor.DeepClone();
            return o;
        }
        public JObject Administration(IEnumerable<ManagerProposal> proposals) {
            var o=Shared(); o["commitments"]=commitments.DeepClone(); o["recentDecisions"]=recentDecisions.DeepClone();
            o["resourcesIfProposedAllowSucceeds"]=AvailableFor(proposals); o["proposals"]=new JArray(proposals.Select(p=>p.Json())); o["nativeActionContexts"]=actionContexts.DeepClone(); o["replyRequested"]=replyRequested; o["laborGrantsFixed"]=laborGrantsFixed; o["acceptedLabor"]=acceptedLabor.DeepClone(); return o;
        }
    }
    public static class ManagerRegistry {
        public static List<ManagementRole> Select(ManagementEvent events) {
            var selected=new HashSet<ManagementRole>();
            if((events&(ManagementEvent.Periodic|ManagementEvent.Direction))!=0)
                foreach(ManagementRole role in Enum.GetValues(typeof(ManagementRole))) selected.Add(role);
            if((events&(ManagementEvent.Food|ManagementEvent.Medical|ManagementEvent.Temperature))!=0) { selected.Add(ManagementRole.Survival); selected.Add(ManagementRole.Workforce); }
            if((events&(ManagementEvent.Temperature|ManagementEvent.Power|ManagementEvent.Work))!=0) selected.Add(ManagementRole.Infrastructure);
            if((events&ManagementEvent.Work)!=0) selected.Add(ManagementRole.Workforce);
            if((events&ManagementEvent.Threat)!=0) { selected.Add(ManagementRole.Security); selected.Add(ManagementRole.Workforce); }
            if((events&ManagementEvent.Pawns)!=0) { selected.Add(ManagementRole.Workforce); selected.Add(ManagementRole.Survival); selected.Add(ManagementRole.Security); }
            if((events&ManagementEvent.Notifications)!=0) { selected.Add(ManagementRole.Survival); selected.Add(ManagementRole.Security); }
            return selected.OrderBy(r=>r).ToList();
        }
    }
    public static class ManagementTools {
        private static readonly Dictionary<ManagementRole,string[]> Owned=new Dictionary<ManagementRole,string[]> {
            {ManagementRole.Survival,new[]{"orders_allow","orders_allow_area","orders_hunt","zones_growing_designate"}},
            {ManagementRole.Infrastructure,new[]{"architect_build","areas_build_roof","zones_stockpile_designate","storage_configure","zones_remove_cells","bills_add","bills_configure","orders_allow","orders_allow_area"}},
            {ManagementRole.Security,new[]{"equipment_equip","pawns_set_drafted","pawns_set_fire_at_will","pawns_order"}},
            {ManagementRole.Workforce,new[]{"work_set_priority","pawns_order"}},
            {ManagementRole.Development,new[]{"research_select"}}
        };
        private static readonly HashSet<string> Reads=new HashSet<string>(new[]{"architect_preview","architect_buildables","architect_catalog","architect_materials","construction_list","storage_filter_options","storage_inspect","zones_list","selection_inspect","pawns_inspect","pawns_list","pawns_orders","buildings_list","items_list","map_inspect","rooms_list","bills_list","plants_sowable","research_list","notifications_read"});
        public static bool IsRead(string name)=>Reads.Contains(name);
        public static bool Owns(ManagementRole role,string name)=>Owned[role].Contains(name);
        public static List<ToolDefinition> Actions(ManagementRole role)=>ToolCatalog.Definitions().Where(t=>Owns(role,t.Name)).ToList();
        public static List<ToolDefinition> Queries(ManagementRole role) {
            var names=new HashSet<string>(new[]{"pawns_list","pawns_inspect","items_list","notifications_read","selection_inspect"});
            if(role==ManagementRole.Infrastructure) names.UnionWith(new[]{"architect_preview","architect_buildables","architect_catalog","architect_materials","construction_list","storage_filter_options","storage_inspect","zones_list","buildings_list","map_inspect","rooms_list","bills_list"});
            if(role==ManagementRole.Survival) names.UnionWith(new[]{"plants_sowable","zones_list","map_inspect","bills_list","rooms_list"});
            if(role==ManagementRole.Security || role==ManagementRole.Workforce) names.Add("pawns_orders");
            if(role==ManagementRole.Workforce) names.Add("construction_list");
            if(role==ManagementRole.Development) names.UnionWith(new[]{"research_list","buildings_list","bills_list"});
            return ToolCatalog.Definitions().Where(t=>names.Contains(t.Name)&&IsRead(t.Name)).ToList();
        }
    }
    // Validate the same JSON schemas advertised to the provider, including nested tool arguments.
    public static class ManagementSchema {
        public static void Validate(JToken value,JObject schema,string path="output") {
            if(value==null) throw new ArgumentException(path+" is missing");
            if(schema["anyOf"] is JArray variants) {
                foreach(var variant in variants.OfType<JObject>()) { try { Validate(value,variant,path); return; } catch(ArgumentException) { } }
                throw new ArgumentException(path+" does not match an allowed action schema");
            }
            string type=schema.Value<string>("type");
            bool valid=type==null || type=="object"&&value.Type==JTokenType.Object || type=="array"&&value.Type==JTokenType.Array || type=="string"&&value.Type==JTokenType.String || type=="integer"&&value.Type==JTokenType.Integer || type=="number"&&(value.Type==JTokenType.Integer||value.Type==JTokenType.Float) || type=="boolean"&&value.Type==JTokenType.Boolean;
            if(!valid) throw new ArgumentException(path+" must be "+type);
            if(schema["enum"] is JArray options && !options.Any(v=>JToken.DeepEquals(v,value))) throw new ArgumentException(path+" has an unknown value");
            if(value is JObject obj) {
                var props=schema["properties"] as JObject??new JObject();
                foreach(string required in (schema["required"] as JArray??new JArray()).Values<string>()) if(obj[required]==null) throw new ArgumentException(path+" missing "+required);
                foreach(var property in obj.Properties()) {
                    if(props[property.Name] is JObject child) Validate(property.Value,child,path+"."+property.Name);
                    else if(schema["additionalProperties"] is JObject extra) Validate(property.Value,extra,path+"."+property.Name);
                    else if(schema["additionalProperties"]?.Type==JTokenType.Boolean && !schema.Value<bool>("additionalProperties")) throw new ArgumentException(path+" unknown field "+property.Name);
                }
            }
            if(value is JArray array) {
                if(schema["minItems"]!=null&&array.Count<schema.Value<int>("minItems") || schema["maxItems"]!=null&&array.Count>schema.Value<int>("maxItems")) throw new ArgumentException(path+" invalid array size");
                if(schema["items"] is JObject item) foreach(var v in array) Validate(v,item,path+"[]");
            }
            if(value.Type==JTokenType.Integer || value.Type==JTokenType.Float) {
                double n=value.Value<double>();
                if(double.IsNaN(n)||double.IsInfinity(n)||schema["minimum"]!=null&&n<schema.Value<double>("minimum")||schema["maximum"]!=null&&n>schema.Value<double>("maximum")) throw new ArgumentException(path+" out of range");
            }
            if(value.Type==JTokenType.String && (schema["maxLength"]!=null&&value.Value<string>().Length>schema.Value<int>("maxLength") || schema["minLength"]!=null&&value.Value<string>().Length<schema.Value<int>("minLength"))) throw new ArgumentException(path+" invalid text length");
        }
        public static JObject Proposal(ManagementRole role,ColonyBlackboard board=null) {
            var s=JObject.Parse(@"{type:'object',additionalProperties:false,required:['manager','priority','summary','risks','requests','labor_requests','resource_requests','review_after_hours'],properties:{manager:{type:'string'},priority:{type:'integer',minimum:0,maximum:100},summary:{type:'string',maxLength:1000},risks:{type:'array',items:{type:'object',additionalProperties:false,required:['type','severity','time_horizon_hours'],properties:{type:{type:'string',minLength:1,maxLength:80},severity:{type:'number',minimum:0,maximum:1},time_horizon_hours:{type:'number',minimum:0}}}},requests:{type:'array',items:{type:'object',additionalProperties:false,required:['action','args'],properties:{action:{type:'string'},args:{type:'object'},requestId:{type:'string'}}}},labor_requests:{type:'array',items:{type:'object',additionalProperties:false,required:['workType','hours'],properties:{workType:{type:'string',minLength:1},hours:{type:'number',minimum:0}}}},resource_requests:{type:'object',additionalProperties:{type:'number',minimum:0}},review_after_hours:{type:'number',minimum:0.25,maximum:168}}}");
            s["properties"]["manager"]["enum"]=new JArray(role.ToString().ToLowerInvariant());
            s["properties"]["requests"]["items"]["properties"]["action"]["enum"]=new JArray(ManagementTools.Actions(role).Select(t=>t.Name));
            var variants=new JArray();
            foreach(var tool in ManagementTools.Actions(role)) {
                var arguments=JObject.Parse(tool.ParametersJson);
                if(tool.Name=="pawns_order" && board!=null && board.actionContexts.Count>0) arguments["properties"]["actionId"]["enum"]=new JArray(board.actionContexts.Properties().Select(p=>p.Name));
                var properties=new JObject{["action"]=new JObject{["type"]="string",["enum"]=new JArray(tool.Name)},["args"]=arguments};
                var required=new JArray("action","args");
                if(role==ManagementRole.Workforce) { properties["requestId"]=new JObject{["type"]="string",["minLength"]=1}; required.Add("requestId"); }
                variants.Add(new JObject{["type"]="object",["properties"]=properties,["required"]=required,["additionalProperties"]=false});
            }
            s["properties"]["requests"]["items"]=new JObject{["anyOf"]=variants};
            if(board!=null && board.workTypes.Count>0) s["properties"]["labor_requests"]["items"]["properties"]["workType"]["enum"]=board.workTypes.DeepClone();
            return s;
        }
        public static readonly JObject Decision=JObject.Parse(@"{type:'object',additionalProperties:false,required:['summary','player_response','proposals','override_commitments','review_after_hours'],properties:{summary:{type:'string',maxLength:1200},player_response:{type:'string',maxLength:600},proposals:{type:'array',items:{type:'object',additionalProperties:false,required:['proposalId','status','reason'],properties:{proposalId:{type:'string'},status:{type:'string',enum:['accept','reject','defer']},reason:{type:'string',minLength:1,maxLength:250}}}},override_commitments:{type:'array',items:{type:'string'}},review_after_hours:{type:'number',minimum:0.25,maximum:168}}}");
        public static ManagerProposal ParseProposal(JObject json,ManagementRole role,ColonyBlackboard board) {
            Validate(json,Proposal(role,board)); var p=json.ToObject<ManagerProposal>(); p.id=Guid.NewGuid().ToString("N");
            if(p.resource_requests.ContainsKey("pawn_hours") && p.labor_requests.Count>0) throw new ArgumentException("Use labor_requests for workforce hours; do not also reserve pawn_hours");
            foreach(var a in p.requests) {
                var tool=ManagementTools.Actions(role).Single(t=>t.Name==a.action);
                Validate(a.args,JObject.Parse(tool.ParametersJson),a.action);
                if(role!=ManagementRole.Workforce && a.requestId!=null) throw new ArgumentException("Only Workforce references labor grants");
                if(a.action=="pawns_order" && board.actionContexts[a.args.Value<string>("actionId")] == null) throw new ArgumentException("Native action handle must be discovered in this cycle before proposing it");
                if(role==ManagementRole.Workforce) {
                    var grant=board.acceptedLabor.OfType<JObject>().FirstOrDefault(g=>g.Value<string>("proposalId")==a.requestId);
                    if(grant==null) throw new ArgumentException("Workforce orders require an accepted labor requestId");
                    if(a.action=="work_set_priority" && !(grant["labor_requests"] as JArray).Any(l=>l.Value<string>("workType")==a.args.Value<string>("workType"))) throw new ArgumentException("Work type is outside the accepted labor request");
                }
            }
            if(role==ManagementRole.Workforce && (p.labor_requests.Count>0||p.resource_requests.Count>0)) throw new ArgumentException("Workforce translates granted labor; it cannot allocate additional resources");
            return p;
        }
        public static AdministrationDecision ParseDecision(JObject json,List<ManagerProposal> proposals,ColonyBlackboard board) {
            Validate(json,Decision); var d=json.ToObject<AdministrationDecision>();
            if(board.replyRequested && string.IsNullOrWhiteSpace(d.player_response)) throw new ArgumentException("Reply to the player direction before approving this review");
            if(d.proposals.Count!=proposals.Count || d.proposals.Select(p=>p.proposalId).Distinct().Count()!=proposals.Count || d.proposals.Any(p=>!proposals.Any(v=>v.id==p.proposalId))) throw new ArgumentException("Administrator must disposition each proposal exactly once");
            if(d.override_commitments.Distinct().Count()!=d.override_commitments.Count || d.override_commitments.Any(id=>!board.commitments.Any(c=>c.Value<string>("id")==id))) throw new ArgumentException("Unknown or duplicate commitment override");
            d.Accepted=proposals.Where(p=>d.proposals.Any(v=>v.proposalId==p.id&&v.status=="accept")).OrderByDescending(p=>p.priority).ToList();
            if(board.laborGrantsFixed && d.Accepted.Any(p=>p.labor_requests.Count>0 && !board.acceptedLabor.Any(g=>g.Value<string>("proposalId")==p.id))) throw new ArgumentException("Workforce has already reviewed grants; defer newly accepted labor until the next cycle");
            var totals=new Dictionary<string,double>();
            Action<string,double> add=(k,n)=>{ if(n>0) totals[k]=(totals.TryGetValue(k,out var old)?old:0)+n; };
            foreach(var c in board.commitments.OfType<JObject>().Where(c=>!d.override_commitments.Contains(c.Value<string>("id"))))
                foreach(var r in (c["resource_requests"] as JObject??new JObject()).Properties()) add(r.Name,r.Value.Value<double>());
            foreach(var p in d.Accepted) {
                foreach(var r in p.resource_requests) add(r.Key,r.Value);
                // Labor is accounted once here, not a second time in the Workforce translation.
                add("pawn_hours",p.labor_requests.Sum(l=>l.hours));
            }
            var ongoingGrants=d.Accepted.Where(p=>p.manager=="workforce").SelectMany(p=>p.requests).Select(a=>a.requestId).Distinct().Where(id=>!d.Accepted.Any(p=>p.id==id));
            foreach(string id in ongoingGrants) {
                var grant=board.commitments.OfType<JObject>().FirstOrDefault(c=>c.Value<string>("id")==id&&c.Value<bool?>("pending_labor")==true);
                if(grant!=null) add("pawn_hours",(grant["labor_requests"] as JArray??new JArray()).Sum(l=>l.Value<double>("hours")));
            }
            var funded=board.AvailableFor(d.Accepted);
            foreach(var total in totals) {
                var available=funded[total.Key];
                if(available==null || total.Value>available.Value<double>()+0.001) throw new ArgumentException("Unfunded allocation: "+total.Key+"; explicitly defer work or override a commitment");
            }
            Func<ProposedAction,string> actor=a=>a.args["pawnId"]?.ToString() ?? (a.action=="pawns_order" ? board.actionContexts[a.args.Value<string>("actionId")]?["pawnId"]?.ToString() : null);
            var securityActors=new HashSet<string>(d.Accepted.Where(p=>p.manager=="security").SelectMany(p=>p.requests).Select(actor).Where(id=>id!=null));
            var workforceActions=d.Accepted.Where(p=>p.manager=="workforce").SelectMany(p=>p.requests).ToList();
            if(workforceActions.Any(a=>actor(a)!=null && securityActors.Contains(actor(a)))) throw new ArgumentException("Security and Workforce cannot issue competing orders to the same pawn in one decision");
            foreach(var first in workforceActions.Where(a=>a.action=="work_set_priority")) foreach(var second in workforceActions.Where(a=>a.action=="work_set_priority" && actor(a)==actor(first))) {
                int high=d.Accepted.FirstOrDefault(p=>p.id==first.requestId)?.priority??board.acceptedLabor.FirstOrDefault(g=>g.Value<string>("proposalId")==first.requestId)?.Value<int>("priority")??0; int low=d.Accepted.FirstOrDefault(p=>p.id==second.requestId)?.priority??board.acceptedLabor.FirstOrDefault(g=>g.Value<string>("proposalId")==second.requestId)?.Value<int>("priority")??0;
                if(high>low && (first.args.Value<int>("priority")==0 || second.args.Value<int>("priority")>0 && first.args.Value<int>("priority")>second.args.Value<int>("priority"))) throw new ArgumentException("Workforce priorities reverse accepted labor urgency");
            }
            var conflicts=new Dictionary<string,string>();
            foreach(var p in d.Accepted) foreach(var a in p.requests) {
                if(p.manager=="workforce" && !d.Accepted.Any(parent=>parent.id==a.requestId) && !board.commitments.Any(c=>c.Value<string>("id")==a.requestId && c.Value<bool?>("pending_labor")==true && !d.override_commitments.Contains(a.requestId))) throw new ArgumentException("Workforce order lost its accepted parent request");
                string conflict=a.args["pawnId"]!=null ? a.args["pawnId"]+":"+(a.action=="work_set_priority"?"work:"+a.args["workType"]:a.action) : a.action+":"+a.args.ToString(Formatting.None);
                if(conflicts.ContainsKey(conflict)) throw new ArgumentException("Conflicting or duplicate orders: "+conflict);
                conflicts[conflict]=p.id; d.AcceptedActions.Add(a);
            }
            return d;
        }
    }
}
