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
            return new JObject { ["scope"]="all visible loose items on this map; x/z only sort distance; equipped items excluded",["totalStacks"]=matches.Count,["totalItems"]=matches.Sum(i=>(long)i.Count),
                ["forbiddenStacks"]=matches.Count(i=>i.Forbidden),
                ["allowTools"]=matches.Any(i=>i.Forbidden)?new JArray("orders_allow"):new JArray(),
                ["nextOffset"]=offset+limit<matches.Count ? (JToken)(offset+limit) : JValue.CreateNull(),
                ["items"]=new JArray(matches.Skip(offset).Take(limit).Select(i=>new JObject { ["id"]=i.Id,["defName"]=i.DefName,["count"]=i.Count,
                    ["forbidden"]=i.Forbidden,["x"]=i.X,["z"]=i.Z,["distance"]=Math.Round(Math.Sqrt((double)(i.X-x)*(i.X-x)+(double)(i.Z-z)*(i.Z-z)),1) })),
                ["note"]="orders_allow accepts selected items[].id values. Allow only supplies needed for a task after assessing their location and threats. Forbidden is not a cleanup objective. Distance is straight-line, not safety." };
        }
    }
}
