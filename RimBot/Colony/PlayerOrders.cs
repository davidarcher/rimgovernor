using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class PlayerOrders
    {
        public static void RequireMap(Map map)
        {
            if(map!=Find.CurrentMap) throw new ArgumentException("Open the target colony map first.");
        }
        public static void Allow(Map map,IEnumerable<Thing> things)
        {
            RequireMap(map); var command=new Designator_Unforbid();
            foreach(var thing in things) if(command.CanDesignateThing(thing).Accepted) command.DesignateThing(thing);
        }
        public static void Roof(Map map,IEnumerable<IntVec3> cells)
        {
            RequireMap(map); var command=new Designator_AreaBuildRoof();
            foreach(var cell in cells) if(command.CanDesignateCell(cell).Accepted) { command.DesignateSingleCell(cell); command.ShowWarningForCell(cell); }
        }
        public static string Hunt(Map map,Pawn animal)
        {
            RequireMap(map);
            if(animal==null || animal.Position.Fogged(map)) throw new ArgumentException("Choose a visible pawn.");
            if(map.designationManager.DesignationOn(animal,DesignationDefOf.Hunt)!=null) return "Hunt already designated.";
            var command=new Designator_Hunt(); var report=command.CanDesignateThing(animal);
            if(!report.Accepted) throw new ArgumentException(report.Reason??"RimWorld does not allow hunting this pawn.");
            command.DesignateThing(animal); Designator_Hunt.ShowDesignationWarnings(animal);
            return "Hunt designated: "+animal.LabelShort+". Game warnings appear in notifications.";
        }
        private sealed class EquipMenu : FloatMenuOptionProvider_Equip
        {
            public FloatMenuOption Option(Thing item,FloatMenuContext context)=>GetSingleOptionFor(item,context);
        }
        public static string Equip(Map map,Pawn pawn,Thing item)
        {
            RequireMap(map);
            if(pawn==null || !pawn.CanTakeOrder || pawn.equipment==null || item==null || item.Position.Fogged(map)) throw new ArgumentException("Choose a controllable pawn and visible equipment.");
            var context=new FloatMenuContext(new List<Pawn>{pawn},item.DrawPos,map);
            var option=new EquipMenu().Option(item,context);
            if(option?.action==null || option.Disabled) throw new ArgumentException(option?.Label??"Equip is not available.");
            if(EquipmentUtility.AlreadyBondedToWeapon(item,pawn) || !string.IsNullOrEmpty(EquipmentUtility.GetPersonaWeaponConfirmationText(item,pawn)))
                throw new ArgumentException("Equipping this item requires a native confirmation dialog; use the pawn's Equip menu.");
            option.action(); return "Equip ordered: "+pawn.LabelShort+", "+item.LabelShort+".";
        }
        public static string Zone(Map map,List<IntVec3> cells,bool growing,ThingDef crop=null,int? zoneId=null)
        {
            RequireMap(map);
            Designator_ZoneAdd command=growing?(Designator_ZoneAdd)new Designator_ZoneAdd_Growing():new Designator_ZoneAddStockpile_Resources();
            var selection=Find.Selector.SelectedObjects.ToList();
            try {
                Find.Selector.ClearSelection();
                if(zoneId.HasValue) {
                    var target=map.zoneManager.AllZones.FirstOrDefault(z=>z.ID==zoneId.Value);
                    if(target==null || (growing && !(target is Zone_Growing)) || (!growing && !(target is Zone_Stockpile))) throw new ArgumentException("Choose a zone of the matching type.");
                    Find.Selector.Select(target,playSound:false,forceDesignatorDeselect:false);
                }
                foreach(var c in cells) { var report=command.CanDesignateCell(c); if(!report.Accepted) throw new ArgumentException("Zone cannot include "+c+": "+report.Reason); }
                command.DesignateMultiCell(cells);
                var zones=cells.Select(c=>map.zoneManager.ZoneAt(c)).Where(z=>z!=null).Distinct().ToList();
                if(crop!=null) foreach(var zone in zones.OfType<Zone_Growing>()) zone.SetPlantDefToGrow(crop);
                return "Zone designation applied: "+string.Join(", ",zones.Select(z=>z.label+" (ID "+z.ID+")"))+".";
            }
            finally {
                Find.Selector.ClearSelection();
                foreach(var selected in selection) Find.Selector.Select(selected,playSound:false,forceDesignatorDeselect:false);
            }
        }
    }
}
