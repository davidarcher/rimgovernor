#nullable enable
using System;
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
        // The detail families one request resolves to: an absent family of
        // the first seven is requested, an absent work, schedule or tend is not.
        internal sealed class Requested
        {
            internal bool Needs, Health, Equipment, Biography, Settings, Social, Animals, Work, Schedule, Tend;
            internal uint? TendReachBudgetMs;
        }

        internal static Requested Defaults(Obs.PawnDetails? source) => new Requested {
            Needs=source?.Needs ?? true, Health=source?.Health ?? true, Equipment=source?.Equipment ?? true,
            Biography=source?.Biography ?? true, Settings=source?.Settings ?? true, Social=source?.Social ?? true,
            Animals=source?.Animals ?? true, Work=source?.Work ?? false, Schedule=source?.Schedule ?? false,
            Tend=source?.Tend ?? false, TendReachBudgetMs=source?.TendReachBudgetMs };

        // Doctor-side tend gates for one reply's rows, mirroring
        // NativeTendOperations.Prepare: pawn-control eligibility (the gate
        // behind "Tend requires an eligible doctor"), WorkGiver_Tend's required
        // capacities, the Doctor work type, and CanReach(ClosestTouch, Deadly)
        // to every other row of the same reply -- the candidate patients.
        internal static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>> Tend(System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>> page,uint? reachBudgetMs)
        {
            var giver=DefDatabase<WorkGiverDef>.AllDefsListForReading
                .Where(d => d.giverClass!=null && typeof(WorkGiver_Tend).IsAssignableFrom(d.giverClass))
                .Select(d => d.Worker as WorkGiver_Tend).FirstOrDefault(w => w!=null)
                ?? throw new InvalidOperationException("WorkGiver_Tend is missing from the game's work givers.");
            // Pairwise reachability is quadratic in the page (about 0.3 us a pair), so
            // it runs under the brain's budget: a doctor reached after it is spent reports
            // reach_skipped (list unknown), never a partial list.
            var budgetTicks=reachBudgetMs.HasValue ? reachBudgetMs.Value*System.Diagnostics.Stopwatch.Frequency/1000 : long.MaxValue;
            var began=System.Diagnostics.Stopwatch.GetTimestamp();
            var result=new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>>(page.Count);
            foreach(var item in page) {
                var pawn=item.Key;
                var row=new Obs.PawnTendDoctor { Spawned = pawn.Spawned, HasDrafter = pawn.drafter!=null,
                    ControlEligible = pawn.drafter!=null && pawn.Spawned && !pawn.Dead && !pawn.Downed && !pawn.InMentalState && pawn.IsColonistPlayerControlled,
                    CapacitiesOk = giver.MissingRequiredCapacity(pawn)==null };
                if(!pawn.Spawned || pawn.Dead) {
                    result.Add(new System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>(pawn,item.Value with { TendDoctor = row }));
                    continue;
                }
                if(System.Diagnostics.Stopwatch.GetTimestamp()-began>=budgetTicks) {row = row with { ReachSkipped = true };result.Add(new System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>(pawn,item.Value with { TendDoctor = row }));continue;}
                foreach(var other in page) {
                    if(ReferenceEquals(other.Key,pawn) || !other.Key.Spawned || other.Key.Dead) continue;
                    if(pawn.CanReach(other.Key,PathEndMode.ClosestTouch,Danger.Deadly)) row = row with { ReachablePawnIds = [.. row.ReachablePawnIds, (other.Value.Pawn.Id ?? "")] };
                }
                result.Add(new System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>(pawn,item.Value with { TendDoctor = row }));
            }
            return result;
        }

        internal static Obs.PawnState Apply(Pawn pawn,Obs.PawnState row,Obs.PawnDetails? requested,Common.ObservationContext context)
        {
            var d=Defaults(requested);
            // An unrequested family is an absent block.
            if(d.Needs) row = row with { Needs = Needs(pawn) };
            if(d.Health) row = row with { Health = Health(pawn,context) };
            if(d.Equipment) row = row with { Equipment = Equipment(pawn) };
            if(d.Biography) row = row with { Biography = Biography(pawn) };
            if(d.Settings || d.Work || d.Schedule) {
                var settings = new Obs.PawnSettings();
                // The bridge contract (the brain's settings
                // validation) enforces that a PawnSettings reply carries
                // ONLY the fields the request actually asked for: care policy
                // (medical_care, self_tend, hostility_response) under
                // d.Settings, work priorities
                // plus the allowed area under d.Work, the 24 timetable slots
                // under d.Schedule.
                // Callers that request several (e.g. ReadTendPawns,
                // ReadRoundsPawns) must therefore get exactly their union,
                // never the wider Assign-tab row (follow
                // flags, master, allowed area) that home/pawn_config's own
                // PawnSettingsRead.SettingsBlock reports instead.
                if(d.Settings) settings = CarePolicy(pawn,settings);
                if(d.Work) { settings = Work(pawn,settings); settings = AllowedArea(pawn,settings); settings = settings with { FoodRestriction = NativeFoodPolicy.Read(pawn), PolicyInputs = NativePolicyFacts.Inputs(pawn) }; settings = BodyStats(pawn,settings); }
                if(d.Schedule) settings = Schedule(pawn,settings);
                row = row with { Settings = settings };
            }
            if(d.Social) row = row with { Social = Social(pawn) };
            if(d.Animals && pawn.RaceProps.Animal) row = row with { AnimalState = Animal(pawn) };
            return row;
        }

        // One hediff row of a health read.
        private static Obs.Hediff HediffRow(Pawn pawn,Hediff h)
        {
                var item=new Obs.Hediff {DefName=Id(h.def.defName),Severity=Number(h.Severity),
                    Visible=h.Visible,Tended=h.IsTended()};
                if(h.Part!=null) {
                    var index=pawn.RaceProps.body.AllParts.IndexOf(h.Part);
                    if(index<0) throw new InvalidOperationException("Hediff body part is not in this pawn's body.");
                    item = item with { PartIndex = index };item = item with { PartDefName = Id(h.Part.def.defName) };
                }
                var immune=h.TryGetComp<HediffComp_Immunizable>();
                if(immune!=null) {
                    item = item with { Immunity = Number(immune.Immunity) };
                    // Include tending's severity modifier, as well as the disease's
                    // randomized native progression. Read only: never create immunity records.
                    item = item with { SeverityPerDay = Number(((HediffWithComps)h).comps
                        .OfType<HediffComp_SeverityModifierBase>().Sum(comp=>comp.SeverityChangePerDay())) };
                    var record=pawn.health.immunity.GetImmunityRecord(h.def);
                    if(record!=null && !pawn.Dead)
                        item = item with { ImmunityPerDay = Number(GameTime.PerDay(record.ImmunityChangePerTick(pawn,true,h))) };
                }
                var tend=h.TryGetComp<HediffComp_TendDuration>();
                if(tend!=null && item.Tended) item = item with { TendQuality = Number(tend.tendQuality) };
                return item;
            }

        // The need levels; a need the pawn lacks is absent.
        private static Obs.PawnNeeds Needs(Pawn pawn)
        {
            var needs=new Obs.PawnNeeds { BreakThresholdMinor = Number(pawn.mindState.mentalBreaker.BreakThresholdMinor) };
            if(pawn.needs?.mood!=null) needs = needs with { Mood = Number(pawn.needs.mood.CurLevelPercentage) };
            if(pawn.needs?.food!=null) needs = needs with { Food = Number(pawn.needs.food.CurLevelPercentage), HungerCategory = (Defs.HungerCategory)(int)pawn.needs.food.CurCategory };
            if(pawn.needs?.rest!=null) needs = needs with { Rest = Number(pawn.needs.rest.CurLevelPercentage) };
            if(pawn.needs?.joy!=null) needs = needs with { Joy = Number(pawn.needs.joy.CurLevelPercentage) };
            if(ModsConfig.RoyaltyActive && pawn.psychicEntropy!=null && pawn.GetPsylinkLevel() is var psylink && psylink>0)
                needs = needs with { Psyfocus = new Obs.PawnPsyfocus { PsylinkLevel = psylink } };
            return needs;
        }

        // Medical care cap inputs: the life threat and every hediff, none filtered.
        internal static Obs.PawnConditions Conditions(Pawn pawn)
        {
            var all=pawn.health.hediffSet.hediffs;
            return new Obs.PawnConditions { LifeThreatening = all.Any(h=>h.IsCurrentlyLifeThreatening), Hediffs = [.. all.Select(h=>HediffRow(pawn,h))] };
        }

        private static Obs.PawnHealth Health(Pawn pawn,Common.ObservationContext context)
        {
            var set=pawn.health.hediffSet;
            var all=set.hediffs.ToList();
            var row=new Obs.PawnHealth { SummaryFraction=Number(pawn.health.summaryHealth.SummaryHealthPercent), NeedsTend=pawn.health.HasHediffsNeedingTend(false),
                BleedRatePerDay=Number(set.BleedRateTotal), Bleeding=set.BleedRateTotal>0,
                LifeThreatening=all.Any(h=>h.IsCurrentlyLifeThreatening), BloodLoss=Number(all.Where(h=>h.def==HediffDefOf.BloodLoss).Sum(h=>(double)h.Severity)),
                ShouldSeekMedicalRest=HealthAIUtility.ShouldSeekMedicalRest(pawn), Hediffs=[.. all.Select(h=>HediffRow(pawn,h))], Surgery=Surgery(pawn) };
            if(pawn.CurrentBed()!=null) row = row with { BedId = Id(pawn.CurrentBed().GetUniqueLoadID()) };
            if(row.Bleeding && !pawn.Dead) {
                var ticks=HealthUtility.TicksUntilDeathDueToBloodLoss(pawn);
                if(ticks>0 && ticks<int.MaxValue) row = row with { HoursUntilDeathFromBloodLoss = GameTime.Hours((double)ticks) };
            }
            row = row with { Snapshot = NativeObservationSnapshot.Snapshot("pawn-health",context,pawn.GetUniqueLoadID(),w => {
                w.Write(row.LifeThreatening);w.Write(row.BloodLoss);
                w.Write(row.ShouldSeekMedicalRest);w.Write(row.BedId??"");
                foreach(var h in all.OrderBy(h=>h.def.defName,StringComparer.Ordinal)
                    .ThenBy(h=>h.Part!=null?pawn.RaceProps.body.AllParts.IndexOf(h.Part):-1)) {
                    w.Write(h.def.defName);w.Write((double)h.Severity);
                    w.Write(h.Part!=null?pawn.RaceProps.body.AllParts.IndexOf(h.Part):-1);
                }
            }) };
            return row;
        }

        // Surgery facts, all through vanilla: recipe discovery from the
        // retired home/medical_operations tool, success chance from the recipe's
        // own SurgeryOutcomeEffectDef comps. The brain never recomputes any of it.
        // Absent for a dead pawn or one off every map.
        internal static Obs.PawnSurgery? Surgery(Pawn pawn)
        {
            var map=pawn.MapHeld;
            if(pawn.Dead || map==null) return null;
            var row=new Obs.PawnSurgery();
            var parts=pawn.RaceProps.body.AllParts;
            foreach(var bill in pawn.BillStack.Bills) {
                var b=new Obs.SurgeryBill {Id=Id(bill.GetUniqueLoadID()),Recipe=Id(bill.recipe.defName)};
                if(bill is Bill_Medical medical && medical.Part!=null) {
                    var index=parts.IndexOf(medical.Part);
                    if(index<0) throw new InvalidOperationException("Surgery body part is not in this pawn's body.");
                    b = b with { PartIndex = index };
                }
                row = row with { SurgeryBills = [.. row.SurgeryBills, b] };
            }
            foreach(var missing in pawn.health.hediffSet.GetMissingPartsCommonAncestors()) {
                var part=missing.Part; if(part==null) continue;
                var m=new Obs.MissingBodyPart {PartIndex=parts.IndexOf(part),PartDefName=Id(part.def.defName)};
                row = row with { MissingParts = [.. row.MissingParts, m] };
            }
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
                    var op=new Obs.SurgeryOperation {Recipe=Definition(def),Kind=Kind(pawn,def,part),
                        EligibleDoctors=(uint)doctors.Count,
                        IngredientsOnMap=others && (!usesMedicine || medicine!=null),
                        // medicine is stocked but the care level forbids all of it.
                        MedicineCareLimited=careLimited,
                        Violation=def.Worker.IsViolationOnPawn(pawn,part,Faction.OfPlayer),Lethal=Lethal(pawn,def,part)};
                    if(part!=null) {op = op with { PartIndex = parts.IndexOf(part) };op = op with { PartDefName = Id(part.def.defName) };}
                    if(op.Kind==Obs.SurgeryKind.Harvest && part?.def.spawnThingOnRemoved!=null) op = op with { YieldMarketValue = Number(part.def.spawnThingOnRemoved.BaseMarketValue) };
                    // Artificial part removal: the added part on the
                    // target and the thing its removal spawns.
                    if(def.Worker is Recipe_RemoveBodyPart && part!=null) {
                        var added=pawn.health.hediffSet.hediffs.OfType<Hediff_AddedPart>().FirstOrDefault(h => h.Part==part);
                        if(added!=null) {
                            op = op with { AddedPartHediff = Id(added.def.defName) };
                            var spawn=added.def.spawnThingOnRemoved;
                            if(spawn!=null) {op = op with { YieldThingDef = Id(spawn.defName) };op = op with { YieldMarketValue = Number(spawn.BaseMarketValue) };}
                        }
                    }
                    if(medicine!=null) {
                        var count=def.ingredients.Where(i => i.filter.Allows(medicine)).Sum(i => i.CountRequiredOfFor(medicine,def));
                        if(count>0) op = op with { MedicineMarketValue = Number(medicine.BaseMarketValue*count) };
                    }
                    if(def.surgeryOutcomeEffect!=null && doctors.Count>0) {
                        float? real=pawn.InBed()?(float?)null:bestBed?.GetStatValue(StatDefOf.SurgerySuccessChanceFactor) ?? 1f;
                        // each doctor's chance, so training can name one.
                        var chances=new System.Collections.Generic.Dictionary<string,double>();
                        foreach(var d in doctors) chances[d.GetUniqueLoadID()]=Number(Chance(def,d,pawn,part,medicine,real));
                        op = op with { DoctorChances = chances, SuccessChance = chances.Values.Max() };
                        op = op with { DoctorSuccessChance = Number(doctors.Max(d => Chance(def,d,pawn,part,medicine,idealFactor))) };
                    }
                    row = row with { Operations = [.. row.Operations, op] };
                }
            }
            return row;
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

        private static Obs.PawnEquipment Equipment(Pawn pawn)
        {
            var row=NativeGearFacts.Equipment(pawn);
            // Defense capacity: observed damage per second.
            if(pawn.equipment!=null) try {row = row with { RangedDps = NativeDefenseStats.PawnRangedDps(pawn), MeleeDps = pawn.GetStatValue(StatDefOf.MeleeDPS) };} catch {}
            return row;
        }

        private static readonly WorkTags[] SingleWorkTags=((WorkTags[])Enum.GetValues(typeof(WorkTags)))
            .Where(tag => tag!=WorkTags.None && ((int)tag&((int)tag-1))==0).ToArray();

        internal static Obs.PawnBiography Biography(Pawn pawn)
        {
            var row=new Obs.PawnBiography { BiologicalAgeYears = Number(pawn.ageTracker.AgeBiologicalYearsFloat) };
            // A pawn without a story or skill tracker (an animal) has no traits or skills.
            if(pawn.story?.traits!=null)
                foreach(var trait in pawn.story.traits.allTraits) row = row with { Traits = [.. row.Traits, new Obs.Trait {DefName=Id(trait.def.defName),Degree=trait.Degree}] };
            if(pawn.skills!=null)
                foreach(var skill in pawn.skills.skills)
                    row = row with { Skills = [.. row.Skills, new Obs.Skill {Learning=NativeSkillLearning.Read(pawn,skill),DefName=Id(skill.def.defName),Level=skill.Level,StoredLevel=skill.levelInt,Passion=NativeEnums.Passion(skill.passion),Disabled=skill.TotallyDisabled}] };
            var tags=pawn.CombinedDisabledWorkTags;
            foreach(var tag in SingleWorkTags) if((tags&tag)!=0) row = row with { DisabledWorkTags = [.. row.DisabledWorkTags, tag.ToString()] };
            foreach(var def in DefDatabase<WorkTypeDef>.AllDefsListForReading) if(pawn.WorkTypeIsDisabled(def)) row = row with { IncapableWorkTypes = [.. row.IncapableWorkTypes, Id(def.defName)] };
            return row;
        }

        // Populates ONLY the care-policy fields (medical_care, self_tend and
        // hostility_response, which PawnSettingsIntent writes) that
        // the brain's settings validation allows through
        // when a caller asks for `care`. The follow flags,
        // master_id and the 24-hour schedule are the wider Assign-tab row --
        // deliberately never requested via this protobuf
        // (rimgovernor/observations_list_pawns) path, so they must never be
        // set here regardless of what native happens to hold.
        // allowed_area_id belongs to the work snapshot (AllowedArea below).
        private static Obs.PawnSettings CarePolicy(Pawn pawn,Obs.PawnSettings row)
        {
            // A pawn without player settings has no care policy.
            var settings=pawn.playerSettings;
            if(settings==null) return row;
            row = row with { MedicalCare = NativeEnums.Care(settings.medCare), SelfTend = settings.selfTend };
            if(settings.UsesConfigurableHostilityResponse) row = row with { HostilityResponse = NativeEnums.Hostility(settings.hostilityResponse) };
            return row;
        }

        private static Obs.PawnSettings Work(Pawn pawn,Obs.PawnSettings row)
        {
            row = row with { WorkApplies = pawn.workSettings?.EverWork==true };
            // GetPriority can initialize an unreadable tracker; never read it then: no work rows.
            if(pawn.workSettings?.Initialized!=true || row.WorkApplies!=true) return row;
            row = row with { ManualWorkPriorities = PawnSettingsRead.ManualPriorities() ?? throw new InvalidOperationException("A pawn read has no game play settings.") };
            var defs=DefDatabase<WorkTypeDef>.AllDefsListForReading;
            foreach(var def in defs) row = row with { Work = [.. row.Work, new Obs.WorkSetting {DefName=Id(def.defName),Priority=pawn.workSettings.GetPriority(def),Disabled=pawn.WorkTypeIsDisabled(def)}] };
            return row;
        }

        // The body stats the outdoor dash planner reads under d.Work: the
        // game's own stat and hediff values. A failed read leaves the whole
        // block absent and adds a body issue, never zeros.
        private static Obs.PawnSettings BodyStats(Pawn pawn,Obs.PawnSettings row)
        {
            try {
                var body=new Obs.PawnBodyStats {
                    ComfortableMinC=Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)),
                    ComfortableMaxC=Number(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax)),
                    MoveSpeed=Number(pawn.GetStatValue(StatDefOf.MoveSpeed)),
                    Hypothermia=Severity(pawn,"Hypothermia") };
                if(pawn.Spawned) body = body with { CellTemperatureC = Number(pawn.AmbientTemperature) };
                row = row with { Body = body };
            } catch(InvalidOperationException) {
                // A failed stat read leaves the whole block absent, never zeros.
            }
            return row;
        }

        // The highest severity among the pawn's hediffs of one def, 0 when it has none.
        private static double Severity(Pawn pawn,string defName)
        {
            var def=DefDatabase<HediffDef>.GetNamedSilentFail(defName);
            if(def==null) return 0;
            var best=0.0;
            foreach(var h in pawn.health.hediffSet.hediffs) if(h.def==def) best=Math.Max(best,h.Severity);
            return Number(best);
        }

        // Projects the pawn's current allowed-area restriction under d.Work
        //: WorkSettingsIntent writes allowed_area alongside the
        // priorities, so a work reader (the routine
        // work review, the recovery/area case) must see the
        // area alongside the priorities. Unset when the pawn is unrestricted,
        // matching the AllowedArea{Clear} write; Missing when the pawn has no
        // player settings at all.
        private static Obs.PawnSettings AllowedArea(Pawn pawn,Obs.PawnSettings row)
        {
            var settings=pawn.playerSettings;
            if(settings==null) return row;
            var area=settings.AreaRestrictionInPawnCurrentMap;
            if(area!=null) row = row with { AllowedAreaId = Id(area.GetUniqueLoadID()) };
            return row;
        }

        // Projects the pawn's 24-hour timetable as one TimetableSlot per hour
        // (hour 0 first), the exact list RimWorld's Pawn_TimetableTracker
        // resolves CurrentAssignment from, so the brain can mirror the
        // schedule observation used by independent timetable-aware consumers
        // (boundary.ExpectedScheduleDef). Pawns without a timetable tracker
        // (animals, other factions) or with an unreadable/odd-length one
        // report a "schedule" issue rather than a partial list.
        private static Obs.PawnSettings Schedule(Pawn pawn,Obs.PawnSettings row)
        {
            var times=pawn.timetable?.times;
            if(times==null) return row;
            if(times.Count!=24 || times.Any(t=>t==null)) throw new InvalidOperationException("The pawn's timetable is not 24 assigned hours.");
            for(var hour=0;hour<times.Count;hour++) row = row with { Schedule = [.. row.Schedule, new Obs.TimetableSlot {Hour=(uint)hour,AssignmentDefName=Id(times[hour].defName)}] };
            return row;
        }

        // The one animal state builder: body, herd and training
        // facts for every animal; for a player animal its pen, area, care
        // and the release/slaughter guards; for a wild one its tame facts.
        internal static Obs.AnimalState Animal(Pawn pawn)
        {
            var row=NativeHusbandryOperations.HerdFacts(pawn,new Obs.AnimalState());
            if(pawn.training!=null) {
                var defs=DefDatabase<TrainableDef>.AllDefsListForReading;
                foreach(var def in defs) {
                    var report=pawn.training.CanAssignToTrain(def,out var visible);
                    var item=new Obs.TrainingEntry {DefName=Id(def.defName),Learned=pawn.training.HasLearned(def),Wanted=pawn.training.GetWanted(def),Available=report.Accepted && visible};
                    if(!item.Available) item = item with { Reason = Text(report.Reason??"Native training option is unavailable.") };
                    row = row with { Training = [.. row.Training, item] };
                }
            }
            var map=pawn.MapHeld;
            if(map?.designationManager!=null) {
                var all=map.designationManager.AllDesignationsOn(pawn);
                row = row with { Slaughter = all.Any(d=>d.def==DesignationDefOf.Slaughter), Release = all.Any(d=>d.def==DesignationDefOf.ReleaseAnimalToWild),
                    Tame = all.Any(d=>d.def==DesignationDefOf.Tame) };
            }
            row = row with { Tameable = NativeHusbandryOperations.Tameable(pawn) };
            // Live spawned animals only: the guards and pen reads assume one.
            if(pawn.Dead || !pawn.Spawned) return row;
            if(pawn.Faction!=null && pawn.Faction==Faction.OfPlayerSilentFail) {
                var eligible=NativeHusbandryOperations.Eligible(pawn);
                row = row with { SafeToRelease = eligible && NativeHusbandryOperations.SafeToRelease(pawn) };
                if(eligible) row = NativeHusbandryOperations.SlaughterFacts(pawn,row) with {
                    FollowDrafted = pawn.playerSettings?.followDrafted ?? false, FollowFieldwork = pawn.playerSettings?.followFieldwork ?? false,
                    Obedient = NativeHusbandryOperations.Obedient(pawn) };
                // Medical care cap inputs.
                if(pawn.playerSettings!=null) row = row with { MedicalCare = NativeEnums.Care(pawn.playerSettings.medCare) };
                var bondPartners=pawn.relations?.DirectRelations.Where(r => r.def==PawnRelationDefOf.Bond && r.otherPawn!=null && !r.otherPawn.Dead && r.otherPawn.RaceProps.Humanlike).Select(r => r.otherPawn.GetUniqueLoadID()).OrderBy(i => i,System.StringComparer.Ordinal).ToList();
                row = row with { Bonded = pawn.relations?.DirectRelations.Any(r => r.def==PawnRelationDefOf.Bond && r.otherPawn!=null && !r.otherPawn.Dead)==true };
                if(bondPartners!=null) row = row with { BondedPawnIds = [.. row.BondedPawnIds, .. bondPartners] };
                row = row with { Conditions = Conditions(pawn) };
                if(AnimalPenUtility.NeedsToBeManagedByRope(pawn)) {
                    var pen=AnimalPenUtility.GetCurrentPenOf(pawn,false);
                    row = row with { Contained = pen!=null };
                    if(pen!=null) row = row with { PenId = Id(pen.parent.GetUniqueLoadID()) };
                }
                // Area reconciliation: the saved restriction.
                if(eligible) {row = row with { MasterId = NativeHusbandryOperations.MasterId(pawn) };row = row with { AllowedAreaId = NativeHusbandryOperations.AreaId(pawn) };row = row with { SupportsAllowedAreas = NativeHusbandryOperations.SupportsAllowedAreas(pawn) };}
            }
            return row;
        }
        // Non-mutating: never calls Pawn_RelationsTracker.OpinionOf or anything that
        // recalculates situational social thoughts -- see PawnConfigTool's class
        // remarks for why (Thought_Situational.Notify_BecameActive deletes memories
        // of the def it produces). Ported from PawnSettingsRead.RelationsBlock/OpinionRow.
        private static Obs.PawnSocial Social(Pawn pawn)
        {
            var row=new Obs.PawnSocial();
            var current=ExpectationsUtility.CurrentExpectationFor(pawn);
            if(current!=null)row = row with { ExpectationDefName = current.defName };
            var high=DefDatabase<ExpectationDef>.GetNamedSilentFail("High");
            if(high!=null&&current!=null)row = row with { HighExpectations = current.order>=high.order };
            foreach(var t in PawnSettingsRead.GroupThoughtRows(PawnSettingsRead.LiveMemories(pawn)))
                row = row with { Memories = [.. row.Memories, ThoughtRow(t.DefName,t.Count,t.Total)] };
            var (situational,stale)=PawnSettingsRead.LiveSituational(pawn);
            foreach(var t in PawnSettingsRead.GroupThoughtRows(situational))
                row = row with { Situational = [.. row.Situational, ThoughtRow(t.DefName,t.Count,t.Total)] };
            return row with { SituationalCacheStale = stale };
        }

        private static Obs.Thought ThoughtRow(string? defName,int count,double total)=>
            new Obs.Thought {DefName=defName ?? throw new InvalidOperationException("A thought has no def."),MoodOffsetTotal=Number(total)};

        private static Obs.DefinitionRef Definition(Def def)=>new Obs.DefinitionRef {DefName=Id(def.defName)};
    }
}
