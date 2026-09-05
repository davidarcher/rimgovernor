using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    // Route through the same designator as Architect, including zero-work placement.
    public static class PlayerConstruction
    {
        public static List<Thing> BrokenSpots(Map map) => map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
            .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
            .Where(t=>t.Faction==Faction.OfPlayer && !t.Position.Fogged(map) && t.def.entityDefToBuild is ThingDef d &&
                d.defName=="SleepingSpot" && d.GetStatValueAbstract(StatDefOf.WorkToBuild)==0).ToList();
        public static string ReplaceBrokenSpots(Map map)
        {
            if(map!=Find.CurrentMap || DebugSettings.godMode) return "Open this map with god mode off first.";
            int replaced=0,failed=0;
            foreach(var old in BrokenSpots(map)) {
                var cell=old.Position; var rot=old.Rotation; var def=(ThingDef)old.def.entityDefToBuild;
                var cancel=new Designator_Cancel();
                if(!cancel.CanDesignateThing(old).Accepted) continue;
                cancel.DesignateThing(old);
                try { Place(def,cell,map,rot,null); replaced++; }
                catch(Exception ex) { failed++; Log.Warning("[RimBot] Canceled invalid spot at "+cell+" but replacement failed: "+ex.Message); }
            }
            return "Replaced "+replaced+" broken sleeping-spot orders."+(failed>0?" "+failed+" canceled spots could not be replaced; see log.":"");
        }
        private sealed class Placement : Designator_Build
        {
            public Placement(ThingDef def, ThingDef stuff, Rot4 rotation):base(def) { SetStuffDef(stuff); placingRot=rotation; }
        }
        public static void Place(ThingDef def, IntVec3 cell, Map map, Rot4 rotation, ThingDef stuff)
        {
            Validate(def,cell,map,rotation,stuff);
            new Placement(def,stuff,rotation).DesignateSingleCell(cell);
        }
        public static void Validate(ThingDef def, IntVec3 cell, Map map, Rot4 rotation, ThingDef stuff)
        {
            if(map!=Find.CurrentMap) throw new ArgumentException("Open this colony map before placing orders.");
            if(DebugSettings.godMode) throw new ArgumentException("Turn off god mode before automatic construction.");
            if(BuildCopyCommandUtility.FindAllowedDesignator(def)==null) throw new ArgumentException("This building is not available in Architect.");
            var designator=new Placement(def,stuff,rotation);
            var report=designator.CanDesignateCell(cell);
            if(!report.Accepted) throw new ArgumentException(report.Reason??"Cannot place here.");

        }
    }
}
