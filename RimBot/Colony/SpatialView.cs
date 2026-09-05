using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class SpatialView
    {
        public static JObject Read(Map map,int x,int z,int width,int height,IEnumerable<JObject> proposal=null)
        {
            if(width<1 || height<1 || width>32 || height>32 || x<0 || z<0 || x+width>map.Size.x || z+height>map.Size.z) throw new ArgumentException("Grid must fit the map and be at most 32x32.");
            var overlay=new Dictionary<IntVec3,char>();
            foreach(var p in proposal??Enumerable.Empty<JObject>()) {
                var def=DefDatabase<ThingDef>.GetNamedSilentFail(p.Value<string>("defName"));
                if(def==null) continue;
                foreach(var cell in GenAdj.OccupiedRect(new IntVec3(p.Value<int>("x"),0,p.Value<int>("z")),new Rot4(p.Value<int>("rotation")),def.Size).Cells) overlay[cell]=char.ToLowerInvariant(Symbol(def));
            }
            var rows=new JArray(); var roofs=new JArray(); var beds=new HashSet<Building_Bed>();
            for(int row=z+height-1;row>=z;row--) {
                var line=""; var roof="";
                for(int col=x;col<x+width;col++) {
                    var c=new IntVec3(col,0,row);
                    if(c.Fogged(map)) { line+="?"; roof+="?"; continue; }
                    var things=c.GetThingList(map);
                    foreach(var bed in things.OfType<Building_Bed>()) beds.Add(bed);
                    var structure=things.FirstOrDefault(t=>t is Blueprint || t is Frame)??(Thing)c.GetEdifice(map);
                    char symbol=structure==null?(c.Walkable(map)?'.':'~'):Symbol(structure.def.entityDefToBuild as ThingDef??structure.def);
                    if(structure is Blueprint || structure is Frame) symbol=char.ToLowerInvariant(symbol);
                    if(overlay.TryGetValue(c,out char proposed)) symbol=proposed;
                    line+=symbol; roof+=c.Roofed(map)?'R':'.';
                }
                rows.Add(row+":"+line); roofs.Add(row+":"+roof);
            }
            return new JObject{["xStart"]=x,["xEnd"]=x+width-1,["rowsNorthToSouth"]=rows,["roofRowsNorthToSouth"]=roofs,
                ["legend"]="Each character is one cell; columns increase x left to right, row labels are absolute z. W=impassable structure/rock, D=door, B=sleeping furniture, F=other structure, .=open, ~=unwalkable, ?=fog. Lowercase=blueprint/frame or proposed placement. Roof layer: R=existing roof, .=unroofed. Pawns/items/plants omitted; query for details.",
                ["sleepingPlaces"]=new JArray(beds.OrderBy(b=>b.thingIDNumber).Select(b=> {
                    var room=b.GetRoom();
                    return new JObject{["id"]=b.thingIDNumber,["x"]=b.Position.x,["z"]=b.Position.z,["defName"]=b.def.defName,
                        ["humanlike"]=b.def.building.bed_humanlike,["roomId"]=room?.ID,["roomCells"]=room?.CellCount,
                        ["outdoors"]=room==null || room.PsychologicallyOutdoors,["roofGaps"]=room?.OpenRoofCount,
                        ["sheltered"]=room!=null && !room.PsychologicallyOutdoors && room.OpenRoofCount==0};
                })),["note"]="Native room facts describe BUILT structures only. An overlay is a proposal, not proof of enclosure or roof support. Verify the actual room after construction."};
        }
        private static char Symbol(ThingDef d)=>typeof(Building_Door).IsAssignableFrom(d.thingClass)?'D':typeof(Building_Bed).IsAssignableFrom(d.thingClass)?'B':d.passability==Traversability.Impassable?'W':'F';
    }
}
