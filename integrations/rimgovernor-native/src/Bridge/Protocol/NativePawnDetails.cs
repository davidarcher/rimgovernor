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
            VisibleHediffsOnly=source?.VisibleHediffsOnly==true, Work=source?.Work==true, Schedule=source?.Schedule==true };

        internal static void Apply(Pawn pawn,System.Collections.Generic.List<Pawn> colonists,Obs.PawnState row,Obs.PawnDetails? requested,Common.ObservationContext context)
        {
            var d=Defaults(requested);
            if(d.Needs) {
                var needs=row.Needs;
                if(pawn.needs?.food!=null) {needs.Food=Number(pawn.needs.food.CurLevelPercentage);needs.HungerCategory=pawn.needs.food.CurCategory.ToString();}
                else {needs.Issues.Add(Missing("food"));needs.Issues.Add(Missing("hunger_category"));}
                if(pawn.needs?.rest!=null) needs.Rest=Number(pawn.needs.rest.CurLevelPercentage); else needs.Issues.Add(Missing("rest"));
                if(pawn.needs?.joy!=null) needs.Joy=Number(pawn.needs.joy.CurLevelPercentage); else needs.Issues.Add(Missing("joy"));
            } else {row.Needs=null;row.Issues.Add(Skipped("needs"));}
            if(d.Health && row.Health!=null) Health(pawn,row.Health,d.VisibleHediffsOnly,context);
            else if(!d.Health) {
                row.Health=null;
                foreach(var issue in row.Issues.Where(i=>i.Field=="health").ToArray()) row.Issues.Remove(issue);
                row.Issues.Add(Skipped("health"));
            }
            if(d.Equipment) row.Equipment=Equipment(pawn); else row.Issues.Add(Skipped("equipment"));
            if(d.Biography) row.Biography=Biography(pawn); else row.Issues.Add(Skipped("biography"));
            if(d.Settings || d.Work || d.Schedule) {
                row.Settings=new Obs.PawnSettings();
                // The bridge contract (go/internal/bridge/work_pawns.go's
                // validateSettings) enforces that a PawnSettings reply carries
                // ONLY the fields the request actually asked for: care policy
                // (medical_care, self_tend) under d.Settings, work priorities
                // plus the allowed area under d.Work, the 24 timetable slots
                // under d.Schedule.
                // Callers that request several (e.g. ReadTendPawns,
                // ReadRoutinePawns) must therefore get exactly their union,
                // never the wider Assign-tab row (hostility_response, follow
                // flags, master, allowed area) that home/pawn_config's own
                // PawnSettingsRead.SettingsBlock reports instead.
                if(d.Settings) CarePolicy(pawn,row.Settings);
                if(d.Work) { Work(pawn,row.Settings); AllowedArea(pawn,row.Settings); row.Settings.FoodRestriction=NativeFoodPolicy.Read(pawn); row.Settings.DrugPolicyWritable=NativeDrugPolicy.Writable(pawn); row.Settings.DrugPolicyName=NativeDrugPolicy.Name(pawn); row.Settings.DrugPolicyDefault=NativeDrugPolicy.OnDefault(pawn); }
                if(d.Schedule) Schedule(pawn,row.Settings);
            } else row.Issues.Add(Skipped("settings"));
            if(d.Social) row.Social=Social(pawn,colonists);
            else row.Issues.Add(Skipped("social"));
            if(!d.Animals) row.Issues.Add(Skipped("animal_state"));
            else if(!pawn.RaceProps.Animal) row.Issues.Add(Issue("animal_state",Common.UnavailableReason.NotApplicable,"Pawn is not an animal."));
            else row.AnimalState=Animal(pawn);
        }

        private static void Health(Pawn pawn,Obs.PawnHealth row,bool visibleOnly,Common.ObservationContext context)
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
                if(immune!=null) {
                    item.Immunity=Number(immune.Immunity);item.FullyImmune=immune.FullyImmune;
                    // Include tending's severity modifier, as well as the disease's
                    // randomized native progression. Read only: never create immunity records.
                    item.SeverityPerDay=Number(((HediffWithComps)h).comps
                        .OfType<HediffComp_SeverityModifierBase>().Sum(comp=>comp.SeverityChangePerDay()));
                    var record=pawn.health.immunity.GetImmunityRecord(h.def);
                    if(record!=null && !pawn.Dead)
                        item.ImmunityPerDay=Number(record.ImmunityChangePerTick(pawn,true,h)*60000f);
                }
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
            row.Snapshot=NativeObservationSnapshot.Snapshot("pawn-health",context,pawn.GetUniqueLoadID(),w => {
                w.Write(row.Pain);w.Write(row.LifeThreatening);w.Write(row.BloodLoss);
                w.Write(row.ShouldSeekMedicalRest);w.Write(row.UrgentMedicalRest);w.Write(row.BedId??"");
                w.Write(row.HiddenHediffs);
                foreach(var h in visible.OrderBy(h=>h.def.defName,StringComparer.Ordinal)
                    .ThenBy(h=>h.Part!=null?pawn.RaceProps.body.AllParts.IndexOf(h.Part):-1)) {
                    w.Write(h.def.defName);w.Write((double)h.Severity);
                    w.Write(h.Part!=null?pawn.RaceProps.body.AllParts.IndexOf(h.Part):-1);
                }
            });
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
        // Range of the primary non-melee verb of a ranged weapon def, the
        // distance the defensive-position planner compares firing cells
        // against. Melee weapons and defs without a ranged verb report nothing.
        internal static double? WeaponRange(Thing thing)
        {
            if(!thing.def.IsRangedWeapon || thing.def.Verbs==null) return null;
            var verb=thing.def.Verbs.FirstOrDefault(v=>!v.IsMeleeAttack && v.range>0);
            if(verb==null || float.IsNaN(verb.range) || float.IsInfinity(verb.range)) return null;
            return verb.range;
        }
        private static Obs.GearItem Gear(Thing thing)
        {
            var row=new Obs.GearItem {Thing=Entity(thing),Weapon=thing.def.IsWeapon,Apparel=thing.def.IsApparel,Ranged=thing.def.IsRangedWeapon,Melee=thing.def.IsMeleeWeapon};
            NativeGearFacts.Biocode(thing, row);
            if(thing.Stuff!=null) row.Stuff=Id(thing.Stuff.defName);
            var range=WeaponRange(thing); if(range.HasValue) row.Range=range.Value;
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

        // Populates ONLY the care-policy fields (medical_care, self_tend) that
        // go/internal/bridge/work_pawns.go's validateSettings allows through
        // when a caller asks for `care`. hostility_response, the follow flags,
        // master_id and the 24-hour schedule are the wider Assign-tab row --
        // deliberately never requested via this protobuf
        // (rimgovernor/observations_list_pawns) path, so they must never be
        // set here regardless of what native happens to hold; home/pawn_config
        // and home/list_pawns reach that wider row through PawnSettingsRead's
        // own dictionary-based SettingsBlock/ScheduleBlock instead.
        // allowed_area_id belongs to the work snapshot (AllowedArea below).
        private static void CarePolicy(Pawn pawn,Obs.PawnSettings row)
        {
            var settings=pawn.playerSettings;
            if(settings==null) {
                row.Issues.Add(Missing("medical_care"));row.Issues.Add(Missing("self_tend"));
            } else {
                row.MedicalCare=settings.medCare.ToString();row.SelfTend=settings.selfTend;
            }
            row.Issues.Add(Unsupported("medical_care_options","Selectable medical care catalog is not projected."));
        }

        private static void Work(Pawn pawn,Obs.PawnSettings row)
        {
            row.WorkApplies=pawn.workSettings?.EverWork==true;
            var manual=PawnSettingsRead.ManualPriorities();
            if(manual.HasValue) row.ManualWorkPriorities=manual.Value;
            else row.Issues.Add(Missing("manual_work_priorities"));
            // GetPriority can initialize an unreadable tracker; never read it then.
            if(pawn.workSettings?.Initialized!=true || !row.WorkApplies) { row.Issues.Add(Missing("work"));return; }
            var defs=DefDatabase<WorkTypeDef>.AllDefsListForReading;Require(defs.Count);
            foreach(var def in defs) row.Work.Add(new Obs.WorkSetting {DefName=Id(def.defName),Priority=pawn.workSettings.GetPriority(def),Disabled=pawn.WorkTypeIsDisabled(def)});
        }

        // Projects the pawn's current allowed-area restriction under d.Work
        // (issue #167): NativeWorkSettings.Token commits the work snapshot to
        // CurrentAreaId and PatchPawn's AllowedArea assignment writes through
        // the same token domain, so a work-snapshot reader (buildingruntime/
        // work.WorkBoundary's readback, the recovery/area case) must see the
        // area alongside the priorities. Unset when the pawn is unrestricted,
        // matching the AllowedArea{Clear} write; Missing when the pawn has no
        // player settings at all.
        private static void AllowedArea(Pawn pawn,Obs.PawnSettings row)
        {
            var settings=pawn.playerSettings;
            if(settings==null) { row.Issues.Add(Missing("allowed_area_id")); return; }
            var area=settings.AreaRestrictionInPawnCurrentMap;
            if(area!=null) row.AllowedAreaId=Id(area.GetUniqueLoadID());
        }

        // Projects the pawn's 24-hour timetable as one TimetableSlot per hour
        // (hour 0 first), the exact list RimWorld's Pawn_TimetableTracker
        // resolves CurrentAssignment from, so the Go side can mirror the
        // schedule fence NativeMoodReliefOperations applies
        // (boundary.ExpectedScheduleDef). Pawns without a timetable tracker
        // (animals, other factions) or with an unreadable/odd-length one
        // report a "schedule" issue rather than a partial list.
        private static void Schedule(Pawn pawn,Obs.PawnSettings row)
        {
            var times=pawn.timetable?.times;
            if(times==null) { row.Issues.Add(Issue("schedule",Common.UnavailableReason.NotApplicable,"Pawn has no timetable tracker.")); return; }
            if(times.Count!=24 || times.Any(t=>t==null)) { row.Issues.Add(Missing("schedule")); return; }
            for(var hour=0;hour<times.Count;hour++) row.Schedule.Add(new Obs.TimetableSlot {Hour=(uint)hour,AssignmentDefName=Id(times[hour].defName)});
        }

        private static Obs.AnimalState Animal(Pawn pawn)
        {
            var row=new Obs.AnimalState {Gender=pawn.gender.ToString(),BodySize=Number(pawn.RaceProps.baseBodySize)};
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
        // Non-mutating: never calls Pawn_RelationsTracker.OpinionOf or anything that
        // recalculates situational social thoughts -- see PawnConfigTool's class
        // remarks for why (Thought_Situational.Notify_BecameActive deletes memories
        // of the def it produces). Ported from PawnSettingsRead.RelationsBlock/OpinionRow.
        private static Obs.PawnSocial Social(Pawn pawn,System.Collections.Generic.List<Pawn> colonists)
        {
            var row=new Obs.PawnSocial();
            var high=DefDatabase<ExpectationDef>.GetNamedSilentFail("High");
            if(high!=null)row.HighExpectations=ExpectationsUtility.CurrentExpectationFor(pawn).order>=high.order;
            var memories=PawnSettingsRead.LiveMemories(pawn);
            if(memories==null) row.Issues.Add(Missing("memories"));
            else foreach(var t in PawnSettingsRead.GroupThoughtRows(memories))
                row.Memories.Add(new Obs.Thought {DefName=t.DefName??"",Label=t.Label??"",Count=(uint)t.Count,MoodOffsetEach=Number(t.Each),MoodOffsetTotal=Number(t.Total)});

            var (situational,stale)=PawnSettingsRead.LiveSituational(pawn);
            if(situational==null) row.Issues.Add(Missing("situational"));
            else foreach(var t in PawnSettingsRead.GroupThoughtRows(situational))
                row.Situational.Add(new Obs.Thought {DefName=t.DefName??"",Label=t.Label??"",Count=(uint)t.Count,MoodOffsetEach=Number(t.Each),MoodOffsetTotal=Number(t.Total)});
            if(stale.HasValue) row.SituationalCacheStale=stale.Value;
            else row.Issues.Add(Missing("situational_cache_stale"));

            var direct=PawnSettingsRead.DirectRelationTargets(pawn);
            foreach(var other in colonists) {
                if(other==pawn) continue;
                var opinion=PawnSettingsRead.ReconstructedOpinion(pawn,other,out _);
                var relation=new Obs.Relation {Other=Entity(other),Opinion=opinion,OpinionReconstructed=true};
                if(direct.TryGetValue(other,out var defName)) relation.RelationDefName=defName;
                row.Relations.Add(relation);
            }
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
