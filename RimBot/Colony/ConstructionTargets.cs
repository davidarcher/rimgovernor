using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    // Read-only lifecycle hints. Native actions must still address a current object ID.
    public sealed class ConstructionTargets : MapComponent
    {
        private sealed class Site
        {
            public IntVec3 Cell;
            public ThingDef Def, Stuff;
            public Rot4 Rotation;
            public bool WasFrame;
        }
        private readonly Dictionary<int,Site> sites=new Dictionary<int,Site>();
        private readonly Queue<int> order=new Queue<int>();
        public ConstructionTargets(Map map):base(map) { }
        public void Observe(Thing thing)
        {
            if(!(thing is Blueprint) && !(thing is Frame)) return;
            if(sites.ContainsKey(thing.thingIDNumber)) return;
            sites[thing.thingIDNumber]=new Site{Cell=thing.Position,Def=thing.def.entityDefToBuild as ThingDef,
                Stuff=(thing as IConstructible)?.EntityToBuildStuff(),Rotation=thing.Rotation,WasFrame=thing is Frame}; order.Enqueue(thing.thingIDNumber);
            while(order.Count>512) sites.Remove(order.Dequeue());
        }
        public Thing ResolveReadOnly(int id)
        {
            if(!sites.TryGetValue(id,out var site) || !site.Cell.InBounds(map) || site.Cell.Fogged(map)) return null;
            var matches=site.Cell.GetThingList(map).Where(t=>t.Spawned && t.Position==site.Cell &&
                (t is Building || (!site.WasFrame && t is Frame)) &&
                (t.def.entityDefToBuild??t.def)==site.Def && t.Rotation==site.Rotation &&
                (t is IConstructible construction?construction.EntityToBuildStuff():t.Stuff)==site.Stuff).Take(2).ToList();
            return matches.Count==1?matches[0]:null;
        }
        public string Missing(int id)
        {
            var result=new JObject{["error"]="target_gone",["oldTargetId"]=id,
                ["instruction"]="Discard this ID. Blueprint, frame and finished building have different IDs. Use current objects below or construction_list; do not reuse an old action handle."};
            if(sites.TryGetValue(id,out var site) && site.Cell.InBounds(map) && !site.Cell.Fogged(map)) {
                var cell=site.Cell;
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
