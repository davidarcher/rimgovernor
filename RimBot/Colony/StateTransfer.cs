using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace RimBot.Colony
{
    // Transport views only. Never change game observations, save data, or decision fingerprints.
    public static class StateTransfer
    {
        public static JObject Select(JObject source, params string[] fields)
        {
            var result=new JObject();
            foreach(string field in fields) if(source[field]!=null) result[field]=source[field].DeepClone();
            return result;
        }
        public static JObject Colony(JObject snapshot,bool strategic=false)
        {
            var view=(JObject)snapshot.DeepClone();
            view.Remove("note"); view.Remove("hostilityNote");
            if(view["base"] is JObject reference) reference.Remove("note");
            // Item totals already contain forbidden counts; exact stack handles are queried on demand.
            view.Remove("forbiddenItemsSample");
            if(view["looseItemsTop20"] is JArray stocks)
                foreach(JObject stock in stocks) {
                    stock["totalOnMap"]=stock["count"]?.DeepClone(); stock.Remove("count");
                }
            if(view["looseItemsTop20"] is JArray supplies && supplies.Any(s=>(s.Value<long?>("forbidden")??0)>0))
                view["forbiddenSupplyControls"]=new JObject{["selectedStacks"]="orders_allow",["note"]="Allow only selected supplies needed for a task after assessing their location and threats. Distant forbidden resources are not a backlog. Stockpiling is optional; reachability is not safety."};
            if(view["objectives"] is JArray objectives)
                view["objectives"]=new JArray(objectives.OfType<JObject>().Select(o=>Select(o,"id","state","evidence")));
            if(view["notifications"] is JObject notifications) {
                notifications.Remove("scope");
                foreach(var row in notifications.Descendants().OfType<JObject>().ToList())
                    if(row["detail"]?.Type==JTokenType.Null) row.Remove("detail");
            }
            if(strategic) {
                view.Remove("mapId"); view.Remove("mapWidth"); view.Remove("mapHeight"); view.Remove("base");
                view.Remove("stockpiles"); view.Remove("pendingWork");
                if(view["colonists"] is JArray pawns)
                    view["colonists"]=new JArray(pawns.OfType<JObject>().Select(p=>Select(p,"id","name","canFight","downed","weapon","mood","idle","job")));
            }
            return view;
        }
        // Delta keys replace the entire previous value, including arrays. Empty means unchanged.
        public static JObject Changes(JObject before,JObject after)
        {
            var delta=new JObject();
            foreach(var property in after.Properties())
                if(before==null || !JToken.DeepEquals(before[property.Name],property.Value)) delta[property.Name]=property.Value.DeepClone();
            if(before!=null) foreach(var property in before.Properties())
                if(after[property.Name]==null) delta[property.Name]=JValue.CreateNull();
            return delta;
        }
        public static JArray Projects(JArray projects)
            =>new JArray(projects.OfType<JObject>().Select(p=>Select(p,"id","title","state","priority","requiredTools","steps","completeWhen")));
        public static string ToolResult(string tool,string result)
        {
            JToken root;
            try { root=JToken.Parse(result); } catch(JsonException) { return result; }
            // Restrict transformations to known contracts. Keep booleans, null availability,
            // error reasons, IDs and pagination intact; do not globally prune values.
            if(tool=="pawns_inspect" && root is JObject pawnResult && pawnResult["pawn"] is JObject pawn)
                pawnResult["pawn"]=Select(pawn,"id","name","x","z","mood","canFight","canTakeOrder","downed","drafted","fireAtWill","job","jobTarget","weapon","idle");
            return root.ToString(Formatting.None);
        }
        public static string Sizes(string layer,JObject raw,JObject compact)
            =>"[RimBot Context] "+layer+" state chars: "+raw.ToString(Formatting.None).Length+" -> "+compact.ToString(Formatting.None).Length;
    }
}
