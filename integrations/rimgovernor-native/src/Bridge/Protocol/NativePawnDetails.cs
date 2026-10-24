#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Defs = RimGovernor.Contracts.Wire.Defs;
using Obs = RimGovernor.Contracts.Wire.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    internal static class NativePawnDetails
    {
        // Doctor-side tend gates of the requested pawns, mirroring
        // NativeTendOperations.Prepare: pawn-control eligibility (the gate
        // behind "Tend requires an eligible doctor"), WorkGiver_Tend's required
        // capacities, and CanReach(ClosestTouch, Deadly) to every other
        // requested pawn -- the candidate patients.
        internal static System.Collections.Generic.List<Obs.TendDoctor> Tend(System.Collections.Generic.List<Pawn> pawns,uint reachBudgetMs)
        {
            var giver=DefDatabase<WorkGiverDef>.AllDefsListForReading
                .Where(d => d.giverClass!=null && typeof(WorkGiver_Tend).IsAssignableFrom(d.giverClass))
                .Select(d => d.Worker as WorkGiver_Tend).FirstOrDefault(w => w!=null)
                ?? throw new InvalidOperationException("WorkGiver_Tend is missing from the game's work givers.");
            // Pairwise reachability is quadratic in the pawns (about 0.3 us a pair), so
            // it runs under the brain's budget: a doctor reached after it is spent reports
            // reach_skipped, never a partial list.
            var budgetTicks=reachBudgetMs*System.Diagnostics.Stopwatch.Frequency/1000;
            var began=System.Diagnostics.Stopwatch.GetTimestamp();
            var result=new System.Collections.Generic.List<Obs.TendDoctor>(pawns.Count);
            foreach(var pawn in pawns) {
                Obs.TendReach reach;
                if(!pawn.Spawned || pawn.Dead) reach=new Obs.TendReach.Read([]);
                else if(System.Diagnostics.Stopwatch.GetTimestamp()-began>=budgetTicks) reach=new Obs.TendReach.Skipped();
                else reach=new Obs.TendReach.Read([.. pawns
                    .Where(other => !ReferenceEquals(other,pawn) && other.Spawned && !other.Dead && pawn.CanReach(other,PathEndMode.ClosestTouch,Danger.Deadly))
                    .Select(other => Id(other.GetUniqueLoadID()))]);
                result.Add(new Obs.TendDoctor { PawnId = Id(pawn.GetUniqueLoadID()), Spawned = pawn.Spawned, HasDrafter = pawn.drafter!=null,
                    ControlEligible = pawn.drafter!=null && pawn.Spawned && !pawn.Dead && !pawn.Downed && !pawn.InMentalState && pawn.IsColonistPlayerControlled,
                    CapacitiesOk = giver.MissingRequiredCapacity(pawn)==null, Reach = reach });
            }
            return result;
        }
        // One hediff row of a health read.
        private static Obs.Hediff HediffRow(Pawn pawn,Hediff h)
        {
            var immune=h.TryGetComp<HediffComp_Immunizable>();
            Obs.HediffImmunity? immunity=null;
            if(immune!=null) {
                var record=pawn.health.immunity.GetImmunityRecord(h.def);
                immunity=new Obs.HediffImmunity {
                    Immunity=Number(immune.Immunity),
                    // Include tending's severity modifier, as well as the disease's
                    // randomized native progression. Read only: never create immunity records.
                    SeverityPerDay=Number(((HediffWithComps)h).comps.OfType<HediffComp_SeverityModifierBase>().Sum(comp=>comp.SeverityChangePerDay())),
                    Gain=pawn.Dead?new Obs.ImmunityGain.Dead()
                        :record==null?new Obs.ImmunityGain.NoRecord()
                        :new Obs.ImmunityGain.PerDay(Number(GameTime.PerDay(record.ImmunityChangePerTick(pawn,true,h)))) };
            }
            // Only a hediff with a tend duration is ever tended (HediffUtility.IsTended).
            Obs.HediffTend tend=h.IsTended()
                ?new Obs.HediffTend.Tended(Number((h.TryGetComp<HediffComp_TendDuration>()
                    ?? throw new InvalidOperationException("A tended hediff has no tend duration.")).tendQuality))
                :new Obs.HediffTend.Untended();
            return new Obs.Hediff {DefName=Id(h.def.defName),Part=h.Part!=null?PartRef(pawn,h.Part,"Hediff"):null,Severity=Number(h.Severity),
                Visible=h.Visible,Tend=tend,Immunizable=immunity};
        }

        // A part of the pawn's race body; one outside it is a broken read.
        private static Obs.BodyPartRef PartRef(Pawn pawn,BodyPartRecord part,string what)
        {
            var index=pawn.RaceProps.body.AllParts.IndexOf(part);
            if(index<0) throw new InvalidOperationException(what+" body part is not in this pawn's body.");
            return new Obs.BodyPartRef { Index=index, DefName=Id(part.def.defName) };
        }

        // The need levels; a need the pawn lacks is Lacks.
        internal static Obs.PawnNeeds Needs(Pawn pawn)
        {
            var food = pawn.needs?.food;
            return new Obs.PawnNeeds
            {
                Food = food != null ? new Obs.FoodNeed.Has(Number(food.CurLevelPercentage), (Defs.HungerCategory)(int)food.CurCategory) : new Obs.FoodNeed.Lacks(),
                Rest = Level(pawn.needs?.rest),
                Mood = Level(pawn.needs?.mood),
                Joy = Level(pawn.needs?.joy),
                // MentalBreaker.BreakThresholdMinor is this stat; a corpse has no mind state but its stat reads.
                BreakThresholdMinor = Number(pawn.GetStatValue(StatDefOf.MentalBreakThreshold, true, 5)),
                PsylinkLevel = ModsConfig.RoyaltyActive ? pawn.GetPsylinkLevel() : 0,
            };
        }

        private static Obs.NeedLevel Level(Need? need) =>
            need != null ? new Obs.NeedLevel.Has(Number(need.CurLevelPercentage)) : new Obs.NeedLevel.Lacks();

        // Medical care cap inputs: the life threat and every hediff, none filtered.
        internal static Obs.PawnConditions Conditions(Pawn pawn)
        {
            var all=pawn.health.hediffSet.hediffs;
            return new Obs.PawnConditions { LifeThreatening = all.Any(h=>h.IsCurrentlyLifeThreatening), Hediffs = [.. all.Select(h=>HediffRow(pawn,h))] };
        }

        internal static Obs.PawnHealth Health(Pawn pawn,Common.ObservationContext context)
        {
            var set=pawn.health.hediffSet;
            var all=set.hediffs.ToList();
            var bleeding=set.BleedRateTotal>0;
            Obs.BleedOut bleedOut=new Obs.BleedOut.NotBleedingOut();
            if(bleeding && !pawn.Dead) {
                var ticks=HealthUtility.TicksUntilDeathDueToBloodLoss(pawn);
                if(ticks>0 && ticks<int.MaxValue) bleedOut=new Obs.BleedOut.Within(GameTime.Hours((double)ticks));
            }
            var lifeThreatening=all.Any(h=>h.IsCurrentlyLifeThreatening);
            var bloodLoss=Number(all.Where(h=>h.def==HediffDefOf.BloodLoss).Sum(h=>(double)h.Severity));
            var seekRest=HealthAIUtility.ShouldSeekMedicalRest(pawn);
            var bedId=pawn.CurrentBed() is { } bed ? Id(bed.GetUniqueLoadID()) : null;
            return new Obs.PawnHealth { SummaryFraction=Number(pawn.health.summaryHealth.SummaryHealthPercent), NeedsTend=pawn.health.HasHediffsNeedingTend(false),
                BleedRatePerDay=Number(set.BleedRateTotal), Bleeding=bleeding, BleedOut=bleedOut,
                LifeThreatening=lifeThreatening, BloodLoss=bloodLoss,
                ShouldSeekMedicalRest=seekRest, Hediffs=[.. all.Select(h=>HediffRow(pawn,h))], Surgery=Surgery(pawn), BedId=bedId,
                Snapshot=NativeObservationSnapshot.Snapshot("pawn-health",context,pawn.GetUniqueLoadID(),w => {
                    w.Write(lifeThreatening);w.Write(bloodLoss);
                    w.Write(seekRest);w.Write(bedId??"");
                    foreach(var h in all.OrderBy(h=>h.def.defName,StringComparer.Ordinal)
                        .ThenBy(h=>h.Part!=null?pawn.RaceProps.body.AllParts.IndexOf(h.Part):-1)) {
                        w.Write(h.def.defName);w.Write((double)h.Severity);
                        w.Write(h.Part!=null?pawn.RaceProps.body.AllParts.IndexOf(h.Part):-1);
                    }
                }) };
        }

        // Surgery facts, all through vanilla: recipe discovery from the
        // retired home/medical_operations tool, success chance from the recipe's
        // own SurgeryOutcomeEffectDef comps. The brain never recomputes any of it.
        // Absent for a dead pawn or one off every map.
        internal static Obs.PawnSurgery? Surgery(Pawn pawn)
        {
            var map=pawn.MapHeld;
            if(pawn.Dead || map==null) return null;
            var bills=pawn.BillStack.Bills.Select(bill => new Obs.SurgeryBill {Id=Id(bill.GetUniqueLoadID()),Recipe=Id(bill.recipe.defName),
                Part=bill is Bill_Medical { Part: { } part } ? PartRef(pawn,part,"Surgery") : null}).ToList();
            var missingParts=pawn.health.hediffSet.GetMissingPartsCommonAncestors().Where(m => m.Part!=null)
                .Select(m => new Obs.MissingBodyPart {Part=PartRef(pawn,m.Part,"Missing")}).ToList();
            var operations=new System.Collections.Generic.List<Obs.SurgeryOperation>();
            var facts=SurgeryScope.Facts(map);
            var bestBed=facts.BestBed; var bedFactor=facts.BedFactor; var idealFactor=facts.IdealFactor;
            foreach(var def in pawn.def.AllRecipes) {
                if(!def.AvailableNow || def.Worker==null || !def.Worker.AvailableReport(pawn).Accepted) continue;
                var targets=def.targetsBodyPart?def.Worker.GetPartsToApplyOn(pawn,def).ToList()
                    :new System.Collections.Generic.List<BodyPartRecord?>{null};
                // Everything but the part is the recipe's: read once for all its parts.
                var shared=false;
                System.Collections.Generic.List<Pawn> doctors=null!; ThingDef? medicine=null; bool others=false, usesMedicine=false, careLimited=false;
                foreach(var part in targets) {
                    if(!def.AvailableOnNow(pawn,part)) continue;
                    if(!shared) {
                        shared=true;
                        var recipe=facts.Recipe(def,map);
                        doctors=recipe.Doctors.Contains(pawn)?recipe.Doctors.Where(p => p!=pawn).ToList():recipe.Doctors;
                        medicine=facts.Medicine(pawn,def,true);
                        others=recipe.Others;
                        usesMedicine=recipe.UsesMedicine;
                        careLimited=others && usesMedicine && medicine==null && facts.Medicine(pawn,def,false)!=null;
                    }
                    var kind=Kind(pawn,def,part);
                    Obs.SurgeryYield? yield=null; string? addedPart=null;
                    if(kind==Obs.SurgeryKind.Harvest && part?.def.spawnThingOnRemoved is { } organ)
                        yield=new Obs.SurgeryYield { ThingDef=Id(organ.defName), MarketValue=Number(organ.BaseMarketValue) };
                    // Artificial part removal: the added part on the
                    // target and the thing its removal spawns.
                    if(def.Worker is Recipe_RemoveBodyPart && part!=null) {
                        var added=pawn.health.hediffSet.hediffs.OfType<Hediff_AddedPart>().FirstOrDefault(h => h.Part==part);
                        if(added!=null) {
                            addedPart=Id(added.def.defName);
                            if(added.def.spawnThingOnRemoved is { } spawn) yield=new Obs.SurgeryYield { ThingDef=Id(spawn.defName), MarketValue=Number(spawn.BaseMarketValue) };
                        }
                    }
                    double medicineValue=0;
                    if(medicine!=null) {
                        var count=def.ingredients.Where(i => i.filter.Allows(medicine)).Sum(i => i.CountRequiredOfFor(medicine,def));
                        if(count>0) medicineValue=Number(medicine.BaseMarketValue*count);
                    }
                    Obs.SurgeryChance chance;
                    if(def.surgeryOutcomeEffect==null) chance=new Obs.SurgeryChance.NoOutcomeEffect();
                    else if(doctors.Count==0) chance=new Obs.SurgeryChance.NoEligibleDoctor();
                    else {
                        float? real=pawn.InBed()?(float?)null:bestBed?.GetStatValue(StatDefOf.SurgerySuccessChanceFactor) ?? 1f;
                        // each doctor's chance, so training can name one.
                        var chances=new System.Collections.Generic.Dictionary<string,double>();
                        foreach(var d in doctors) chances[d.GetUniqueLoadID()]=Number(Chance(def,d,pawn,part,medicine,real));
                        chance=new Obs.SurgeryChance.Rated(chances.Values.Max(),Number(doctors.Max(d => Chance(def,d,pawn,part,medicine,idealFactor))),chances);
                    }
                    operations.Add(new Obs.SurgeryOperation {Recipe=Id(def.defName),Part=part!=null?PartRef(pawn,part,"Surgery"):null,Kind=kind,
                        Chance=chance,
                        EligibleDoctors=doctors.Count,
                        IngredientsOnMap=others && (!usesMedicine || medicine!=null),
                        // medicine is stocked but the care level forbids all of it.
                        MedicineCareLimited=careLimited,
                        Violation=def.Worker.IsViolationOnPawn(pawn,part,Faction.OfPlayer),Lethal=Lethal(pawn,def,part),
                        Yield=yield, AddedPartHediff=addedPart, MedicineMarketValue=medicineValue});
                }
            }
            return new Obs.PawnSurgery { SurgeryBills=[.. bills], MissingParts=[.. missingParts], Operations=[.. operations] };
        }

        // #3830: an install recipe on a part as if it were missing, read as Surgery reads an operation (a patient not yet in bed is
        // scored in the colony's best medical bed), with the outcome model a queued bill's demand carries and the best eligible
        // doctor's quality before the bed and the clamp (NativeSupportActivities).
        internal static Obs.OrganReplacementRead Replacement(Pawn pawn,RecipeDef def,BodyPartRecord part,MapFacts facts,Map map)
        {
            var recipe=facts.Recipe(def,map);
            var doctors=recipe.Doctors.Where(p => p!=pawn).ToList();
            var medicine=facts.Medicine(pawn,def,true);
            var row=new Obs.OrganReplacementRead(part.def.defName,def.defName) {
                IngredientsOnMap=recipe.Others && (!recipe.UsesMedicine || medicine!=null), EligibleDoctors=doctors.Count,
                Violation=def.Worker.IsViolationOnPawn(pawn,part,Faction.OfPlayer), Lethal=Lethal(pawn,def,part) };
            var bill=new Bill_Medical {recipe=def};
            if(medicine!=null) bill.consumedMedicine[medicine]=1;
            if(def.surgeryOutcomeEffect!=null && doctors.Count>0) {
                var real=facts.BestBed==null?1f:facts.BedFactor;
                row = row with { SuccessChance = doctors.Max(d => Number(Chance(def,d,pawn,part,medicine,real))),
                    DoctorSuccessChance = doctors.Max(d => Number(Chance(def,d,pawn,part,medicine,facts.IdealFactor))),
                    Quality = doctors.Max(d => NativeSupportActivities.SurgeryQualityOf(d,pawn,def,part,bill)) };
            }
            var (outcomes,min,max,environment)=NativeSupportActivities.SurgeryModel(pawn,def,part,true);
            if(outcomes!=null) row = row with { Outcomes = [.. outcomes] };
            return row with { QualityMin = min, QualityMax = max, Environment = environment };
        }

        // The best-potency medicine on the map that the recipe allows and,
        // when care is set, the patient's care policy too; null when none.
        private static ThingDef? MedicineFor(Pawn pawn,RecipeDef def,System.Collections.Generic.List<Thing> stock,bool care)
            => stock
                .Where(t => (!care || pawn.playerSettings==null || pawn.playerSettings.medCare.AllowsMedicine(t.def))
                    && def.ingredients.Any(i => i.filter.Allows(t)))
                .Select(t => t.def).OrderByDescending(d => d.GetStatValueAbstract(StatDefOf.MedicalPotency)).FirstOrDefault();

        // The pawn-independent surgery facts of one capture: the
        // unforbidden medicine stock, the best medical bed, and per recipe
        // its eligible doctors and ingredient availability. Table() opens a
        // scope so every pawn of the frame shares them; a lone Surgery call
        // gets a throwaway one, so a read is always from the current map.
        internal sealed class SurgeryScope : IDisposable
        {
            [ThreadStatic] private static SurgeryScope? current;
            private readonly SurgeryScope? previous;
            private readonly System.Collections.Generic.Dictionary<Map,MapFacts> maps=new System.Collections.Generic.Dictionary<Map,MapFacts>();

            internal SurgeryScope() {previous=current;current=this;}
            public void Dispose() {current=previous;}

            internal static MapFacts Facts(Map map)
            {
                var scope=current;
                if(scope==null) return new MapFacts(map);
                if(!scope.maps.TryGetValue(map,out var facts)) scope.maps[map]=facts=new MapFacts(map);
                return facts;
            }
        }

        internal sealed class RecipeFacts
        {
            internal System.Collections.Generic.List<Pawn> Doctors=null!;
            internal bool Others, UsesMedicine;
        }

        internal sealed class MapFacts
        {
            internal readonly Building_Bed? BestBed; internal readonly float BedFactor, IdealFactor;
            private readonly System.Collections.Generic.List<Pawn> colonists;
            private readonly System.Collections.Generic.List<Thing> stock;
            private readonly System.Collections.Generic.Dictionary<RecipeDef,RecipeFacts> recipes=new System.Collections.Generic.Dictionary<RecipeDef,RecipeFacts>();
            private readonly System.Collections.Generic.Dictionary<RecipeDef,System.Collections.Generic.Dictionary<int,ThingDef?>> medicines=new System.Collections.Generic.Dictionary<RecipeDef,System.Collections.Generic.Dictionary<int,ThingDef?>>();

            internal MapFacts(Map map)
            {
                colonists=map.mapPawns.FreeColonistsSpawned.ToList();
                stock=map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine)
                    .Where(t => !t.IsForbidden(Faction.OfPlayer) && !t.Position.Fogged(map)).ToList();
                foreach(var bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>()) {
                    if(!bed.Medical) continue;
                    var f=bed.GetStatValue(StatDefOf.SurgerySuccessChanceFactor);
                    if(BestBed==null || f>BedFactor) {BestBed=bed;BedFactor=f;}
                }
                // The ideal bed and room: a plain Bed's factor off the map
                // (as if clean, roofed and lit), never below the neutral 1.
                IdealFactor=System.Math.Max(1f,ThingDefOf.Bed.GetStatValueAbstract(StatDefOf.SurgerySuccessChanceFactor,GenStuff.DefaultStuffFor(ThingDefOf.Bed)));
            }

            // Doctors include every eligible colonist; the caller drops the patient.
            internal RecipeFacts Recipe(RecipeDef def,Map map)
            {
                if(recipes.TryGetValue(def,out var facts)) return facts;
                facts=new RecipeFacts {
                    Doctors=colonists.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                        && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) && def.PawnSatisfiesSkillRequirements(p)).ToList(),
                    Others=!def.PotentiallyMissingIngredients(null,map).Any(),
                    UsesMedicine=def.ingredients.Any(i => i.filter.AllowedThingDefs.Any(d => d.IsMedicine)) };
                recipes[def]=facts;
                return facts;
            }

            internal ThingDef? Medicine(Pawn pawn,RecipeDef def,bool care)
            {
                // The care policy is the only pawn input: key by its category.
                var key=!care ? 0 : pawn.playerSettings==null ? 1 : 2+(int)pawn.playerSettings.medCare;
                if(!medicines.TryGetValue(def,out var byCare)) medicines[def]=byCare=new System.Collections.Generic.Dictionary<int,ThingDef?>();
                if(!byCare.TryGetValue(key,out var medicine)) byCare[key]=medicine=MedicineFor(pawn,def,stock,care);
                return medicine;
            }
        }

        // Vanilla SurgeryOutcomeEffectDef.GetQuality, except a patient not yet
        // in bed is scored in the best colony medical bed it will lie in.
        // bedFactor, when set, replaces the bed and room factor (the best
        // medical bed's, or the ideal one's); null keeps vanilla's.
        private static double Chance(RecipeDef def,Pawn surgeon,Pawn patient,BodyPartRecord? part,ThingDef? medicine,float? bedFactor)
        {
            var bill=new Bill_Medical {recipe=def};
            if(medicine!=null) bill.consumedMedicine[medicine]=1;
            var ingredients=new System.Collections.Generic.List<Thing>();
            float quality=1f;
            foreach(var comp in def.surgeryOutcomeEffect.comps ?? new System.Collections.Generic.List<SurgeryOutcomeComp>()) {
                if(comp is SurgeryOutcomeComp_BedAndRoomQuality && bedFactor.HasValue) {
                    if(!def.surgeryIgnoreEnvironment) quality*=bedFactor.Value;
                    continue;
                }
                if(comp.Affects(def,surgeon,patient,part)) comp.AffectQuality(def,surgeon,patient,ingredients,part,bill,ref quality);
            }
            return quality;
        }

        private static Obs.SurgeryKind Kind(Pawn pawn,RecipeDef def,BodyPartRecord? part)
        {
            var worker=def.Worker;
            if(worker is Recipe_RemoveBodyPart)
                return part!=null && HealthUtility.PartRemovalIntent(pawn,part)==BodyPartRemovalIntent.Harvest
                    && !pawn.health.hediffSet.HasDirectlyAddedPartFor(part)?Obs.SurgeryKind.Harvest:Obs.SurgeryKind.Amputate;
            if(worker is Recipe_InstallArtificialBodyPart || worker is Recipe_InstallNaturalBodyPart)
                return part!=null && pawn.health.hediffSet.PartIsMissing(part)?Obs.SurgeryKind.Restore:Obs.SurgeryKind.Install;
            if(worker is Recipe_InstallImplant) return Obs.SurgeryKind.Install;
            if(def.removesHediff!=null || worker is Recipe_RemoveHediff) return Obs.SurgeryKind.Cure;
            return Obs.SurgeryKind.Other;
        }

        private static bool Lethal(Pawn pawn,RecipeDef def,BodyPartRecord? part)
        {
            if(def.Worker is Recipe_ExecuteByCut) return true;
            if(def.Worker is Recipe_RemoveBodyPart && part!=null)
                return pawn.health.WouldDieAfterAddingHediff(HediffDefOf.MissingBodyPart,part,1f);
            return def.addsHediff!=null && pawn.health.WouldDieAfterAddingHediff(def.addsHediff,part,def.addsHediff.initialSeverity);
        }

        internal static Obs.PawnEquipment Equipment(Pawn pawn)
        {
            var row=NativeGearFacts.Equipment(pawn);
            // Defense capacity: the ranged hit factor the observed damage per second counts for (#3845).
            if(pawn.equipment!=null) row = row with { RangedHitFactor = NativeDefenseStats.PawnRangedHitFactor(pawn) };
            return row;
        }

        private static readonly WorkTags[] SingleWorkTags=((WorkTags[])Enum.GetValues(typeof(WorkTags)))
            .Where(tag => tag!=WorkTags.None && ((int)tag&((int)tag-1))==0).ToArray();

        internal static Obs.PawnBiography Biography(Pawn pawn)
        {
            var row=new Obs.PawnBiography { BiologicalAgeYears = Number(pawn.ageTracker.AgeBiologicalYearsFloat), Traits = [], Skills = [], SkillStatCurves = [], SkillStatFindings = [], DisabledWorkTags = [], IncapableWorkTypes = [] };
            // A pawn without a story or skill tracker (an animal) has no traits or skills.
            if(pawn.story?.traits!=null)
                foreach(var trait in pawn.story.traits.allTraits) row = row with { Traits = [.. row.Traits, new Obs.Trait {DefName=Id(trait.def.defName),Degree=trait.Degree}] };
            if(pawn.skills!=null)
            {
                foreach(var skill in pawn.skills.skills)
                    row = row with { Skills = [.. row.Skills, new Obs.Skill {Learning=NativeSkillLearning.Read(pawn,skill),DefName=Id(skill.def.defName),Level=skill.Level,StoredLevel=skill.levelInt,Passion=NativeEnums.Passion(skill.passion),Disabled=skill.TotallyDisabled}] };
                row = NativeSkillStat.Curves(pawn,row);
            }
            var tags=pawn.CombinedDisabledWorkTags;
            foreach(var tag in SingleWorkTags) if((tags&tag)!=0) row = row with { DisabledWorkTags = [.. row.DisabledWorkTags, tag.ToString()] };
            foreach(var def in DefDatabase<WorkTypeDef>.AllDefsListForReading) if(pawn.WorkTypeIsDisabled(def)) row = row with { IncapableWorkTypes = [.. row.IncapableWorkTypes, Id(def.defName)] };
            return row;
        }

        // The pawn's player settings, every member read for every pawn.
        internal static Obs.PawnSettings Settings(Pawn pawn)
        {
            var settings=pawn.playerSettings;
            return new Obs.PawnSettings {
                Care = Care(pawn),
                // The current-map restriction WorkSettingsIntent writes alongside the priorities;
                // null when unrestricted (the AllowedArea{Clear} write) or without player settings.
                AllowedAreaId = settings?.AreaRestrictionInPawnCurrentMap is Area area ? Id(area.GetUniqueLoadID()) : null,
                Work = Work(pawn),
                WorkApplies = pawn.workSettings?.EverWork==true,
                ManualWorkPriorities = Manual(pawn),
                Schedule = Schedule(pawn),
                FoodPolicyRespected = NativeFoodPolicy.Respected(pawn),
                PolicyInputs = NativePolicyFacts.Inputs(pawn),
                Body = BodyStats(pawn),
            };
        }

        // The care policy PawnSettingsIntent writes; none without player settings.
        private static Obs.PawnCare Care(Pawn pawn)
        {
            var settings=pawn.playerSettings;
            if(settings==null) return new Obs.PawnCare.None();
            return new Obs.PawnCare.Configured(NativeEnums.Care(settings.medCare), settings.selfTend,
                NativeEnums.Hostility(settings.hostilityResponse));
        }

        // One row per work type for a working pawn with an initialized tracker; GetPriority can initialize an
        // unread tracker, so it is never read then.
        private static RimGovernor.Brain.Domain.EquatableArray<Obs.WorkSetting> Work(Pawn pawn)
        {
            var work=pawn.workSettings;
            if(work==null || !work.EverWork || !work.Initialized) return [];
            return [.. DefDatabase<WorkTypeDef>.AllDefsListForReading
                .Select(def => new Obs.WorkSetting {DefName=Id(def.defName),Priority=work.GetPriority(def),Disabled=pawn.WorkTypeIsDisabled(def)})];
        }

        // The game's manual-priorities setting, for a working pawn with an initialized tracker; null otherwise.
        private static bool? Manual(Pawn pawn)
        {
            var work=pawn.workSettings;
            if(work==null || !work.EverWork || !work.Initialized) return null;
            return PawnSettingsRead.ManualPriorities() ?? throw new InvalidOperationException("A pawn read has no game play settings.");
        }

        // The body stats the outdoor dash planner reads: the game's own stat
        // and hediff values, at the pawn's cell or its holder's.
        private static Obs.PawnBodyStats BodyStats(Pawn pawn)
        {
            var hypothermia=0.0;
            foreach(var h in pawn.health.hediffSet.hediffs) if(h.def==HediffDefOf.Hypothermia) hypothermia=Math.Max(hypothermia,h.Severity);
            return new Obs.PawnBodyStats {
                ComfortableMinC=Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)),
                ComfortableMaxC=Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax)),
                MoveSpeed=Number(pawn.GetStatValue(StatDefOf.MoveSpeed)),
                CellTemperatureC=Number(pawn.AmbientTemperature),
                Hypothermia=Number(hypothermia) };
        }

        // The pawn's 24-hour timetable as one TimetableSlot per hour (hour 0
        // first), the exact list RimWorld's Pawn_TimetableTracker resolves
        // CurrentAssignment from. A pawn without a timetable (an animal,
        // another faction's pawn) has no slots.
        private static RimGovernor.Brain.Domain.EquatableArray<Obs.TimetableSlot> Schedule(Pawn pawn)
        {
            var times=pawn.timetable?.times;
            if(times==null) return [];
            if(times.Count!=24 || times.Any(t=>t==null)) throw new InvalidOperationException("The pawn's timetable is not 24 assigned hours.");
            return [.. times.Select((t,hour) => new Obs.TimetableSlot {Hour=(uint)hour,AssignmentDefName=Id(t.defName)})];
        }
        // The one animal state builder: body, herd and training
        // facts for every animal; for a player animal its pen, area, care
        // and the release/slaughter guards; for a wild one its tame facts.
        internal static Obs.AnimalState Animal(Pawn pawn)
        {
            var training=new List<Obs.TrainingEntry>();
            if(pawn.training!=null) {
                foreach(var def in DefDatabase<TrainableDef>.AllDefsListForReading) {
                    var report=pawn.training.CanAssignToTrain(def,out var visible);
                    training.Add(new Obs.TrainingEntry {DefName=Id(def.defName),Learned=pawn.training.HasLearned(def),Wanted=pawn.training.GetWanted(def),Available=report.Accepted && visible});
                }
            }
            var designations=pawn.MapHeld?.designationManager?.AllDesignationsOn(pawn).ToList() ?? [];
            var player=pawn.Faction!=null && pawn.Faction==Faction.OfPlayerSilentFail;
            // Live spawned player animals only: the guards and pen reads assume one.
            var live=player && !pawn.Dead && pawn.Spawned;
            var eligible=live && NativeHusbandryOperations.Eligible(pawn);
            var roped=live && AnimalPenUtility.NeedsToBeManagedByRope(pawn);
            var pen=roped ? AnimalPenUtility.GetCurrentPenOf(pawn,false) : null;
            return new Obs.AnimalState {
                Gender=(RimGovernor.Contracts.Wire.Defs.Gender)(int)pawn.gender,
                AgeYears=pawn.ageTracker.AgeBiologicalYearsFloat,
                Adult=pawn.ageTracker.Adult,
                Pregnancy=NativeHusbandryOperations.Pregnancy(pawn),
                Downed=pawn.Downed,
                InMentalState=pawn.InMentalState,
                LifeStageIndex=pawn.ageTracker.CurLifeStageIndex,
                NextLifeStage=NativeHusbandryOperations.NextLifeStage(pawn),
                Training=[.. training],
                Slaughter=designations.Any(d=>d.def==DesignationDefOf.Slaughter),
                Release=designations.Any(d=>d.def==DesignationDefOf.ReleaseAnimalToWild),
                Tame=designations.Any(d=>d.def==DesignationDefOf.Tame),
                Tameable=NativeHusbandryOperations.Tameable(pawn),
                Bonded=pawn.relations?.DirectRelations.Any(r => r.def==PawnRelationDefOf.Bond && r.otherPawn!=null && !r.otherPawn.Dead)==true,
                Sterilized=NativeHusbandryOperations.Sterilized(pawn),
                Sick=pawn.health.hediffSet.AnyHediffMakesSickThought,
                SterilizeQueued=NativeHusbandryOperations.SterilizeBill(pawn)!=null,
                Venerated=NativeHusbandryOperations.Venerated(pawn),
                SafeToRelease=eligible && NativeHusbandryOperations.SafeToRelease(pawn),
                ColonistBonded=pawn.relations!=null && TrainableUtility.GetAllColonistBondsFor(pawn).Any(),
                SlaughterDesignatable=eligible && new Designator_Slaughter().CanDesignateThing(pawn).Accepted,
                FollowDrafted=pawn.playerSettings?.followDrafted ?? false,
                FollowFieldwork=pawn.playerSettings?.followFieldwork ?? false,
                Obedient=NativeHusbandryOperations.Obedient(pawn),
                BondedPawnIds=live ? [.. pawn.relations?.DirectRelations.Where(r => r.def==PawnRelationDefOf.Bond && r.otherPawn!=null && !r.otherPawn.Dead && r.otherPawn.RaceProps.Humanlike).Select(r => r.otherPawn.GetUniqueLoadID()).OrderBy(i => i,System.StringComparer.Ordinal) ?? Enumerable.Empty<string>()] : [],
                PenId=pen!=null ? Id(pen.parent.GetUniqueLoadID()) : null,
                MasterId=NativeHusbandryOperations.MasterId(pawn),
                SupportsAllowedAreas=NativeHusbandryOperations.SupportsAllowedAreas(pawn),
            };
        }
        // Non-mutating: never calls Pawn_RelationsTracker.OpinionOf or anything that
        // recalculates situational social thoughts -- see PawnConfigTool's class
        // remarks for why (Thought_Situational.Notify_BecameActive deletes memories
        // of the def it produces). Ported from PawnSettingsRead.RelationsBlock/OpinionRow.
        internal static Obs.PawnSocial Social(Pawn pawn)
        {
            var current=ExpectationsUtility.CurrentExpectationFor(pawn) ?? throw new InvalidOperationException("The pawn has no current expectation.");
            var (situational,stale)=PawnSettingsRead.LiveSituational(pawn);
            return new Obs.PawnSocial
            {
                HighExpectations = current.order>=ExpectationDefOf.High.order,
                ExpectationDefName = Id(current.defName),
                Memories = [.. PawnSettingsRead.GroupThoughtRows(PawnSettingsRead.LiveMemories(pawn)).Select(t => ThoughtRow(t.DefName,t.Count,t.Total))],
                Situational = [.. PawnSettingsRead.GroupThoughtRows(situational).Select(t => ThoughtRow(t.DefName,t.Count,t.Total))],
                SituationalCacheStale = stale,
            };
        }

        private static Obs.Thought ThoughtRow(string? defName,int count,double total)=>
            new Obs.Thought {DefName=defName ?? throw new InvalidOperationException("A thought has no def."),MoodOffsetTotal=Number(total)};

    }
}
