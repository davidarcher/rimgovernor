using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Tools;
namespace RimBot.Colony
{
    // Persistent records of attempts. Only observed game outcomes establish completion.
    public sealed class TaskLedger
    {
        public JArray Rows { get; private set; }=new JArray();
        public string Serialize()=>Rows.ToString(Formatting.None);
        private static bool Immediate(string tool)=>tool=="orders_allow" || tool=="orders_allow_all";
        public void Load(string json)
        {
            Rows=string.IsNullOrEmpty(json)?new JArray():JArray.Parse(json);
            foreach(var row in Rows.OfType<JObject>().Where(r=>Immediate(r.Value<string>("tool")) && r.Value<string>("state")=="Issued"))
                row["state"]="Complete";
        }
        public void Record(int map,int tick,ToolCall call,string result,bool success)
        {
            if(!success) return; // Failed API attempts belong to error memory/activity, not colony work.
            var args=(JObject)(call.Arguments?.DeepClone()??new JObject());
            string project=args.Value<string>("projectId"); args.Remove("projectId");
            var row=Rows.OfType<JObject>().FirstOrDefault(r=>r.Value<int>("mapId")==map && r.Value<string>("tool")==call.Name && JToken.DeepEquals(r["args"],args));
            if(row==null) {
                row=new JObject{["id"]=Guid.NewGuid().ToString("N"),["mapId"]=map,["tool"]=call.Name,["args"]=args}; Rows.Add(row);
            }
            row["projectId"]=project; row["result"]=ActivitySummary.Short(result,350);
            // Repeating an accepted construction order must not reset its progress clock.
            if(!success || row["state"]==null || row.Value<string>("state")=="Rejected") {
                row["state"]=success?"Issued":"Rejected"; row["lastProgressTick"]=tick;
            }
            // Allow changes a flag synchronously; it does not queue hauling or need approval.
            if(Immediate(call.Name)) row["state"]="Complete";
            while(Rows.Count>200) {
                var old=Rows.OfType<JObject>().FirstOrDefault(r=>r.Value<string>("state")=="Complete" || r.Value<string>("state")=="Rejected");
                if(old==null) break; old.Remove();
            }
        }
        public void ObserveConstruction(JObject row,int tick,string stage,float work)
        {
            if(row.Value<string>("state")=="Rejected") return;
            if(stage=="built") { row["state"]="Complete"; row["stage"]=stage; return; }
            if(stage=="missing") { row["state"]="Missing"; row["stage"]=stage; return; }
            bool progressed=row["stage"]!=null && (row.Value<string>("stage")!=stage || work>(row.Value<float?>("workDone")??0));
            if(progressed) row["lastProgressTick"]=tick;
            row["stage"]=stage; row["workDone"]=work;
            row["state"]=tick-row.Value<int>("lastProgressTick")>=7500?"Stalled":stage=="frame" && work>0?"In progress":"Ordered";
        }
        public JArray View(int map)=>new JArray(Rows.OfType<JObject>().Where(r=>r.Value<int>("mapId")==map && r.Value<string>("state")!="Rejected")
            .Select(r=>StateTransfer.Select(r,"id","projectId","tool","args","state","stage","workDone","materials","result")));
        public string DecisionKey(int map)=>new JArray(Rows.OfType<JObject>().Where(r=>r.Value<int>("mapId")==map && r.Value<string>("state")!="Rejected")
            .Select(r=>StateTransfer.Select(r,"id","state"))).ToString(Formatting.None);
    }
}
