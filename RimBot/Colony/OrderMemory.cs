using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Tools;
namespace RimBot.Colony
{
    // Previous attempts, not claims that a job completed. Refreshed game facts take precedence.
    public sealed class OrderMemory
    {
        private readonly List<JObject> rows=new List<JObject>();
        public void Clear()=>rows.Clear();
        public void Remember(ToolCall call,string result,bool success)
        {
            var row=new JObject{["tool"]=call.Name,["args"]=call.Arguments?.DeepClone(),["accepted"]=success};
            try { row["result"]=JToken.Parse(result); } catch(JsonException) { row["result"]=result; }
            // Repeated attempts retain the latest outcome without crowding out other orders.
            rows.RemoveAll(previous=>previous.Value<string>("tool")==call.Name && JToken.DeepEquals(previous["args"],row["args"]));
            rows.Add(row);
            while(rows.Count>16 || (rows.Count>1 && Serialize().Length>6000)) rows.RemoveAt(0);
        }
        public string Serialize()=>new JArray(rows).ToString(Formatting.None);
    }
}
