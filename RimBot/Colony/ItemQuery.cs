using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace RimBot.Colony
{
    public sealed class ItemRecord
    {
        public int Id,Count,X,Z;
        public string DefName,Category;
        public bool Forbidden;
    }
    public static class ItemQuery
    {
        public static JObject Find(IEnumerable<ItemRecord> records,string defName,string category,string forbidden,int x,int z,int offset,int limit)
        {
            if(limit<1 || limit>40 || offset<0 || offset>100000) throw new ArgumentException("Use limit 1–40 and a nonnegative offset.");
            if(forbidden!="any" && forbidden!="yes" && forbidden!="no") throw new ArgumentException("forbidden must be any, yes or no.");
            if(!new[]{"all","food","material","weapon","apparel","medicine","other"}.Contains(category)) throw new ArgumentException("Unknown item category.");
            var matches=records.Where(i=>(string.IsNullOrEmpty(defName) || i.DefName.Equals(defName,StringComparison.OrdinalIgnoreCase)) &&
                (category=="all" || i.Category==category) && (forbidden=="any" || i.Forbidden==(forbidden=="yes")))
                .OrderBy(i=>(long)(i.X-x)*(i.X-x)+(long)(i.Z-z)*(i.Z-z)).ThenBy(i=>i.Id).ToList();
            return new JObject { ["totalStacks"]=matches.Count,["totalItems"]=matches.Sum(i=>(long)i.Count),
                ["nextOffset"]=offset+limit<matches.Count ? (JToken)(offset+limit) : JValue.CreateNull(),
                ["items"]=new JArray(matches.Skip(offset).Take(limit).Select(i=>new JObject { ["id"]=i.Id,["defName"]=i.DefName,["count"]=i.Count,
                    ["forbidden"]=i.Forbidden,["x"]=i.X,["z"]=i.Z,["distance"]=Math.Round(Math.Sqrt((double)(i.X-x)*(i.X-x)+(double)(i.Z-z)*(i.Z-z)),1) })),
                ["note"]="Visible loose items across this map, sorted by straight-line distance. Not a path/reachability test. Results may change as stacks move; IDs are revalidated before Allow." };
        }
    }
}
