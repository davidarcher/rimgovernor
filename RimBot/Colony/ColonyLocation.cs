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
        public static JObject Describe(Map map)
        {
            var c=map.GetComponent<ColonyLocation>().Center;
            return new JObject { ["x"]=c.x,["z"]=c.z,["note"]="Player colony reference point. Inspect sites and existing buildings; placement follows game rules, without a fixed radius." };
        }
    }
}
