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
            if(target==null) throw new ArgumentException(map.GetComponent<ConstructionTargets>().Missing(id));
            map.GetComponent<ConstructionTargets>().Observe(target);
            var position=target.Position;
            var materials=ColonyObserver.Materials(target);
            var workers=ColonyObserver.Workers(map,target);
            var needed=materials.Where(m=>m.Value<int>("needed")>0).Select(m=>m.Value<string>("defName")).ToList();
            var supplies=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t=>!t.Position.Fogged(map) && needed.Contains(t.def.defName)).ToList();
            var result=new JObject{
                ["target"]=new JObject{["id"]=id,["label"]=target.LabelShort,["defName"]=target.def.entityDefToBuild?.defName??target.def.defName,
                    ["x"]=position.x,["z"]=position.z,["stage"]=target is Blueprint?"blueprint":target is Frame?"frame":"existing",["forbidden"]=target.IsForbidden(Faction.OfPlayer)},
                ["materials"]=materials,
                ["pawnsTargeting"]=workers,
                ["workStatus"]=(target is Blueprint || target is Frame)?(workers.Count>0?"staffed (includes material delivery)":"no current job targets this order"):"existing object",
                ["nearbySupplies"]=new JArray(supplies.OrderBy(t=>t.Position.DistanceToSquared(position)).Take(8).Select(t=>new JObject{
                    ["id"]=t.thingIDNumber,["defName"]=t.def.defName,["count"]=t.stackCount,["forbidden"]=t.IsForbidden(Faction.OfPlayer),["x"]=t.Position.x,["z"]=t.Position.z})),
                ["matchingSupplyStacks"]=supplies.Count,
                ["nearbyColonists"]=PawnQueries.Find(map,new JObject{["group"]="colonists",["x"]=position.x,["z"]=position.z,["limit"]=6}),
                ["visibleHostilesByDistance"]=PawnQueries.Find(map,new JObject{["group"]="hostiles",["x"]=position.x,["z"]=position.z,["limit"]=6}),
                ["surroundings"]=new JArray(CellRect.CenteredOn(position,2).Cells.Where(c=>c.InBounds(map) && !c.Fogged(map)).Select(c=>new JObject{
                    ["x"]=c.x,["z"]=c.z,["walkable"]=c.Walkable(map),["roofed"]=c.Roofed(map),["edifice"]=c.GetEdifice(map)?.def.defName})),
                ["next"]="If work is unstaffed and supplies are sufficient, choose a pawnId and inspect this target for native blockers. Construction uses undrafted pawns. Do not request another order when a worker is already doing the job. Inspect supply locations before Allow; nearby hostile pawns and jobs are observations, not a certified safe route. Ground supplies need not be stockpiled."
            };
            if((target is Blueprint || target is Frame) && workers.Count>0)
                result["next"]="Native jobs already target this construction, including material delivery. Let them progress; do not report missing builders merely because no FinishFrame job has started. Recheck if work stalls. Do not re-allow permitted supplies.";
            if(args["pawnId"]!=null) result["menu"]=PawnDirectOrders.Inspect(map,new JObject{["pawnId"]=args["pawnId"].DeepClone(),["targetId"]=id});
            return result;
        }
    }
}
