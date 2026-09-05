using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace RimBot.Colony
{
    // Conversation memory only: retain returned handles when verbose tool exchanges are compacted.
    public sealed class ObservedIdentifiers
    {
        private readonly List<JObject> rows=new List<JObject>();
        public void Clear()=>rows.Clear();
        public void ClearActionHandles()=>rows.RemoveAll(row=>row["actionId"]!=null);
        public void RemoveMissingThings(HashSet<int> visible)
        {
            rows.RemoveAll(row=> {
                string source=row.Value<string>("source");
                // Zone and bill IDs belong to separate native namespaces.
                if(!new[]{"selection_inspect","construction_list","buildings_list","items_list","pawns_list","pawns_inspect"}.Contains(source)) return false;
                var id=row["id"]??row["thingId"];
                return id?.Type==JTokenType.Integer && !visible.Contains(id.Value<int>());
            });
        }
        public void Remember(string source,string result)
        {
            JToken root;
            try { root=JToken.Parse(result); } catch(JsonException) { return; }
            foreach(var obj in Walk(root)) {
                string key=obj["actionId"]?.Type==JTokenType.String?"actionId":obj["id"]!=null?"id":obj["billId"]!=null?"billId":obj["zoneId"]!=null?"zoneId":obj["thingId"]!=null?"thingId":null;
                if(key==null || obj[key].Type==JTokenType.Null) continue;
                var row=new JObject{["source"]=source,[key]=obj[key].DeepClone()};
                foreach(string field in new[]{"defName","label","name","stage"}) if(obj[field]?.Type==JTokenType.String) row[field]=ActivitySummary.Short(obj[field].Value<string>(),70);
                foreach(string field in new[]{"canFight","canTakeOrder"}) if(obj[field]?.Type==JTokenType.Boolean) row[field]=obj[field].DeepClone();
                rows.RemoveAll(r=>r.Value<string>("source")==source && JToken.DeepEquals(r[key],row[key]));
                rows.Add(row);
            }
            while(rows.Count>40 || (rows.Count>0 && Serialize().Length>3500)) rows.RemoveAt(0);
        }
        private static IEnumerable<JObject> Walk(JToken token)
        {
            if(token is JObject obj) yield return obj;
            if(token is JContainer container) foreach(var child in container.Children()) foreach(var nested in Walk(child)) yield return nested;
        }
        public string Serialize()=>new JArray(rows.Select(r=>r.DeepClone())).ToString(Formatting.None);
    }
}
