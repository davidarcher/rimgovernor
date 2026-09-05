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
        public static string AllowAll(Map map)
        {
            RequireMap(map);
            var command=new Designator_Unforbid();
            int before=map.listerThings.AllThings.Count(t=>!t.Fogged() && command.CanDesignateThing(t).Accepted);
            if(before==0) return "No visible forbidden item stacks to allow.";
            string label="UnforbidAllItems".Translate().ToString();
            var option=command.RightClickFloatMenuOptions.FirstOrDefault(o=>o.Label.StartsWith(label,StringComparison.Ordinal));
            if(option?.action==null || option.Disabled) throw new ArgumentException("Native Allow All option is unavailable.");
            option.action();
            int remaining=map.listerThings.AllThings.Count(t=>!t.Fogged() && command.CanDesignateThing(t).Accepted);
            return "Allowed "+(before-remaining)+" item stacks across the map using Allow All. "+remaining+" remain forbidden.";
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
        public static string Equip(Map map,Pawn pawn,Thing item)
        {
            RequireMap(map);
            if(pawn==null || !pawn.CanTakeOrder || pawn.equipment==null) throw new ArgumentException("Choose a controllable pawn from pawns_list.");
            if(pawn.WorkTagIsDisabled(WorkTags.Violent)) throw new ArgumentException(pawn.LabelShort+" is incapable of violence (canFight=false). Drafting or choosing another weapon cannot remove this incapability.");
            if(item==null || !item.Spawned || item.Map!=map || item.Position.Fogged(map)) throw new ArgumentException("Weapon ID is not a visible item on this map. Query items_list with category=weapon and use items[].id as itemId; never a row number or definition name. Drafting is not required to equip.");
            var previous=FloatMenuMakerMap.currentProvider;
            var provider=new FloatMenuOptionProvider_Equip();
            try {
            FloatMenuMakerMap.currentProvider=provider;
            var context=new FloatMenuContext(new List<Pawn>{pawn},item.DrawPos,map);
            if(!provider.SelectedPawnValid(pawn,context) || !provider.Applies(context) || !provider.TargetThingValid(item,context)) throw new ArgumentException("Native Equip is unavailable.");
            var option=provider.GetOptionsFor(item,context).FirstOrDefault();
            if(option?.action==null || option.Disabled) throw new ArgumentException(option?.Label??"Equip is not available.");
            if(EquipmentUtility.AlreadyBondedToWeapon(item,pawn) || !string.IsNullOrEmpty(EquipmentUtility.GetPersonaWeaponConfirmationText(item,pawn)))
                throw new ArgumentException("Equipping this item requires a native confirmation dialog; use the pawn's Equip menu.");
            option.action(); return "Equip ordered: "+pawn.LabelShort+", "+item.LabelShort+".";
            } finally { FloatMenuMakerMap.currentProvider=previous; }
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
                var accepted=cells.Where(c=>command.CanDesignateCell(c).Accepted).ToList();
                if(accepted.Count==0) throw new ArgumentException("No cells accepted by the native zone designator. Inspect terrain fertility, existing zones and obstructions at this location.");
                int skipped=cells.Count-accepted.Count;
                cells=accepted;
                command.DesignateMultiCell(cells);
                var zones=cells.Select(c=>map.zoneManager.ZoneAt(c)).Where(z=>z!=null).Distinct().ToList();
                if(crop!=null) foreach(var zone in zones.OfType<Zone_Growing>()) zone.SetPlantDefToGrow(crop);
                return "Zone designation applied ("+cells.Count+" accepted cells, "+skipped+" skipped): "+string.Join(", ",zones.Select(z=>z.label+" (ID "+z.ID+")"))+".";
            }
            finally {
                Find.Selector.ClearSelection();
                foreach(var selected in selection) Find.Selector.Select(selected,playSound:false,forceDesignatorDeselect:false);
            }
        }
    }
}
