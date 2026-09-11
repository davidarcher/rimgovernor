#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    internal static class NativePawnDetails
    {
        internal static Obs.PawnDetails Defaults(Obs.PawnDetails? source)=>new Obs.PawnDetails {
            Needs=source==null || !source.HasNeeds || source.Needs,
            Health=source==null || !source.HasHealth || source.Health,
            Equipment=source==null || !source.HasEquipment || source.Equipment,
            Biography=source==null || !source.HasBiography || source.Biography,
            Settings=source==null || !source.HasSettings || source.Settings,
            Social=source==null || !source.HasSocial || source.Social,
            Animals=source==null || !source.HasAnimals || source.Animals,
            VisibleHediffsOnly=source?.VisibleHediffsOnly==true };

        internal static void Apply(Pawn pawn,Obs.PawnState row,Obs.PawnDetails? requested)
        {
            var d=Defaults(requested);
            if(d.Needs) {
                var needs=row.Needs;
                if(pawn.needs?.food!=null) {needs.Food=Number(pawn.needs.food.CurLevelPercentage);needs.HungerCategory=pawn.needs.food.CurCategory.ToString();}
                else {needs.Issues.Add(Missing("food"));needs.Issues.Add(Missing("hunger_category"));}
                if(pawn.needs?.rest!=null) needs.Rest=Number(pawn.needs.rest.CurLevelPercentage); else needs.Issues.Add(Missing("rest"));
                if(pawn.needs?.joy!=null) needs.Joy=Number(pawn.needs.joy.CurLevelPercentage); else needs.Issues.Add(Missing("joy"));
            } else {row.Needs=null;row.Issues.Add(Skipped("needs"));}
            if(d.Health && row.Health!=null) Health(pawn,row.Health,d.VisibleHediffsOnly);
            else if(!d.Health) {
                row.Health=null;
                foreach(var issue in row.Issues.Where(i=>i.Field=="health").ToArray()) row.Issues.Remove(issue);
                row.Issues.Add(Skipped("health"));
            }
            if(d.Equipment) row.Equipment=Equipment(pawn); else row.Issues.Add(Skipped("equipment"));
            if(d.Biography) row.Biography=Biography(pawn); else row.Issues.Add(Skipped("biography"));
            if(d.Settings) row.Settings=Settings(pawn); else row.Issues.Add(Skipped("settings"));
            if(d.Social) row.Issues.Add(Unsupported("social","Safe complete thought and relation projection is not implemented; native refresh APIs can mutate memories."));
            else row.Issues.Add(Skipped("social"));
            if(!d.Animals) row.Issues.Add(Skipped("animal_state"));
            else if(!pawn.RaceProps.Animal) row.Issues.Add(Issue("animal_state",Common.UnavailableReason.NotApplicable,"Pawn is not an animal."));
            else row.AnimalState=Animal(pawn);
        }

        private static void Health(Pawn pawn,Obs.PawnHealth row,bool visibleOnly)
        {
            row.Issues.Clear();
            var set=pawn.health.hediffSet;
            var all=set.hediffs.ToList(); Require(all.Count);
            row.Pain=Number(set.PainTotal);
            row.LifeThreatening=all.Any(h=>h.IsCurrentlyLifeThreatening);
            row.BloodLoss=Number(all.Where(h=>h.def==HediffDefOf.BloodLoss).Sum(h=>(double)h.Severity));
            row.Issues.Add(Unsupported("stable_rest_eligible","The existing medical-rest safety owner does not distinguish unreadable facts from ineligibility."));
            row.ShouldSeekMedicalRest=HealthAIUtility.ShouldSeekMedicalRest(pawn);
            row.UrgentMedicalRest=HealthAIUtility.ShouldSeekMedicalRestUrgent(pawn);
            if(pawn.CurrentBed()!=null) row.BedId=Id(pawn.CurrentBed().GetUniqueLoadID());
            else row.Issues.Add(Issue("bed_id",Common.UnavailableReason.NotApplicable,"Pawn is not in a bed."));
            if(row.Bleeding && !pawn.Dead) {
                var ticks=HealthUtility.TicksUntilDeathDueToBloodLoss(pawn);
                if(ticks>0 && ticks<int.MaxValue) row.HoursUntilDeathFromBloodLoss=ticks/2500.0;
                else row.Issues.Add(Issue("hours_until_death_from_blood_loss",Common.UnavailableReason.NotApplicable,"No finite native bleed-out estimate."));
            } else row.Issues.Add(Issue("hours_until_death_from_blood_loss",Common.UnavailableReason.NotApplicable,"No living bleeding pawn."));
            if(pawn.Dead) row.Issues.Add(Issue("capacities",Common.UnavailableReason.NotApplicable,"Capacities are not evaluated for dead pawns."));
            else if(pawn.health.capacities==null) row.Issues.Add(Missing("capacities"));
            else {
                var defs=DefDatabase<PawnCapacityDef>.AllDefsListForReading; Require(defs.Count);
                foreach(var def in defs) row.Capacities.Add(new Obs.Capacity {DefName=Id(def.defName),Level=Number(pawn.health.capacities.GetLevel(def))});
            }
            var visible=all.Where(h=>!visibleOnly || h.Visible).ToList();
            row.HiddenHediffs=checked((uint)(all.Count-visible.Count));
            foreach(var h in visible) {
                var item=new Obs.Hediff {Definition=Definition(h.def),Severity=Number(h.Severity),SeverityLabel=Text(h.SeverityLabel??""),
                    Visible=h.Visible,Bad=h.def.isBad,Permanent=h.IsPermanent(),LifeThreatening=h.IsCurrentlyLifeThreatening,
                    TendableNow=h.TendableNow(false),Tended=h.IsTended()};
                if(!item.Definition.HasLabel) row.Issues.Add(Issue("hediffs.definition.label",Common.UnavailableReason.NotApplicable,"Native definition supplies no label."));
                if(h.Part!=null) {
                    var index=pawn.RaceProps.body.AllParts.IndexOf(h.Part);
                    if(index<0) throw new InvalidOperationException("Hediff body part is not in this pawn's body.");
                    item.PartIndex=index;item.PartDefName=Id(h.Part.def.defName);item.PartLabel=Text(h.Part.LabelCap);
                }
                var immune=h.TryGetComp<HediffComp_Immunizable>(); item.Immunizable=immune!=null;
                if(immune!=null) {item.Immunity=Number(immune.Immunity);item.FullyImmune=immune.FullyImmune;}
                var tend=h.TryGetComp<HediffComp_TendDuration>();
                if(tend!=null) {
                    if(item.Tended) item.TendQuality=Number(tend.tendQuality);
                    if(!tend.TProps.TendIsPermanent) {item.TendExpiresInTicks=Math.Max(0,tend.tendTicksLeft);item.NextTendInTicks=Math.Max(0,tend.tendTicksLeft-tend.TProps.TendTicksOverlap);}
                }
                row.Hediffs.Add(item);
            }
            row.HediffCompleteness=Complete(visible.Count,all.Count-visible.Count);
            var bills=pawn.BillStack?.Bills;
            if(bills==null) row.Issues.Add(Missing("surgery_bills"));
            else {
                Require(bills.Count);
                foreach(var bill in bills) {
                    var b=new Obs.SurgeryBill {Id=Id(bill.GetUniqueLoadID()),Recipe=Id(bill.recipe.defName),Suspended=bill.suspended};
                    if(bill is Bill_Medical medical && medical.Part!=null) {
                        var index=pawn.RaceProps.body.AllParts.IndexOf(medical.Part);
                        if(index<0) throw new InvalidOperationException("Surgery body part is not in this pawn's body.");
                        b.PartIndex=index;
                    }
                    row.SurgeryBills.Add(b);
                }
            }
            row.Issues.Add(Unsupported("snapshot","This read does not issue health CAS tokens."));
        }

        private static Obs.PawnEquipment Equipment(Pawn pawn)
        {
            var row=new Obs.PawnEquipment();
            if(pawn.equipment==null) {
                row.Issues.Add(Missing("equipped"));row.Issues.Add(Missing("primary_id"));row.Issues.Add(Missing("armed"));
            }
            else {
                var list=pawn.equipment.AllEquipmentListForReading; Require(list.Count);
                foreach(var thing in list) row.Equipped.Add(Gear(thing));
                row.Armed=pawn.equipment.Primary!=null;
                if(pawn.equipment.Primary!=null) row.PrimaryId=Id(pawn.equipment.Primary.GetUniqueLoadID());
                else row.Issues.Add(Issue("primary_id",Common.UnavailableReason.NotApplicable,"No equipped primary weapon."));
            }
            if(pawn.apparel==null) row.Issues.Add(Missing("apparel"));
            else {Require(pawn.apparel.WornApparel.Count);foreach(var thing in pawn.apparel.WornApparel) row.Apparel.Add(Gear(thing));}
            if(pawn.inventory?.innerContainer==null) {row.Issues.Add(Missing("inventory_weapons"));row.Issues.Add(Missing("inventory_item_count"));}
            else {
                var list=pawn.inventory.innerContainer; Require(list.Count);row.InventoryItemCount=checked((uint)list.Count);
                for(var i=0;i<list.Count;i++) if(list[i].def.IsWeapon) row.InventoryWeapons.Add(Gear(list[i]));
            }
            if(pawn.carryTracker==null) row.Issues.Add(Missing("carried_thing_id"));
            else if(pawn.carryTracker.CarriedThing!=null) row.CarriedThingId=Id(pawn.carryTracker.CarriedThing.GetUniqueLoadID());
            else row.Issues.Add(Issue("carried_thing_id",Common.UnavailableReason.NotApplicable,"Pawn is carrying no thing."));
            foreach(var collection in new[]{"equipped","apparel","inventory_weapons"})
                foreach(var field in new[]{"forced","locked","armor_sharp","armor_blunt","insulation_cold","insulation_heat"})
                    row.Issues.Add(Unsupported(collection+"."+field,"Gear ownership and protection stats are not projected."));
            return row;
        }
        private static Obs.GearItem Gear(Thing thing)
        {
            var row=new Obs.GearItem {Thing=Entity(thing),Weapon=thing.def.IsWeapon,Apparel=thing.def.IsApparel,Ranged=thing.def.IsRangedWeapon,Melee=thing.def.IsMeleeWeapon};
            if(thing.Stuff!=null) row.Stuff=Id(thing.Stuff.defName);
            if(thing.TryGetQuality(out var quality)) row.Quality=quality.ToString();
            if(thing.def.useHitPoints) {
                if(thing.MaxHitPoints<=0 || thing.HitPoints<0) throw new InvalidOperationException("Invalid native hit points.");
                row.HitPoints=thing.HitPoints;row.MaxHitPoints=thing.MaxHitPoints;row.ConditionFraction=Number((double)thing.HitPoints/thing.MaxHitPoints);
            }
            if(thing.def.apparel!=null) {
                Require(thing.def.apparel.layers.Count);Require(thing.def.apparel.bodyPartGroups.Count);
                row.ApparelLayers.Add(thing.def.apparel.layers.Select(d=>Id(d.defName)));
                row.BodyPartGroups.Add(thing.def.apparel.bodyPartGroups.Select(d=>Id(d.defName)));
            }
            return row;
        }

        private static Obs.PawnBiography Biography(Pawn pawn)
        {
            var row=new Obs.PawnBiography();
            if(pawn.ageTracker!=null) {row.BiologicalAgeYears=Number(pawn.ageTracker.AgeBiologicalYearsFloat);row.ChronologicalAgeYears=Number(pawn.ageTracker.AgeChronologicalYearsFloat);}
            else {row.Issues.Add(Missing("biological_age_years"));row.Issues.Add(Missing("chronological_age_years"));}
            if(pawn.story==null) {row.Issues.Add(Missing("childhood"));row.Issues.Add(Missing("adulthood"));row.Issues.Add(Missing("traits"));}
            else {
                if(pawn.story.Childhood!=null) {
                    row.Childhood=DefinitionLabel(pawn.story.Childhood.defName,pawn.story.Childhood.TitleCapFor(pawn.gender));
                    if(!row.Childhood.HasLabel) row.Issues.Add(Issue("childhood.label",Common.UnavailableReason.NotApplicable,"Native backstory supplies no title."));
                } else row.Issues.Add(Issue("childhood",Common.UnavailableReason.NotApplicable,"No childhood backstory."));
                if(pawn.story.Adulthood!=null) {
                    row.Adulthood=DefinitionLabel(pawn.story.Adulthood.defName,pawn.story.Adulthood.TitleCapFor(pawn.gender));
                    if(!row.Adulthood.HasLabel) row.Issues.Add(Issue("adulthood.label",Common.UnavailableReason.NotApplicable,"Native backstory supplies no title."));
                } else row.Issues.Add(Issue("adulthood",Common.UnavailableReason.NotApplicable,"No adulthood backstory."));
                if(pawn.story.traits==null) row.Issues.Add(Missing("traits"));
                else {Require(pawn.story.traits.allTraits.Count);foreach(var trait in pawn.story.traits.allTraits) row.Traits.Add(new Obs.Trait {DefName=Id(trait.def.defName),Degree=trait.Degree});}
            }
            if(pawn.skills==null) row.Issues.Add(Missing("skills"));
            else {
                Require(pawn.skills.skills.Count);
                foreach(var skill in pawn.skills.skills) {
                    var definition=DefinitionLabel(skill.def.defName,skill.def.skillLabel);
                    if(!definition.HasLabel) row.Issues.Add(Issue("skills.definition.label",Common.UnavailableReason.NotApplicable,"Native skill supplies no label."));
                    row.Skills.Add(new Obs.Skill {Definition=definition,Level=skill.Level,StoredLevel=skill.levelInt,Passion=skill.passion.ToString(),Disabled=skill.TotallyDisabled});
                }
            }
            var tags=pawn.CombinedDisabledWorkTags;
            foreach(WorkTags tag in Enum.GetValues(typeof(WorkTags))) if(tag!=WorkTags.None && ((int)tag&((int)tag-1))==0 && (tags&tag)!=0) row.DisabledWorkTags.Add(tag.ToString());
            var defs=DefDatabase<WorkTypeDef>.AllDefsListForReading; Require(defs.Count);
            foreach(var def in defs) if(pawn.WorkTypeIsDisabled(def)) row.IncapableWorkTypes.Add(Id(def.defName));
            row.Issues.Add(Unsupported("incapable_sources","Individual incapability sources are not projected."));
            row.Issues.Add(Unsupported("title","Royal, role and story title selection is not projected."));
            row.Issues.Add(Unsupported("title_source","Title source is not projected."));
            return row;
        }

        private static Obs.PawnSettings Settings(Pawn pawn)
        {
            var row=new Obs.PawnSettings();var settings=pawn.playerSettings;
            if(settings==null) {
                foreach(var field in new[]{"medical_care","self_tend","hostility_response","allowed_area_id","master_id","follow_drafted","follow_fieldwork"}) row.Issues.Add(Missing(field));
            }
            else {
                row.MedicalCare=settings.medCare.ToString();row.SelfTend=settings.selfTend;row.HostilityResponse=settings.hostilityResponse.ToString();
                row.FollowDrafted=settings.followDrafted;row.FollowFieldwork=settings.followFieldwork;
                if(settings.Master!=null) row.MasterId=Id(settings.Master.GetUniqueLoadID()); else row.Issues.Add(Issue("master_id",Common.UnavailableReason.NotApplicable,"No assigned master."));
                var area=settings.AreaRestrictionInPawnCurrentMap;
                if(area!=null) row.AllowedAreaId=Id(area.GetUniqueLoadID()); else row.Issues.Add(Issue("allowed_area_id",Common.UnavailableReason.NotApplicable,"No area restriction."));
            }
            // GetPriority initializes and logs for an uninitialized tracker. Never call it in that state.
            if(pawn.workSettings?.Initialized!=true) row.Issues.Add(Missing("work"));
            else {
                var defs=DefDatabase<WorkTypeDef>.AllDefsListForReading;Require(defs.Count);
                foreach(var def in defs) row.Work.Add(new Obs.WorkSetting {DefName=Id(def.defName),Priority=pawn.workSettings.GetPriority(def),Disabled=pawn.WorkTypeIsDisabled(def)});
            }
            if(pawn.timetable==null) row.Issues.Add(Missing("schedule"));
            else for(var hour=0;hour<24;hour++) row.Schedule.Add(new Obs.TimetableSlot {Hour=(uint)hour,AssignmentDefName=Id(pawn.timetable.GetAssignment(hour).defName)});
            row.Issues.Add(Unsupported("snapshot","This read does not issue settings CAS tokens."));
            row.Issues.Add(Unsupported("allowed_areas","Selectable area catalog is not projected."));
            row.Issues.Add(Unsupported("medical_care_options","Selectable medical care catalog is not projected."));
            row.Issues.Add(Unsupported("hostility_response_options","Selectable hostility response catalog is not projected."));
            return row;
        }

        private static Obs.AnimalState Animal(Pawn pawn)
        {
            var row=new Obs.AnimalState {Gender=pawn.gender.ToString()};
            if(pawn.ageTracker!=null) row.AgeYears=Number(pawn.ageTracker.AgeBiologicalYearsFloat); else row.Issues.Add(Missing("age_years"));
            if(pawn.training==null) row.Issues.Add(Missing("training"));
            else {
                var defs=DefDatabase<TrainableDef>.AllDefsListForReading;Require(defs.Count);
                foreach(var def in defs) {
                    var report=pawn.training.CanAssignToTrain(def,out var visible);
                    var item=new Obs.TrainingEntry {DefName=Id(def.defName),Learned=pawn.training.HasLearned(def),Wanted=pawn.training.GetWanted(def),Available=report.Accepted && visible};
                    if(!item.Available) item.Reason=Text(report.Reason??"Native training option is unavailable.");
                    row.Training.Add(item);
                }
            }
            if(pawn.MapHeld?.designationManager==null) {row.Issues.Add(Missing("slaughter"));row.Issues.Add(Missing("release"));}
            else {
                var all=pawn.MapHeld.designationManager.AllDesignationsOn(pawn);
                row.Slaughter=all.Any(d=>d.def==DesignationDefOf.Slaughter);row.Release=all.Any(d=>d.def==DesignationDefOf.ReleaseAnimalToWild);
            }
            var milk=pawn.GetComp<CompMilkable>();if(milk!=null) row.MilkFullness=Number(milk.Fullness);else row.Issues.Add(Issue("milk_fullness",Common.UnavailableReason.NotApplicable,"No milk component."));
            var wool=pawn.GetComp<CompShearable>();if(wool!=null) row.WoolFullness=Number(wool.Fullness);else row.Issues.Add(Issue("wool_fullness",Common.UnavailableReason.NotApplicable,"No wool component."));
            foreach(var field in new[]{"fertile_adult","pregnant","gestation","pen_id","contained","parent_ids","safe_to_slaughter","minimum_handling_skill"}) row.Issues.Add(Unsupported(field,"Animal reproduction, pen and handling detail is not projected."));
            return row;
        }
        private static Obs.DefinitionRef Definition(Def def)=>DefinitionLabel(def.defName,def.LabelCap);
        internal static Obs.DefinitionRef DefinitionLabel(string defName,string? label) {
            var result=new Obs.DefinitionRef {DefName=Id(defName)};
            if(label!=null) result.Label=Text(label);
            return result;
        }
        private static Obs.ReadIssue Missing(string field)=>Issue(field,Common.UnavailableReason.NativeComponentMissing,"Native component is absent or uninitialized.");
        private static Obs.ReadIssue Skipped(string field)=>Issue(field,Common.UnavailableReason.NotRequested,"Detail was explicitly disabled.");
        private static Obs.ReadIssue Unsupported(string field,string detail)=>Issue(field,Common.UnavailableReason.Unsupported,detail);
    }
}
