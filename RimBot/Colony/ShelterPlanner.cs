using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimBot.Colony
{
    public sealed class ShelterPlanner : MapComponent
    {
        private string activeId="";
        public ShelterPlanner(Map map):base(map) { }
        public override void ExposeData() { Scribe_Values.Look(ref activeId,"rimBotShelterProject",""); }
        private ShelterCell Read(IntVec3 cell)
        {
            if(!cell.InBounds(map) || cell.Fogged(map)) return new ShelterCell{Kind=ShelterCellKind.Unknown};
            var result=new ShelterCell { Kind=ShelterCellKind.Floor,Roofed=cell.Roofed(map),ThickRoof=cell.GetRoof(map)?.isThickRoof==true };
            if(map.zoneManager.ZoneAt(cell)!=null) { result.Kind=ShelterCellKind.Blocked; return result; }
            foreach(var thing in cell.GetThingList(map)) {
                var def=thing.def.entityDefToBuild as ThingDef ?? thing.def;
                if(def.category!=ThingCategory.Building) continue;
                if(thing.Faction!=null && thing.Faction!=Faction.OfPlayer) { result.Kind=ShelterCellKind.Blocked; return result; }
                if(def.building?.isNaturalRock==true && def.mineable) result.Kind=ShelterCellKind.Rock;
                else if(def==ThingDefOf.Door) result.Kind=ShelterCellKind.Door;
                else if(def.defName=="Wall") result.Kind=ShelterCellKind.Wall;
                else if(def.thingClass!=null && typeof(Building_Bed).IsAssignableFrom(def.thingClass) && def.building?.bed_humanlike==true) {
                    if(thing is Building_Bed bed && (bed.Medical || bed.ForPrisoners || bed.ForSlaves)) { result.Kind=ShelterCellKind.Blocked; return result; }
                    result.Occupied=true;
                    if(thing.Position==cell) result.BedSlots=BedUtility.GetSleepingSlotsCount(def.size);
                } else if(thing.def.category==ThingCategory.Building || thing.def.entityDefToBuild!=null) { result.Kind=ShelterCellKind.Blocked; return result; }
            }
            if(result.Kind==ShelterCellKind.Floor && !result.Occupied && !cell.Walkable(map)) result.Kind=ShelterCellKind.Blocked;
            return result;
        }
        private ShelterDesign Evaluate(int x,int z,int w,int h,int sleepers,Dictionary<IntVec3,ShelterCell> cache=null)
        {
            if(x<1 || z<1 || x+w>=map.Size.x || z+h>=map.Size.z || !PlacementRange.Near(Center.x,Center.z,x,z,w,h)) return null;
            Func<IntVec3,ShelterCell> read=c=> {
                if(cache==null) return Read(c);
                if(!cache.TryGetValue(c,out var value)) { value=Read(c); cache[c]=value; }
                return value;
            };
            var cells=new ShelterCell[w,h];
            for(int dx=0;dx<w;dx++) for(int dz=0;dz<h;dz++) cells[dx,dz]=read(new IntVec3(x+dx,0,z+dz));
            return ShelterGeometry.Evaluate(cells,sleepers,(dx,dz)=> {
                var outside=new IntVec3(x+dx+(dx==0?-1:dx==w-1?1:0),0,z+dz+(dz==0?-1:dz==h-1?1:0));
                var c=read(outside); return (c.Kind==ShelterCellKind.Floor || c.Kind==ShelterCellKind.Door) && !c.Occupied;
            });
        }
        private IntVec3 Center=>map.GetComponent<ColonyLocation>().Center;
        private static string Id(int x,int z,int w,int h,int sleepers)=>string.Join(":",new[]{x,z,w,h,sleepers});
        private static int[] Decode(string id)
        {
            var pieces=(id??"").Split(':');
            if(pieces.Length!=5 || pieces.Any(p=>!int.TryParse(p,out _))) throw new ArgumentException("Use a shelter ID returned by find_shelter_options.");
            var n=pieces.Select(int.Parse).ToArray();
            if(n[2]<4 || n[2]>8 || n[3]<4 || n[3]>8 || n[4]<1 || n[4]>6) throw new ArgumentException("Invalid shelter dimensions or capacity.");
            return n;
        }
        private JObject Describe(int x,int z,int w,int h,int sleepers,ShelterDesign p)=>new JObject {
            ["id"]=Id(x,z,w,h,sleepers),["x"]=x,["z"]=z,["width"]=w,["height"]=h,["approach"]=p.Kind,["sleepers"]=sleepers,
            ["reusedBoundaryCells"]=p.Reused,["newWalls"]=p.Walls.Count,["newDoor"]=p.NewDoor,["rockCellsToMine"]=p.Mine.Count,
            ["unroofedInteriorCells"]=p.MissingRoof,["overheadMountainCells"]=p.ThickRoof,["woodEstimate"]=p.WoodEstimate,
            ["newSleepingSpots"]=p.Beds.Count,["distanceFromBase"]=Math.Round(Math.Sqrt((x+w/2-Center.x)*(x+w/2-Center.x)+(z+h/2-Center.z)*(z+h/2-Center.z)),1),
            ["notes"]="Reuses existing walls, doors, rock and beds. Temporary sleeping spots avoid unnecessary bed materials. Wood estimate assumes wood walls; available stone blocks are preferred. Mining uses only visible rock; no hidden rooms are inspected. Overhead mountain carries infestation risk."
        };
        public JArray Find(int sleepers)
        {
            if(sleepers<1 || sleepers>6) throw new ArgumentException("sleepers must be 1–6.");
            var cache=new Dictionary<IntVec3,ShelterCell>();
            var options=new List<JObject>();
            foreach(var cell in CellRect.CenteredOn(Center,20).Cells) foreach(var size in new[]{new[]{5,5},new[]{5,7},new[]{7,5},new[]{7,7},new[]{8,6},new[]{6,8}}) {
                var plan=Evaluate(cell.x,cell.z,size[0],size[1],sleepers,cache);
                if(plan==null) continue;
                options.Add(Describe(cell.x,cell.z,size[0],size[1],sleepers,plan));
            }
            var selected=new JArray();
            foreach(var option in options.OrderBy(o=>o["woodEstimate"].Value<int>()+o["rockCellsToMine"].Value<int>()*10+o["unroofedInteriorCells"].Value<int>()+
                o["overheadMountainCells"].Value<int>()*3+o["distanceFromBase"].Value<double>()*2)) {
                int x=option["x"].Value<int>(),z=option["z"].Value<int>();
                if(selected.Any(o=>Math.Abs(o["x"].Value<int>()-x)<4 && Math.Abs(o["z"].Value<int>()-z)<4)) continue;
                selected.Add(option); if(selected.Count==5) break;
            }
            return selected;
        }
        public JObject Active()
        {
            if(string.IsNullOrEmpty(activeId)) return null;
            var n=Decode(activeId); var plan=Evaluate(n[0],n[1],n[2],n[3],n[4]);
            return new JObject { ["id"]=activeId,["phase"]=plan==null?"site changed; inspect before continuing":plan.Mine.Count>0?"excavation":"enclosure, roofing and sleeping places",
                ["instruction"]="Continue this footprint with prepare_shelter after game progress. Do not start a duplicate shelter. Completion requires actually sheltered sleeping places." };
        }
        public string Prepare(string id)
        {
            var n=Decode(id); int x=n[0],z=n[1],w=n[2],h=n[3];
            ColonyLocation.Validate(map,x,z,w,h);
            var plan=Evaluate(x,z,w,h,n[4]);
            if(plan==null) throw new ArgumentException("Shelter no longer fits. Inspect again; nothing changed.");
            Func<ShelterPoint,IntVec3> pos=p=>new IntVec3(x+p.X,0,z+p.Z);
            var entry=pos(plan.Door)+new IntVec3(plan.Door.X==0?-1:plan.Door.X==w-1?1:0,0,plan.Door.Z==0?-1:plan.Door.Z==h-1?1:0);
            if(!map.mapPawns.FreeColonistsSpawned.Any(p=>p.CanReach(entry,PathEndMode.OnCell,Danger.Some))) throw new ArgumentException("No colonist can reach the shelter entrance. Nothing changed.");
            if(plan.Mine.Count>0) {
                var retained=CellRect.FromLimits(x,z,x+w-1,z+h-1).Cells.Where(c=>c.x==x || c.z==z || c.x==x+w-1 || c.z==z+h-1)
                    .Where(c=>c!=pos(plan.Door) && c.GetEdifice(map)?.def.holdsRoof==true).ToList();
                foreach(var point in plan.Mine) {
                    var c=pos(point);
                    if(c.Roofed(map) && !retained.Any(r=>r.DistanceToSquared(c)<=36)) throw new ArgumentException("Excavation lacks nearby retained roof support. Nothing changed.");
                    if(c.GetEdifice(map)?.def.mineable!=true) throw new ArgumentException("Excavation changed; inspect again. Nothing changed.");
                }
                foreach(var point in plan.Mine) {
                    var c=pos(point);
                    if(map.designationManager.DesignationAt(c,DesignationDefOf.Mine)==null) map.designationManager.AddDesignation(new Designation(c,DesignationDefOf.Mine));
                }
                activeId=id;
                return "Shelter excavation ordered for "+plan.Mine.Count+" visible rock cells. Retained boundary supports are preserved. Wait for miners, then call prepare_shelter with this same ID to enclose and roof it. No buildings spawned.";
            }
            var wall=ThingDefOf.Wall; var door=ThingDefOf.Door; var spot=DefDatabase<ThingDef>.GetNamed("SleepingSpot"); var wood=ThingDefOf.WoodLog;
            var stocks=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t=>t.def.category==ThingCategory.Item && !t.Position.Fogged(map) && !t.IsForbidden(Faction.OfPlayer))
                .GroupBy(t=>t.def).ToDictionary(g=>g.Key,g=>g.Sum(t=>t.stackCount));
            int doorCost=plan.NewDoor?door.costStuffCount:0;
            if(doorCost>0 && (!stocks.ContainsKey(wood) || stocks[wood]<doorCost)) throw new ArgumentException("Allow or gather "+doorCost+" wood for the door. Existing structure preserved; nothing added.");
            if(stocks.ContainsKey(wood)) stocks[wood]-=doorCost;
            int wallCost=plan.Walls.Count*wall.costStuffCount;
            var material=stocks.Where(s=>s.Value>=wallCost && (s.Key==wood || s.Key.defName.StartsWith("Blocks",StringComparison.Ordinal)) && s.Key.stuffProps!=null &&
                wall.stuffCategories.Any(c=>s.Key.stuffProps.categories.Contains(c))).OrderBy(s=>s.Key==wood?1:0).Select(s=>s.Key).FirstOrDefault();
            if(wallCost>0 && material==null) throw new ArgumentException("Need "+wallCost+" allowed wood or stone blocks of one type for the gaps. Nothing added.");
            var orders=new List<Tuple<ThingDef,IntVec3,ThingDef>>();
            foreach(var point in plan.Walls) orders.Add(Tuple.Create(wall,pos(point),material));
            if(plan.NewDoor) orders.Add(Tuple.Create(door,pos(plan.Door),wood));
            foreach(var point in plan.Beds) orders.Add(Tuple.Create(spot,pos(point),(ThingDef)null));
            foreach(var order in orders) {
                var report=GenConstruct.CanPlaceBlueprintAt(order.Item1,order.Item2,Rot4.North,map);
                if(!report.Accepted) throw new ArgumentException("Shelter order does not fit: "+report.Reason+". Nothing added.");
            }
            foreach(var order in orders) GenConstruct.PlaceBlueprintForBuild(order.Item1,order.Item2,map,Rot4.North,Faction.OfPlayer,order.Item3);
            foreach(var c in CellRect.FromLimits(x+1,z+1,x+w-2,z+h-2).Cells) map.areaManager.BuildRoof[c]=true;
            activeId=id;
            return "Shelter ordered: reused "+plan.Reused+" boundary cells, filled "+plan.Walls.Count+" gaps, "+(plan.NewDoor?"added a door":"kept existing doors")+", designated roofing and "+plan.Beds.Count+" temporary sleeping spots. Existing beds are reused. Wait for actual construction and roof completion; upgrade comfort later.";
        }
    }
}
