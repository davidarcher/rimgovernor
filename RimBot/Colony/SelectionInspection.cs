using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class SelectionInspection
    {
        public static JObject Read(Map map,JObject args)
        {
            PlayerOrders.RequireMap(map);
            int id=args.Value<int>("targetId");
            var target=map.listerThings.AllThings.FirstOrDefault(t=>t.thingIDNumber==id && t.Spawned && !t.Position.Fogged(map));
            if(target==null) throw new ArgumentException("Target is no longer visible. Select a current ID from game queries.");
            var position=target.Position;
            var materials=ColonyObserver.Materials(target);
            var needed=materials.Select(m=>m.Value<string>("defName")).ToList();
            var supplies=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t=>!t.Position.Fogged(map) && needed.Contains(t.def.defName)).ToList();
            var result=new JObject{
                ["target"]=new JObject{["id"]=id,["label"]=target.LabelShort,["defName"]=target.def.entityDefToBuild?.defName??target.def.defName,
                    ["x"]=position.x,["z"]=position.z,["stage"]=target is Blueprint?"blueprint":target is Frame?"frame":"existing",["forbidden"]=target.IsForbidden(Faction.OfPlayer)},
                ["materials"]=materials,
                ["nearbySupplies"]=new JArray(supplies.OrderBy(t=>t.Position.DistanceToSquared(position)).Take(8).Select(t=>new JObject{
                    ["id"]=t.thingIDNumber,["defName"]=t.def.defName,["count"]=t.stackCount,["forbidden"]=t.IsForbidden(Faction.OfPlayer),["x"]=t.Position.x,["z"]=t.Position.z})),
                ["matchingSupplyStacks"]=supplies.Count,
                ["nearbyColonists"]=PawnQueries.Find(map,new JObject{["group"]="colonists",["x"]=position.x,["z"]=position.z,["limit"]=6}),
                ["visibleHostilesByDistance"]=PawnQueries.Find(map,new JObject{["group"]="hostiles",["x"]=position.x,["z"]=position.z,["limit"]=6}),
                ["surroundings"]=new JArray(CellRect.CenteredOn(position,2).Cells.Where(c=>c.InBounds(map) && !c.Fogged(map)).Select(c=>new JObject{
                    ["x"]=c.x,["z"]=c.z,["walkable"]=c.Walkable(map),["roofed"]=c.Roofed(map),["edifice"]=c.GetEdifice(map)?.def.defName})),
                ["next"]="Choose a pawnId from nearbyColonists, then inspect this same targetId with that pawnId for native actions. Inspect supply locations before Allow; nearby hostile pawns and jobs are observations, not a certified safe route. Ground supplies need not be stockpiled."
            };
            if(args["pawnId"]!=null) result["menu"]=PawnDirectOrders.Inspect(map,new JObject{["pawnId"]=args["pawnId"].DeepClone(),["targetId"]=id});
            return result;
        }
    }
}
