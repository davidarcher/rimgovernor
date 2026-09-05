using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;
namespace RimBot.Colony
{
    public static class ColonyDevelopment
    {
        private static int Int(JObject a,string key) { if(a[key]?.Type!=JTokenType.Integer) throw new ArgumentException("Supply integer "+key); return a[key].Value<int>(); }
        private static string Text(JObject a,string key) { if(a[key]?.Type!=JTokenType.String || string.IsNullOrWhiteSpace(a[key].Value<string>())) throw new ArgumentException("Supply "+key); return a[key].Value<string>(); }
        public static bool IsAction(string name)=>new[]{"ensure_growing_zone","set_production_bill","start_research","hunt_animal","equip_weapon"}.Contains(name);
        public static string Execute(Map map,string name,JObject a)
        {
            switch(name) {
                case "list_crops":
                    return new JArray(DefDatabase<ThingDef>.AllDefs.Where(d=>d.plant?.Sowable==true && (d.plant.sowResearchPrerequisites==null || d.plant.sowResearchPrerequisites.All(r=>r.IsFinished)))
                        .OrderBy(d=>d.plant.growDays).Take(20).Select(d=>new JObject { ["defName"]=d.defName,["label"]=d.label,["minFertility"]=d.plant.fertilityMin,["growDays"]=d.plant.growDays,["minGrowingSkill"]=d.plant.sowMinSkill })).ToString(Formatting.None);
                case "ensure_growing_zone":
                    int x=Int(a,"x"),z=Int(a,"z"),w=Int(a,"width"),h=Int(a,"height");
                    if(w<1 || w>8 || h<1 || h>8 || x<0 || z<0 || x+w>map.Size.x || z+h>map.Size.z) throw new ArgumentException("Growing area must be within map and at most 8x8.");
                    ColonyLocation.Validate(map,x,z,w,h);
                    var crop=DefDatabase<ThingDef>.GetNamedSilentFail(Text(a,"crop"));
                    if(crop?.plant?.Sowable!=true || crop.plant.sowResearchPrerequisites?.Any(r=>!r.IsFinished)==true) throw new ArgumentException("Choose an available crop from list_crops.");
                    if(!map.mapPawns.FreeColonistsSpawned.Any(p=>!p.WorkTypeIsDisabled(WorkTypeDefOf.Growing) && p.skills.GetSkill(SkillDefOf.Plants).Level>=crop.plant.sowMinSkill)) throw new ArgumentException("No capable grower meets this crop's skill requirement.");
                    var cells=CellRect.FromLimits(x,z,x+w-1,z+h-1).Cells.ToList();
                    var existing=map.zoneManager.AllZones.OfType<Zone_Growing>().FirstOrDefault(g=>cells.All(c=>g.Cells.Contains(c)));
                    if(existing!=null) return "Existing growing zone covers this area; crop is "+existing.GetPlantDefToGrow().label+". Nothing duplicated or overwritten.";
                    if(cells.Any(c=>c.Fogged(map) || c.GetEdifice(map)!=null || c.GetTerrain(map).fertility<crop.plant.fertilityMin || map.zoneManager.ZoneAt(c)!=null)) throw new ArgumentException("Growing area is occupied, fogged, zoned or insufficiently fertile. Inspect another site.");
                    var zone=new Zone_Growing(map.zoneManager); map.zoneManager.RegisterZone(zone);
                    foreach(var c in cells) zone.AddCell(c);
                    zone.SetPlantDefToGrow(crop);
                    return "Growing zone ordered: "+cells.Count+" cells of "+crop.label+". Colonists sow normally; temperature, season, light, skills and work priorities still matter.";
                case "list_workstations":
                    return new JArray(map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().Take(12).Select(t=>new JObject { ["id"]=t.thingIDNumber,["label"]=t.LabelShort,["defName"]=t.def.defName,
                        ["recipes"]=new JArray(t.def.AllRecipes.Where(r=>r.AvailableNow).Take(15).Select(r=>new JObject{["defName"]=r.defName,["label"]=r.label,["supportsTarget"]=r.products?.Count>0})),
                        ["bills"]=new JArray(t.BillStack.Bills.OfType<Bill_Production>().Select(b=>new JObject{["recipe"]=b.recipe.defName,["mode"]=b.repeatMode.defName,["target"]=b.targetCount})) })).ToString(Formatting.None);
                case "set_production_bill":
                    var table=map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().FirstOrDefault(t=>t.thingIDNumber==Int(a,"workstationId"));
                    int target=a["target"]==null?10:Int(a,"target");
                    string mode=a["mode"]?.Value<string>()??"targetCount";
                    if(mode!="forever" && mode!="targetCount") throw new ArgumentException("Bill mode must be forever or targetCount.");
                    if(table==null || target<1 || target>1000) throw new ArgumentException("Choose a built workstation and target 1–1000.");
                    var recipe=table.def.AllRecipes.FirstOrDefault(r=>r.defName==Text(a,"recipe") && r.AvailableNow);
                    if(recipe==null) throw new ArgumentException("Recipe is not available at this workstation. Use list_workstations.");
                    if(mode=="targetCount" && !(recipe.products?.Count>0)) throw new ArgumentException("This recipe has variable products (e.g. butchering). Use mode forever.");
                    var bill=table.BillStack.Bills.OfType<Bill_Production>().FirstOrDefault(b=>b.recipe==recipe);
                    if(bill==null) { bill=recipe.MakeNewBill() as Bill_Production; if(bill==null) throw new ArgumentException("Recipe cannot create a production bill."); table.BillStack.AddBill(bill); }
                    bill.repeatMode=mode=="forever"?BillRepeatModeDefOf.Forever:BillRepeatModeDefOf.TargetCount; bill.targetCount=target;
                    return "Production bill: "+recipe.label+(mode=="forever"?" forever":" until "+target+" products")+" at "+table.LabelShort+". Normal ingredients, fuel/power, skills and work priorities apply.";
                case "list_research":
                    return new JArray(DefDatabase<ResearchProjectDef>.AllDefs.Where(r=>!r.IsFinished).OrderBy(r=>r.baseCost).Take(25).Select(r=>new JObject{["defName"]=r.defName,["label"]=r.label,["canStart"]=r.CanStartNow,
                        ["prerequisites"]=new JArray(r.prerequisites?.Select(p=>p.defName)??Enumerable.Empty<string>())})).ToString(Formatting.None);
                case "start_research":
                    var project=DefDatabase<ResearchProjectDef>.GetNamedSilentFail(Text(a,"research"));
                    if(project==null || project.IsFinished || !project.CanStartNow) throw new ArgumentException("Research is unavailable, finished, or missing prerequisites/bench. Use list_research and build the required bench normally.");
                    Find.ResearchManager.SetCurrentProject(project);
                    return "Research selected: "+project.label+". Researchers still need normal bench access and work priorities.";
                case "find_wildlife":
                    var center=map.GetComponent<ColonyLocation>().Center;
                    return new JArray(map.mapPawns.AllPawnsSpawned.Where(p=>p.RaceProps.Animal && p.Faction==null && !p.Dead && !p.Position.Fogged(map))
                        .OrderBy(p=>p.Position.DistanceToSquared(center)).Take(20).Select(p=>new JObject{["id"]=p.thingIDNumber,["label"]=p.LabelShort,["x"]=p.Position.x,["z"]=p.Position.z,["bodySize"]=p.BodySize,["predator"]=p.RaceProps.predator,
                            ["huntingOrdered"]=map.designationManager.DesignationOn(p,DesignationDefOf.Hunt)!=null})).ToString(Formatting.None);
                case "hunt_animal":
                    var animal=map.mapPawns.AllPawnsSpawned.FirstOrDefault(p=>p.thingIDNumber==Int(a,"animalId"));
                    if(animal==null || !animal.RaceProps.Animal || animal.Faction!=null || animal.Dead || animal.Position.Fogged(map)) throw new ArgumentException("Choose a visible wild animal from find_wildlife.");
                    if(!map.mapPawns.FreeColonistsSpawned.Any(p=>!p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting) && p.equipment?.Primary?.def.IsRangedWeapon==true && p.workSettings.GetPriority(WorkTypeDefOf.Hunting)>0)) throw new ArgumentException("Equip and enable a capable hunter with a ranged weapon first.");
                    if(map.designationManager.DesignationOn(animal,DesignationDefOf.Hunt)!=null) return "Hunting already ordered; nothing duplicated.";
                    map.designationManager.AddDesignation(new Designation(animal,DesignationDefOf.Hunt));
                    return "Hunting designated for "+animal.LabelShort+". Normal hunting jobs apply; animals can retaliate. Butchering requires a workstation bill.";
                case "equip_weapon":
                    var pawn=map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p=>p.thingIDNumber==Int(a,"pawnId"));
                    var weapon=map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).FirstOrDefault(t=>t.thingIDNumber==Int(a,"itemId"));
                    if(pawn==null || pawn.Downed || pawn.Drafted || pawn.equipment==null || pawn.WorkTagIsDisabled(WorkTags.Violent)) throw new ArgumentException("Choose an undrafted, capable, standing colonist.");
                    if(weapon==null || !weapon.def.IsWeapon || weapon.IsForbidden(Faction.OfPlayer) || weapon.Position.Fogged(map)) throw new ArgumentException("Choose an allowed weapon from find_items.");
                    if(!EquipmentUtility.CanEquip(weapon,pawn,out string equipReason)) throw new ArgumentException(equipReason);
                    if(!pawn.CanReserveAndReach(weapon,PathEndMode.ClosestTouch,Danger.Some)) throw new ArgumentException("Colonist cannot reach or reserve that weapon.");
                    pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Equip,weapon),JobTag.Misc);
                    return "Equip order issued to "+pawn.LabelShort+" for "+weapon.LabelShort+". Wait for the pawn to pick it up; equipment has not been teleported.";
                default: throw new ArgumentException("Unknown development tool: "+name);
            }
        }
    }
}
