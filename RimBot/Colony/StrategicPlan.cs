using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Tools;
namespace RimBot.Colony
{
    public sealed class StrategicPlan
    {
        public JObject Data { get; private set; }
        public string Season => Data["season"].Value<string>();
        public string Year => Data["year"].Value<string>();
        public string ThreeYears => Data["threeYears"].Value<string>();
        public JArray Projects => (JArray)Data["projects"];
        public string Serialize() => Data.ToString(Formatting.None);
        private static string Text(JToken data,string key,int max)
        {
            if(data[key]?.Type!=JTokenType.String || string.IsNullOrWhiteSpace(data[key].Value<string>()) || data[key].Value<string>().Length>max)
                throw new ArgumentException("Strategy needs " + key + " (1–"+max+" characters).");
            return data[key].Value<string>();
        }
        private static JArray Strings(JToken data,string key,int max,int length)
        {
            var list=data[key] as JArray;
            if(list==null || list.Count>max || list.Any(t=>t.Type!=JTokenType.String || string.IsNullOrWhiteSpace(t.Value<string>()) || t.Value<string>().Length>length))
                throw new ArgumentException("Invalid strategy " + key);
            return (JArray)list.DeepClone();
        }
        public static StrategicPlan Parse(string json)
        {
            if(json==null || json.Length>16000) throw new ArgumentException("Strategy is missing or too large.");
            var input=JObject.Parse(json);
            var result=new JObject { ["season"]=Text(input,"season",500),["year"]=Text(input,"year",500),["threeYears"]=Text(input,"threeYears",500) };
            var projects=input["projects"] as JArray;
            if(projects==null || projects.Count>8) throw new ArgumentException("Strategy supports up to eight seasonal projects.");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            var clean=new JArray();
            foreach(var p in projects) {
                var id=Text(p,"id",40);
                if(!ids.Add(id)) throw new ArgumentException("Duplicate project ID: "+id);
                if(p["priority"]?.Type!=JTokenType.Integer || p["priority"].Value<int>()<1 || p["priority"].Value<int>()>5) throw new ArgumentException("Project priority must be 1–5.");
                var checks=p["completeWhen"] as JArray;
                if(checks==null || checks.Count<1 || checks.Count>6) throw new ArgumentException("Each project needs 1–6 measured completion conditions.");
                var conditions=new JArray();
                foreach(var c in checks) {
                    string metric=Text(c,"metric",80),op=Text(c,"op",12);
                    if(!new[]{"armed_colonists","capable_fighters","growing_cells","configured_food_bills","research_active","colonists","sheltered_slots","food_days","hostiles","patients","medical_emergencies","stockpiles","food_bills","pending_orders"}.Contains(metric) &&
                        !(metric.StartsWith("building:") && metric.Length>9) && !(metric.StartsWith("research:") && metric.Length>9))
                        throw new ArgumentException("Unmeasured completion metric: "+metric+". Use supplied measured counters; forbidden item counts are not colony objectives.");
                    if(op!="atLeast" && op!="atMost") throw new ArgumentException("Unknown completion comparison.");
                    if(c["value"]?.Type!=JTokenType.Integer && c["value"]?.Type!=JTokenType.Float) throw new ArgumentException("Completion target must be numeric.");
                    double value=c["value"].Value<double>();
                    if(double.IsNaN(value) || double.IsInfinity(value) || value<0 || value>1000000) throw new ArgumentException("Invalid completion target.");
                    conditions.Add(new JObject { ["metric"]=metric,["op"]=op,["value"]=value });
                }
                var steps=Strings(p,"steps",5,240);
                if(steps.Count==0) throw new ArgumentException("Project needs at least one next step.");
                clean.Add(new JObject { ["id"]=id,["title"]=Text(p,"title",100),["purpose"]=Text(p,"purpose",240),["priority"]=p["priority"],
                    ["dependsOn"]=Strings(p,"dependsOn",8,40),["requiredTools"]=new JArray(Strings(p,"requiredTools",12,80).Values<string>().Select(ToolNames.Canonical)),["steps"]=steps,["completeWhen"]=conditions });
            }
            foreach(var p in clean) foreach(string dependency in p["dependsOn"].Values<string>())
                if(!ids.Contains(dependency)) throw new ArgumentException("Missing dependency: "+dependency);
            var visited=new HashSet<string>(); var visiting=new HashSet<string>();
            Action<string> visit=null;
            visit=id=> {
                if(visited.Contains(id)) return;
                if(!visiting.Add(id)) throw new ArgumentException("Project dependencies contain a cycle.");
                foreach(string dep in clean.Single(p=>p["id"].Value<string>()==id)["dependsOn"].Values<string>()) visit(dep);
                visiting.Remove(id); visited.Add(id);
            };
            foreach(string id in ids) visit(id);
            result["projects"]=clean;
            return new StrategicPlan { Data=result };
        }
        public string State(JToken project,IDictionary<string,double> metrics,ISet<string> tools)
        {
            var conditions=project["completeWhen"].ToList();
            bool met=conditions.All(c=>metrics.TryGetValue(c["metric"].Value<string>(),out double n) &&
                (c["op"].Value<string>()=="atLeast" ? n>=c["value"].Value<double>() : n<=c["value"].Value<double>()));
            if(met) return "Complete";
            var missing=project["requiredTools"].Values<string>().Select(ToolNames.Canonical).Where(t=>!tools.Contains(t)).ToArray();
            if(missing.Length>0) return "Blocked: missing tools " + string.Join(", ",missing);
            var unknown=conditions.Where(c=>!metrics.ContainsKey(c["metric"].Value<string>())).Select(c=>c["metric"].Value<string>()).ToArray();
            if(unknown.Length>0) return "Blocked: cannot verify " + string.Join(", ",unknown);
            foreach(string id in project["dependsOn"].Values<string>())
                if(State(Projects.Single(p=>p["id"].Value<string>()==id),metrics,tools)!="Complete") return "Waiting for " + id;
            if(conditions.Any(c=>c["metric"].Value<string>().StartsWith("building:") &&
                metrics.TryGetValue("pending:"+c["metric"].Value<string>().Substring(9),out double count) && count>0)) return "In progress";
            return "Ready";
        }
        public JArray Tactical(IDictionary<string,double> metrics,ISet<string> tools)
        {
            return new JArray(Projects.OrderBy(p=>p["priority"].Value<int>()).Select(p=>new { Project=p,Status=State(p,metrics,tools) })
                .Where(p=>p.Status=="Ready" || p.Status=="In progress").Take(3).Select(p=> {
                    var data=(JObject)p.Project.DeepClone(); data["state"]=p.Status; return data;
                }));
        }
        public static List<ToolDefinition> Tools() => new List<ToolDefinition> { new ToolDefinition {
            Name="save_strategy",Description="Save a strategic plan, not world orders. Up to 8 seasonal projects in dependency order. Preserve useful existing projects and IDs. Completion is checked by game metrics, never by your assertion. Copy implemented tool names exactly from the supplied catalog into requiredTools; do not invent synonyms. Only genuinely unsupported capabilities may be named as missing. Do not invent numeric evidence.",
            ParametersJson=@"{type:'object',additionalProperties:false,properties:{season:{type:'string',maxLength:500},year:{type:'string',maxLength:500},threeYears:{type:'string',maxLength:500},projects:{type:'array',maxItems:8,items:{type:'object',additionalProperties:false,properties:{id:{type:'string',maxLength:40},title:{type:'string',maxLength:100},purpose:{type:'string',maxLength:240},priority:{type:'integer',minimum:1,maximum:5},dependsOn:{type:'array',maxItems:8,items:{type:'string'}},requiredTools:{type:'array',maxItems:12,items:{type:'string'}},steps:{type:'array',minItems:1,maxItems:5,items:{type:'string',maxLength:240}},completeWhen:{type:'array',minItems:1,maxItems:6,items:{type:'object',additionalProperties:false,properties:{metric:{type:'string'},op:{type:'string',enum:['atLeast','atMost']},value:{type:'number',minimum:0}},required:['metric','op','value']}}},required:['id','title','purpose','priority','dependsOn','requiredTools','steps','completeWhen']}}},required:['season','year','threeYears','projects']}"
        }};
    }
    public static class StrategySchedule
    {
        public static string Due(bool hasPlan,int lastQuadrum,int quadrum,int lastTick,int tick,string lastGoal,string goal,int oldPopulation,int population,double oldFood,double food,int oldShelter,int shelter,bool projectsFinished=false)
        {
            if(!hasPlan) return "First colony plan";
            if(lastGoal!=goal) return "Player direction changed";
            if(lastQuadrum!=quadrum) return "New quadrum";
            if(tick-lastTick<60000) return null;
            if(projectsFinished) return "Seasonal projects completed";
            if(population!=oldPopulation) return "Colony population changed";
            if(oldFood>=2 && food<0.5) return "Food reserves collapsed";
            if(oldShelter>0 && shelter<=oldShelter- Math.Max(1,(int)Math.Ceiling(oldShelter*0.25))) return "Shelter was lost";
            return null;
        }
    }
}
