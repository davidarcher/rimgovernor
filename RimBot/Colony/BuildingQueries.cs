using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class BuildingQueries
    {
        private static IEnumerable<Thing> All(Map map) => map.listerBuildings.allBuildingsColonist.Cast<Thing>()
            .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
            .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
            .Where(t=>t.Faction==Faction.OfPlayer && !t.Position.Fogged(map) && Def(t).category==ThingCategory.Building);
        private static ThingDef Def(Thing t)=>(t.def.entityDefToBuild as ThingDef)??t.def;
        public static JArray Counts(Map map,string defName=null)=>new JArray(All(map).Where(t=>defName==null || Def(t).defName==defName)
            .GroupBy(t=>Def(t).defName).OrderBy(g=>g.Key).Select(g=>new JObject {
                ["defName"]=g.Key,["built"]=g.Count(t=>t is Building && !(t is Frame)),["pending"]=g.Count(t=>t is Blueprint || t is Frame)
            }));
        public static JObject Rooms(Map map,JObject a)
        {
            int offset=a.Value<int?>("offset")??0,limit=a.Value<int?>("limit")??10;
            if(offset<0 || limit<1 || limit>20) throw new ArgumentException("Use offset >= 0 and limit 1–20.");
            var rooms=map.regionGrid.AllRooms.Where(r=>!r.PsychologicallyOutdoors && r.Cells.Any(c=>!c.Fogged(map))).OrderBy(r=>r.ID).ToList();
            return new JObject { ["total"]=rooms.Count,["nextOffset"]=offset+limit<rooms.Count?(JToken)(offset+limit):JValue.CreateNull(),
                ["rooms"]=new JArray(rooms.Skip(offset).Take(limit).Select(r=>new JObject {
                    ["id"]=r.ID,["role"]=r.Role.label,["cells"]=r.CellCount,["roofGaps"]=r.OpenRoofCount,
                    ["temperature"]=r.Temperature,["x"]=r.Cells.First(c=>!c.Fogged(map)).x,["z"]=r.Cells.First(c=>!c.Fogged(map)).z
                })) };
        }
        public static JObject Find(Map map,JObject a)
        {
            int offset=a.Value<int?>("offset")??0,limit=a.Value<int?>("limit")??10;
            if(offset<0 || limit<1 || limit>20) throw new ArgumentException("Use offset >= 0 and limit 1–20.");
            string name=a.Value<string>("defName");
            var all=All(map).Where(t=>name==null || Def(t).defName==name).OrderBy(t=>t.thingIDNumber).ToList();
            return new JObject{["total"]=all.Count,["nextOffset"]=offset+limit<all.Count?(JToken)(offset+limit):JValue.CreateNull(),
                ["buildings"]=new JArray(all.Skip(offset).Take(limit).Select(t=>new JObject {
                    ["id"]=t.thingIDNumber,["defName"]=Def(t).defName,["label"]=Def(t).label,
                    ["x"]=t.Position.x,["z"]=t.Position.z,["rotation"]=t.Rotation.AsInt,
                    ["stage"]=t is Frame?"frame":t is Blueprint?"blueprint":"built"
                }))};
        }
    }
}
