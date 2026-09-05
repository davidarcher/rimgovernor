using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    // Observed sites, not ID aliases. A replacement is evidence, never an automatic action target.
    public sealed class ConstructionTargets : MapComponent
    {
        private readonly Dictionary<int,IntVec3> sites=new Dictionary<int,IntVec3>();
        private readonly Queue<int> order=new Queue<int>();
        public ConstructionTargets(Map map):base(map) { }
        public void Observe(Thing thing)
        {
            if(!(thing is Blueprint) && !(thing is Frame)) return;
            if(sites.ContainsKey(thing.thingIDNumber)) return;
            sites[thing.thingIDNumber]=thing.Position; order.Enqueue(thing.thingIDNumber);
            while(order.Count>512) sites.Remove(order.Dequeue());
        }
        public string Missing(int id)
        {
            var result=new JObject{["error"]="target_gone",["oldTargetId"]=id,
                ["instruction"]="Discard this ID. Blueprint, frame and finished building have different IDs. Use current objects below or construction_list; do not reuse an old action handle."};
            if(sites.TryGetValue(id,out var cell) && cell.InBounds(map) && !cell.Fogged(map)) {
                result["observedSite"]=new JObject{["x"]=cell.x,["z"]=cell.z};
                result["currentObjects"]=new JArray(cell.GetThingList(map).Where(t=>t.Position==cell && (t is Blueprint || t is Frame || t is Building)).Select(t=>new JObject{
                    ["id"]=t.thingIDNumber,["defName"]=t.def.entityDefToBuild?.defName??t.def.defName,
                    ["stage"]=t is Blueprint?"blueprint":t is Frame?"frame":"built"}));
                result["note"]="These are current objects at the observed site, not guaranteed replacements. Inspect before ordering.";
            }
            return result.ToString(Formatting.None);
        }
    }
}
