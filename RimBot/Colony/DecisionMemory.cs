using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Tools;
namespace RimBot.Colony
{
    // Observed repetition, not a cache: every tool still runs against live game state.
    public sealed class DecisionMemory
    {
        private JArray rows=new JArray();
        public void Clear()=>rows.Clear();
        public string Serialize()=>rows.ToString(Formatting.None);
        public void Load(string json) { rows=string.IsNullOrEmpty(json)?new JArray():JArray.Parse(json); Trim(); }
        private void Trim() { while(rows.Count>48 || (rows.Count>1 && Serialize().Length>9000)) rows.RemoveAt(0); }
        public bool Observe(ToolCall call,string result,bool success)
        {
            JToken value; try { value=JToken.Parse(result); } catch(JsonException) { value=new JValue(result); }
            var args=(JObject)(call.Arguments?.DeepClone()??new JObject());
            bool empty=call.Name=="items_list" && value is JObject && value.Value<int?>("totalStacks")==0;
            if(call.Name=="items_list") {
                // An empty map-wide search is the same observation regardless of sorting/pagination.
                foreach(var key in new[]{"x","z","offset","limit"}) args.Remove(key);
                if(args["category"]==null) args["category"]="all";
                if(args["forbidden"]==null) args["forbidden"]="any";
                if(empty) value=new JObject{["totalStacks"]=0,["scope"]="all visible loose items on this map"};
            }
            args=new JObject(args.Properties().OrderBy(p=>p.Name).Select(p=>new JProperty(p.Name,p.Value.DeepClone())));
            string keyText=call.Name+":"+args.ToString(Formatting.None);
            var row=rows.OfType<JObject>().FirstOrDefault(x=>x.Value<string>("key")==keyText);
            string signature;
            using(var hash=SHA256.Create()) signature=Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(value.ToString(Formatting.None))));
            bool same=row!=null && row.Value<string>("signature")==signature && row.Value<bool>("success")==success;
            if(value.ToString(Formatting.None).Length>500) value=new JObject{["summary"]="Unchanged response; inspect current workFocus or original tool result for details."};
            int count=same?row.Value<int>("count")+1:1;
            if(row!=null) rows.Remove(row);
            rows.Add(new JObject{["key"]=keyText,["count"]=count,["success"]=success,["signature"]=signature,["result"]=value}); Trim();
            return count>=3;
        }
        public JArray Repeats()=>new JArray(rows.OfType<JObject>().Where(r=>r.Value<int>("count")>=3).Take(6).Select(r=>new JObject{
            ["query"]=r["key"],["unchangedResponses"]=r["count"],["result"]=r["result"]}));
    }
}
