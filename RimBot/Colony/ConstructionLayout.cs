using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class ConstructionLayout
    {
        public static List<JObject> Expand(JObject a)
        {
            foreach(string field in new[]{"x","z","rotation"}) if(a[field]?.Type!=JTokenType.Integer) throw new ArgumentException("Supply integer "+field);
            int x=a.Value<int>("x"),z=a.Value<int>("z"),tx=a.Value<int?>("toX")??x,tz=a.Value<int?>("toZ")??z;
            if(x!=tx && z!=tz) throw new ArgumentException("Drag a horizontal or vertical line; diagonal construction lines are not supported.");
            int n=Math.Max(Math.Abs(tx-x),Math.Abs(tz-z))+1;
            if(n>128) throw new ArgumentException("Use construction lines of at most 128 cells.");
            return Enumerable.Range(0,n).Select(i=> { var p=(JObject)a.DeepClone(); p.Remove("toX"); p.Remove("toZ"); p["x"]=x+i*Math.Sign(tx-x); p["z"]=z+i*Math.Sign(tz-z); return p; }).ToList();
        }
        public static void Validate(Map map,List<JObject> placements)
        {
            var occupied=new HashSet<IntVec3>();
            foreach(var p in placements) {
                var def=DefDatabase<ThingDef>.GetNamedSilentFail(p.Value<string>("defName")??"");
                if(def==null || def.category!=ThingCategory.Building) throw new ArgumentException("Choose a building from architect_buildables.");
                var stuff=DefDatabase<ThingDef>.GetNamedSilentFail(p.Value<string>("material")??"");
                if(def.MadeFromStuff && (stuff?.stuffProps==null || !stuff.stuffProps.CanMake(def))) throw new ArgumentException("Supply a compatible material for "+def.defName);
                int rotation=p.Value<int>("rotation"); if(rotation<0 || rotation>3) throw new ArgumentException("Rotation must be 0–3.");
                var cell=new IntVec3(p.Value<int>("x"),0,p.Value<int>("z"));
                if(!cell.InBounds(map) || cell.Fogged(map)) throw new ArgumentException("Placement must be visible and in bounds.");
                foreach(var c in GenAdj.OccupiedRect(cell,new Rot4(rotation),def.Size).Cells) if(!occupied.Add(c)) throw new ArgumentException("Proposed footprints overlap at "+c);
                if(cell.GetThingList(map).Any(t=>t.Position==cell && (t.def==def || t.def.entityDefToBuild==def))) continue;
                PlayerConstruction.Validate(def,cell,map,new Rot4(rotation),def.MadeFromStuff?stuff:null);
            }
        }
        public static JObject Preview(Map map,JObject a)
        {
            var plans=a["placements"] as JArray;
            if(plans==null || plans.Count<1 || plans.Count>32) throw new ArgumentException("Supply 1–32 placements/lines.");
            var placements=plans.OfType<JObject>().SelectMany(Expand).ToList();
            if(placements.Count==0 || placements.Count>256) throw new ArgumentException("Preview supports at most 256 placed objects.");
            string error=null; try { Validate(map,placements); } catch(ArgumentException ex) { error=ex.Message; }
            int minX=placements.Min(p=>p.Value<int>("x")),minZ=placements.Min(p=>p.Value<int>("z"));
            int maxX=placements.Max(p=>p.Value<int>("x")),maxZ=placements.Max(p=>p.Value<int>("z"));
            int x=Math.Max(0,minX-2),z=Math.Max(0,minZ-2),w=Math.Min(map.Size.x-x,maxX-x+5),h=Math.Min(map.Size.z-z,maxZ-z+5);
            return new JObject{["nativePlacementChecksPassed"]=error==null,["error"]=error,["objectCount"]=placements.Count,
                ["preview"]=SpatialView.Read(map,x,z,w,h,placements),
                ["next"]="No orders placed. Check the entire layout, gaps and door access. Revise if needed, then execute its lines with architect_build. Roofs use areas_build_roof. Native per-placement checks do not simulate the future room."};
        }
    }
}
