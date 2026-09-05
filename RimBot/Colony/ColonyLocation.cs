using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public sealed class ColonyLocation : MapComponent
    {
        private int baseX=-1,baseZ=-1;
        public ColonyLocation(Map map):base(map) { }
        public IntVec3 Center {
            get {
                if(baseX<0) {
                    var pawns=map.mapPawns.FreeColonistsSpawned;
                    if(pawns.Count==0) return map.Center;
                    // Capture once, before AI orders move anyone. Never follow wandering haulers.
                    baseX=(int)pawns.Average(p=>p.Position.x); baseZ=(int)pawns.Average(p=>p.Position.z);
                }
                return new IntVec3(baseX,0,baseZ);
            }
        }
        public void SetCenter(IntVec3 cell) { if(!cell.InBounds(map)) return; baseX=cell.x; baseZ=cell.z; }
        public override void ExposeData() { Scribe_Values.Look(ref baseX,"rimBotBaseX",-1); Scribe_Values.Look(ref baseZ,"rimBotBaseZ",-1); }
        public static void Validate(Map map,int x,int z,int width,int height)
        {
            var center=map.GetComponent<ColonyLocation>().Center;
            if(!PlacementRange.Near(center.x,center.z,x,z,width,height))
                throw new ArgumentException("Site is too far from the colony base at ("+center.x+", "+center.z+"). Use find_build_sites for nearby valid coordinates. Nothing changed.");
        }
        public static JObject Describe(Map map)
        {
            var c=map.GetComponent<ColonyLocation>().Center;
            return new JObject { ["x"]=c.x,["z"]=c.z,["constructionRadius"]=32,["note"]="Persistent colony base. Choose sites with find_build_sites, not invented map coordinates." };
        }
        public static JArray Sites(Map map,string kind)
        {
            if(kind!="room" && kind!="stockpile") throw new ArgumentException("kind must be room or stockpile.");
            int size=kind=="room"?7:4;
            var center=map.GetComponent<ColonyLocation>().Center;
            var wall=DefDatabase<ThingDef>.GetNamed("Wall");
            var sites=new JArray();
            // Only a bounded neighborhood around the base, not an entire-map scan.
            var candidates=CellRect.CenteredOn(center,24).Cells.Where(c=>c.InBounds(map))
                .OrderBy(c=>(c.x+size/2-center.x)*(c.x+size/2-center.x)+(c.z+size/2-center.z)*(c.z+size/2-center.z));
            foreach(var c in candidates) {
                if(c.x<1 || c.z<1 || c.x+size>=map.Size.x || c.z+size>=map.Size.z) continue;
                if(sites.Any(s=>Math.Abs(s["x"].Value<int>()-c.x)<size && Math.Abs(s["z"].Value<int>()-c.z)<size)) continue;
                var cells=CellRect.FromLimits(c.x,c.z,c.x+size-1,c.z+size-1).Cells.ToList();
                if(cells.Any(p=>p.Fogged(map) || !p.Walkable(map) || p.GetEdifice(map)!=null || map.zoneManager.ZoneAt(p)!=null ||
                    p.GetThingList(map).Any(t=>t.def.entityDefToBuild!=null))) continue;
                if(kind=="room" && (cells.Any(p=>!GenConstruct.CanPlaceBlueprintAt(wall,p,Rot4.North,map).Accepted) ||
                    !new IntVec3(c.x+3,0,c.z-1).Walkable(map))) continue;
                sites.Add(new JObject { ["x"]=c.x,["z"]=c.z,["width"]=size,["height"]=size,["kind"]=kind,
                    ["note"]="Clear nearby footprint; placement and materials are rechecked when ordered. Not a pawn path audit." });
                if(sites.Count==3) break;
            }
            return sites;
        }
    }
}
