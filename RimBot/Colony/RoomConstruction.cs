using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class RoomConstruction
    {
        private sealed class Order
        {
            public ThingDef Def,Material;
            public IntVec3 Cell;
        }
        public static string Build(Map map,JObject a)
        {
            if(a["x"]?.Type!=JTokenType.Integer || a["z"]?.Type!=JTokenType.Integer || a["beds"]?.Type!=JTokenType.Integer)
                throw new ArgumentException("Supply integer x, z and beds (0–4). The exterior is 7x7.");
            int x=a["x"].Value<int>(),z=a["z"].Value<int>();
            var layout=RoomLayout.Create(a["beds"].Value<int>());
            ColonyLocation.Validate(map,x,z,7,7);
            var origin=new IntVec3(x,0,z);
            if(x<0 || z<1 || x>map.Size.x-7 || z>map.Size.z-7) throw new ArgumentException("Room or doorway approach is outside the map.");
            var cells=CellRect.FromLimits(x,z,x+6,z+6).Cells.ToList();
            if(cells.Any(c=>c.Fogged(map) || !c.Walkable(map) && c.GetEdifice(map)?.def.defName!="Wall"))
                throw new ArgumentException("Room area is fogged or obstructed. Inspect a clear 7x7 site; nothing placed.");
            var entrance=new IntVec3(x+3,0,z-1);
            if(!entrance.Walkable(map) || entrance.GetEdifice(map)!=null) throw new ArgumentException("Doorway needs a clear approach south of the room.");
            var orders=new List<Order>();
            foreach(var part in layout) {
                var def=DefDatabase<ThingDef>.GetNamed(part.DefName);
                var cell=new IntVec3(x+part.X,0,z+part.Z);
                if(cell.GetThingList(map).Any(t=>t.Position==cell && (t.def==def || t.def.entityDefToBuild==def))) continue;
                var report=GenConstruct.CanPlaceBlueprintAt(def,cell,Rot4.North,map);
                if(!report.Accepted) throw new ArgumentException("Room cannot fit: "+part.DefName+" at "+cell+": "+report.Reason+". Nothing placed.");
                if(def.researchPrerequisites!=null && def.researchPrerequisites.Any(r=>!r.IsFinished)) throw new ArgumentException("Room structure is not researched: "+part.DefName);
                orders.Add(new Order { Def=def,Cell=cell });
            }
            // Reject occupied circulation space; don't wall in furniture, stockpiles, or other orders.
            foreach(var cell in cells) {
                if(map.zoneManager.ZoneAt(cell)!=null) throw new ArgumentException("Room overlaps a zone. Choose a clear site; nothing placed.");
                foreach(var thing in cell.GetThingList(map).Where(t=>t.def.category==ThingCategory.Building || t.def.entityDefToBuild!=null)) {
                    bool matches=layout.Any(p=>thing.Position==new IntVec3(x+p.X,0,z+p.Z) &&
                        (thing.def.defName==p.DefName || thing.def.entityDefToBuild?.defName==p.DefName));
                    if(!matches) throw new ArgumentException("Existing structure or order blocks the room layout. Nothing placed.");
                }
            }
            var stock=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                .Where(t=>t.def.category==ThingCategory.Item && !t.IsForbidden(Faction.OfPlayer) && !t.Position.Fogged(map))
                .GroupBy(t=>t.def).ToDictionary(g=>g.Key,g=>g.Sum(t=>t.stackCount));
            var wood=DefDatabase<ThingDef>.GetNamed("WoodLog");
            int woodNeed=orders.Where(o=>o.Def.defName!="Wall").Sum(o=>o.Def.costStuffCount);
            if(!stock.ContainsKey(wood) || stock[wood]<woodNeed) {
                if(woodNeed>0) throw new ArgumentException("Room needs "+woodNeed+" allowed wood for beds and door. Allow or gather wood first; nothing placed.");
            }
            if(stock.ContainsKey(wood)) stock[wood]-=woodNeed;
            int wallNeed=orders.Where(o=>o.Def.defName=="Wall").Sum(o=>o.Def.costStuffCount);
            var wallDef=DefDatabase<ThingDef>.GetNamed("Wall");
            var wallMaterial=stock.Where(s=>s.Value>=wallNeed && (s.Key.defName.StartsWith("Blocks",StringComparison.Ordinal) || s.Key==wood) &&
                s.Key.stuffProps!=null && wallDef.stuffCategories.Any(c=>s.Key.stuffProps.categories.Contains(c)))
                .OrderBy(s=>s.Key==wood?1:0).ThenByDescending(s=>s.Value).Select(s=>s.Key).FirstOrDefault();
            if(wallNeed>0 && wallMaterial==null) throw new ArgumentException("Room needs "+wallNeed+" allowed stone blocks of one type or spare wood for walls. Stone chunks cannot be used. Nothing placed; steel is not substituted.");
            foreach(var order in orders) {
                order.Material=order.Def.defName=="Wall"?wallMaterial:wood;
                if(order.Def.stuffCategories==null || !order.Def.stuffCategories.Any(c=>order.Material.stuffProps.categories.Contains(c)))
                    throw new ArgumentException("Room material is incompatible with "+order.Def.defName+". Nothing placed.");
            }
            foreach(var order in orders) GenConstruct.PlaceBlueprintForBuild(order.Def,order.Cell,map,Rot4.North,Faction.OfPlayer,order.Material);
            foreach(var cell in CellRect.FromLimits(x+1,z+1,x+5,z+5).Cells) map.areaManager.BuildRoof[cell]=true;
            return "Room ordered at ("+x+", "+z+"): complete 7x7 perimeter, south door, "+a["beds"]+" beds with central aisle, and roof area. "+orders.Count+" new blueprints; walls use "+(wallMaterial?.label??"existing material")+". Colonists must build it normally; materials are not reserved and reachability is not guaranteed.";
        }
    }
}
