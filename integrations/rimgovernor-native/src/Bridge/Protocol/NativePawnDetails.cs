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
        internal static Obs.PawnDetails Defaults(Obs.PawnDetails? source)
        {
            var d=new Obs.PawnDetails {
                Needs=source==null || source.Needs is null || source.Needs.GetValueOrDefault(),
                Health=source==null || source.Health is null || source.Health.GetValueOrDefault(),
                Equipment=source==null || source.Equipment is null || source.Equipment.GetValueOrDefault(),
                Biography=source==null || source.Biography is null || source.Biography.GetValueOrDefault(),
                Settings=source==null || source.Settings is null || source.Settings.GetValueOrDefault(),
                Social=source==null || source.Social is null || source.Social.GetValueOrDefault(),
                Animals=source==null || source.Animals is null || source.Animals.GetValueOrDefault(),
                VisibleHediffsOnly=(source?.VisibleHediffsOnly).GetValueOrDefault()==true, Work=(source?.Work).GetValueOrDefault()==true, Schedule=(source?.Schedule).GetValueOrDefault()==true,
                Tend=(source?.Tend).GetValueOrDefault()==true };
            if(source!=null && source.TendReachBudgetMs is not null) d = d with { TendReachBudgetMs = source.TendReachBudgetMs };
            return d;
        }

        // Doctor-side tend gates for one reply's rows, mirroring
        // NativeTendOperations.Prepare: pawn-control eligibility (the gate
        // behind "Tend requires an eligible doctor"), WorkGiver_Tend's required
        // capacities, the Doctor work type, and CanReach(ClosestTouch, Deadly)
        // to every other row of the same reply -- the candidate patients.
        internal static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>> Tend(System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>> page,uint? reachBudgetMs)
        {
            var giver=DefDatabase<WorkGiverDef>.AllDefsListForReading
                .Where(d => d.giverClass!=null && typeof(WorkGiver_Tend).IsAssignableFrom(d.giverClass))
                .Select(d => d.Worker as WorkGiver_Tend).FirstOrDefault(w => w!=null);
            // Pairwise reachability is quadratic in the page (about 0.3 us a pair), so
            // it runs under Go's budget: a doctor reached after it is spent reports
            // reach_skipped (list unknown), never a partial list.
            var budgetTicks=reachBudgetMs.HasValue ? reachBudgetMs.Value*System.Diagnostics.Stopwatch.Frequency/1000 : long.MaxValue;
            var began=System.Diagnostics.Stopwatch.GetTimestamp();
            var result=new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>>(page.Count);
            foreach(var item in page) {
                var pawn=item.Key; var row=new Obs.PawnTendDoctor();
                row = row with { Spawned = pawn.Spawned }; row = row with { HasDrafter = pawn.drafter!=null };
                row = row with { ControlEligible = pawn.drafter!=null && pawn.Spawned && !pawn.Dead && !pawn.Downed
                    && !pawn.InMentalState && pawn.IsColonistPlayerControlled };
                row = row with { WorkTypeDisabled = pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) };
                if(giver==null) {
                    row = row with { Issues = [.. row.Issues, Issue("capacities_ok",Common.UnavailableReason.NotApplicable,"WorkGiver_Tend is unavailable in this game.")] };
                    row = row with { Issues = [.. row.Issues, Issue("missing_capacity",Common.UnavailableReason.NotApplicable,"WorkGiver_Tend is unavailable in this game.")] };
                } else {
                    var missing=giver.MissingRequiredCapacity(pawn);
                    row = row with { CapacitiesOk = missing==null };
                    if(missing!=null) row = row with { MissingCapacity = Id(missing.defName) };
                    else row = row with { Issues = [.. row.Issues, Issue("missing_capacity",Common.UnavailableReason.NotApplicable,"No tend capacity is missing.")] };
                }
                if(!pawn.Spawned || pawn.Dead) {
                    row = row with { Issues = [.. row.Issues, Issue("reachable_pawn_ids",Common.UnavailableReason.NotApplicable,"An unspawned or dead pawn reaches nothing.")] };
                    result.Add(new System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>(pawn,item.Value with { TendDoctor = row }));
                    continue;
                }
                if(System.Diagnostics.Stopwatch.GetTimestamp()-began>=budgetTicks) {row = row with { ReachSkipped = true };result.Add(new System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>(pawn,item.Value with { TendDoctor = row }));continue;}
                foreach(var other in page) {
                    if(ReferenceEquals(other.Key,pawn) || !other.Key.Spawned || other.Key.Dead) continue;
                    if(pawn.CanReach(other.Key,PathEndMode.ClosestTouch,Danger.Deadly)) row = row with { ReachablePawnIds = [.. row.ReachablePawnIds, (other.Value.Pawn?.Id ?? "")] };
                }
                result.Add(new System.Collections.Generic.KeyValuePair<Pawn,Obs.PawnState>(pawn,item.Value with { TendDoctor = row }));
            }
            return result;
        }

        internal static Obs.PawnState Apply(Pawn pawn,System.Collections.Generic.List<Pawn> colonists,Obs.PawnState row,Obs.PawnDetails? requested,Common.ObservationContext context)
        {
            var d=Defaults(requested);
            if(d.Needs.GetValueOrDefault()) {
                var needs=row.Needs ?? new Obs.PawnNeeds();
                if(pawn.needs?.food!=null) {needs = needs with { Food = Number(pawn.needs.food.CurLevelPercentage) };needs = needs with { HungerCategory = (Defs.HungerCategory)(int)pawn.needs.food.CurCategory };}
                else {needs = needs with { Issues = [.. needs.Issues, Missing("food")] };needs = needs with { Issues = [.. needs.Issues, Missing("hunger_category")] };}
                if(pawn.needs?.rest!=null) needs = needs with { Rest = Number(pawn.needs.rest.CurLevelPercentage) }; else needs = needs with { Issues = [.. needs.Issues, Missing("rest")] };
                if(pawn.needs?.joy!=null) needs = needs with { Joy = Number(pawn.needs.joy.CurLevelPercentage) }; else needs = needs with { Issues = [.. needs.Issues, Missing("joy")] };
                row = row with { Needs = needs };
            } else {row = row with { Needs = null };row = row with { Issues = [.. row.Issues, Skipped("needs")] };}
            if(d.Health.GetValueOrDefault() && row.Health!=null) row = row with { Health = Health(pawn,row.Health,d.VisibleHediffsOnly.GetValueOrDefault(),context) };
            else if(!d.Health.GetValueOrDefault()) {
                row = row with { Health = null };
                row = row with { Issues = [.. row.Issues.Where(i=>(i.Field ?? "")!="health")] };
                row = row with { Issues = [.. row.Issues, Skipped("health")] };
            }
            if(d.Equipment.GetValueOrDefault()) row = row with { Equipment = Equipment(pawn) }; else row = row with { Issues = [.. row.Issues, Skipped("equipment")] };
            if(d.Biography.GetValueOrDefault()) row = row with { Biography = Biography(pawn) }; else row = row with { Issues = [.. row.Issues, Skipped("biography")] };
            if(d.Settings.GetValueOrDefault() || d.Work.GetValueOrDefault() || d.Schedule.GetValueOrDefault()) {
                var settings = new Obs.PawnSettings();
                // The bridge contract (go/internal/bridge/work_pawns.go's
                // validateSettings) enforces that a PawnSettings reply carries
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
                if(d.Settings.GetValueOrDefault()) settings = CarePolicy(pawn,settings);
                if(d.Work.GetValueOrDefault()) { settings = Work(pawn,settings); settings = AllowedArea(pawn,settings); settings = settings with { FoodRestriction = NativeFoodPolicy.Read(pawn), PolicyInputs = NativePolicyFacts.Inputs(pawn) }; if(pawn.RaceProps.Humanlike && pawn.needs?.food!=null && pawn.Faction==Faction.OfPlayerSilentFail) settings = settings with { EatTrigger = EatTriggerGuard.Effective(pawn) }; settings = BodyStats(pawn,settings); }
                if(d.Schedule.GetValueOrDefault()) settings = Schedule(pawn,settings);
                row = row with { Settings = settings };
            } else row = row with { Issues = [.. row.Issues, Skipped("settings")] };
            if(d.Social.GetValueOrDefault()) row = row with { Social = Social(pawn,colonists) };
            else row = row with { Issues = [.. row.Issues, Skipped("social")] };
            if(!d.Tend.GetValueOrDefault()) row = row with { Issues = [.. row.Issues, Skipped("tend_doctor")] };
            if(!d.Animals.GetValueOrDefault()) row = row with { Issues = [.. row.Issues, Skipped("animal_state")] };
            else if(!pawn.RaceProps.Animal) row = row with { Issues = [.. row.Issues, Issue("animal_state",Common.UnavailableReason.NotApplicable,"Pawn is not an animal.")] };
            else row = row with { AnimalState = Animal(pawn) };
            return row;
        }

        // One hediff row of a health read.
        private static Obs.Hediff HediffRow(Pawn pawn,Hediff h,Obs.PawnHealth row)
        {
                var item=new Obs.Hediff {DefName=Id(h.def.defName),Severity=Number(h.Severity),
                    Visible=h.Visible,Permanent=h.IsPermanent(),LifeThreatening=h.IsCurrentlyLifeThreatening,
                    TendableNow=h.TendableNow(false),Tended=h.IsTended()};
                if(h.Part!=null) {
                    var index=pawn.RaceProps.body.AllParts.IndexOf(h.Part);
                    if(index<0) throw new InvalidOperationException("Hediff body part is not in this pawn's body.");
                    item = item with { PartIndex = index };item = item with { PartDefName = Id(h.Part.def.defName) };
                }
                var immune=h.TryGetComp<HediffComp_Immunizable>(); item = item with { Immunizable = immune!=null };
                if(immune!=null) {
                    item = item with { Immunity = Number(immune.Immunity) };item = item with { FullyImmune = immune.FullyImmune };
                    // Include tending's severity modifier, as well as the disease's
                    // randomized native progression. Read only: never create immunity records.
                    item = item with { SeverityPerDay = Number(((HediffWithComps)h).comps
                        .OfType<HediffComp_SeverityModifierBase>().Sum(comp=>comp.SeverityChangePerDay())) };
                    var record=pawn.health.immunity.GetImmunityRecord(h.def);
                    if(record!=null && !pawn.Dead)
                        item = item with { ImmunityPerDay = Number(GameTime.PerDay(record.ImmunityChangePerTick(pawn,true,h))) };
                }
                var tend=h.TryGetComp<HediffComp_TendDuration>();
                if(tend!=null) {
                    if(item.Tended.GetValueOrDefault()) item = item with { TendQuality = Number(tend.tendQuality) };
                    if(!tend.TProps.TendIsPermanent) {item = item with { TendExpiresInTicks = Math.Max(0,tend.tendTicksLeft) };item = item with { NextTendInTicks = Math.Max(0,tend.tendTicksLeft-tend.TProps.TendTicksOverlap) };}
                }
                return item;
            }

        // Medical care cap inputs: the life threat and every hediff,
        // none filtered; the other PawnHealth fields stay absent.
        internal static Obs.PawnHealth Conditions(Pawn pawn)
        {
            var row=new Obs.PawnHealth();
            var all=pawn.health?.hediffSet?.hediffs;
            if(all==null) {row = row with { Issues = [.. row.Issues, Missing("hediffs")] };return row;}
            row = row with { LifeThreatening = all.Any(h=>h.IsCurrentlyLifeThreatening) };
            foreach(var h in all) row = row with { Hediffs = [.. row.Hediffs, HediffRow(pawn,h,row)] };
            row = row with { HiddenHediffs = 0 };
            row = row with { HediffCompleteness = Complete(all.Count,0) };
            return row;
        }

        private static Obs.PawnHealth Health(Pawn pawn,Obs.PawnHealth row,bool visibleOnly,Common.ObservationContext context)
        {
            row = row with { Issues = [] };
            var set=pawn.health.hediffSet;
            var all=set.hediffs.ToList(); 
            row = row with { Pain = Number(set.PainTotal) };
            row = row with { LifeThreatening = all.Any(h=>h.IsCurrentlyLifeThreatening) };
            row = row with { BloodLoss = Number(all.Where(h=>h.def==HediffDefOf.BloodLoss).Sum(h=>(double)h.Severity)) };
            row = row with { Issues = [.. row.Issues, Unsupported("stable_rest_eligible","The existing medical-rest safety owner does not distinguish unreadable facts from ineligibility.")] };
            row = row with { ShouldSeekMedicalRest = HealthAIUtility.ShouldSeekMedicalRest(pawn) };
            row = row with { UrgentMedicalRest = HealthAIUtility.ShouldSeekMedicalRestUrgent(pawn) };
            if(pawn.CurrentBed()!=null) row = row with { BedId = Id(pawn.CurrentBed().GetUniqueLoadID()) };
            else row = row with { Issues = [.. row.Issues, Issue("bed_id",Common.UnavailableReason.NotApplicable,"Pawn is not in a bed.")] };
            if(row.Bleeding.GetValueOrDefault() && !pawn.Dead) {
                var ticks=HealthUtility.TicksUntilDeathDueToBloodLoss(pawn);
                if(ticks>0 && ticks<int.MaxValue) row = row with { HoursUntilDeathFromBloodLoss = GameTime.Hours((double)ticks) };
                else row = row with { Issues = [.. row.Issues, Issue("hours_until_death_from_blood_loss",Common.UnavailableReason.NotApplicable,"No finite native bleed-out estimate.")] };
            } else row = row with { Issues = [.. row.Issues, Issue("hours_until_death_from_blood_loss",Common.UnavailableReason.NotApplicable,"No living bleeding pawn.")] };
            if(pawn.Dead) row = row with { Issues = [.. row.Issues, Issue("capacities",Common.UnavailableReason.NotApplicable,"Capacities are not evaluated for dead pawns.")] };
            else if(pawn.health.capacities==null) row = row with { Issues = [.. row.Issues, Missing("capacities")] };
            else {
                var defs=DefDatabase<PawnCapacityDef>.AllDefsListForReading; 
                foreach(var def in defs) row = row with { Capacities = [.. row.Capacities, new Obs.Capacity {DefName=Id(def.defName),Level=Number(pawn.health.capacities.GetLevel(def))}] };
            }
            var visible=all.Where(h=>!visibleOnly || h.Visible).ToList();
            row = row with { HiddenHediffs = checked((uint)(all.Count-visible.Count)) };
            foreach(var h in visible) row = row with { Hediffs = [.. row.Hediffs, HediffRow(pawn,h,row)] };
            row = row with { HediffCompleteness = Complete(visible.Count,all.Count-visible.Count) };
            var bills=pawn.BillStack?.Bills;
            if(bills==null) row = row with { Issues = [.. row.Issues, Missing("surgery_bills")] };
            else {
                foreach(var bill in bills) {
                    var b=new Obs.SurgeryBill {Id=Id(bill.GetUniqueLoadID()),Recipe=Id(bill.recipe.defName),Suspended=bill.suspended};
                    if(bill is Bill_Medical medical && medical.Part!=null) {
                        var index=pawn.RaceProps.body.AllParts.IndexOf(medical.Part);
                        if(index<0) throw new InvalidOperationException("Surgery body part is not in this pawn's body.");
                        b = b with { PartIndex = index };
                    }
                    row = row with { SurgeryBills = [.. row.SurgeryBills, b] };
                }
            }
            if(pawn.Dead) {row = row with { Issues = [.. row.Issues, Issue("missing_parts",Common.UnavailableReason.NotApplicable,"Dead pawn.")] };row = row with { Issues = [.. row.Issues, Issue("operations",Common.UnavailableReason.NotApplicable,"Dead pawn.")] };row = row with { Issues = [.. row.Issues, Issue("installed_parts",Common.UnavailableReason.NotApplicable,"Dead pawn.")] };}
            else row = Surgery(pawn,row);
            row = row with { Snapshot = NativeObservationSnapshot.Snapshot("pawn-health",context,pawn.GetUniqueLoadID(),w => {
                w.Write(row.Pain.GetValueOrDefault());w.Write(row.LifeThreatening.GetValueOrDefault());w.Write(row.BloodLoss.GetValueOrDefault());
                w.Write(row.ShouldSeekMedicalRest.GetValueOrDefault());w.Write(row.UrgentMedicalRest.GetValueOrDefault());w.Write(row.BedId??"");
                w.Write(row.HiddenHediffs.GetValueOrDefault());
                foreach(var h in visible.OrderBy(h=>h.def.defName,StringComparer.Ordinal)
                    .ThenBy(h=>h.Part!=null?pawn.RaceProps.body.AllParts.IndexOf(h.Part):-1)) {
                    w.Write(h.def.defName);w.Write((double)h.Severity);
                    w.Write(h.Part!=null?pawn.RaceProps.body.AllParts.IndexOf(h.Part):-1);
                }
            }) };
            return row;
        }

        // Surgery facts, all through vanilla: recipe discovery from the
        // retired home/medical_operations tool, success chance from the recipe's
        // own SurgeryOutcomeEffectDef comps. Go never recomputes any of it.
        internal static Obs.PawnHealth Surgery(Pawn pawn,Obs.PawnHealth row)
        {
            var parts=pawn.RaceProps.body.AllParts;
            foreach(var added in pawn.health.hediffSet.hediffs.OfType<Hediff_AddedPart>()) {
                var installed=new Obs.InstalledPart {Definition=Definition(added.def)};
                if(added.Part!=null) {installed = installed with { PartIndex = parts.IndexOf(added.Part) };installed = installed with { PartDefName = Id(added.Part.def.defName) };}
                row = row with { InstalledParts = [.. row.InstalledParts, installed] };
            }
            foreach(var missing in pawn.health.hediffSet.GetMissingPartsCommonAncestors()) {
                var part=missing.Part; if(part==null) continue;
                var m=new Obs.MissingBodyPart {PartIndex=parts.IndexOf(part),PartDefName=Id(part.def.defName),
                    Vital=part.def.tags!=null && part.def.tags.Any(t => t!=null && t.vital)};
                if(part.parent!=null) {m = m with { ParentIndex = parts.IndexOf(part.parent) };m = m with { ParentDefName = Id(part.parent.def.defName) };}
                row = row with { MissingParts = [.. row.MissingParts, m] };
            }
            var map=pawn.MapHeld;
            if(map==null) {row = row with { Issues = [.. row.Issues, Issue("operations",Common.UnavailableReason.NotApplicable,"Pawn is not on a map.")] };return row;}
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
            var row=new Obs.PawnEquipment();
            if(pawn.equipment==null) {
                row = row with { Issues = [.. row.Issues, Missing("equipped")] };row = row with { Issues = [.. row.Issues, Missing("primary_id")] };row = row with { Issues = [.. row.Issues, Missing("armed")] };
            }
            else {
                var list=pawn.equipment.AllEquipmentListForReading; 
                foreach(var thing in list) row = row with { Equipped = [.. row.Equipped, Gear(thing)] };
                row = row with { Armed = pawn.equipment.Primary!=null };
                // Defense capacity: observed damage per second.
                try {row = row with { RangedDps = NativeDefenseStats.PawnRangedDps(pawn) };row = row with { MeleeDps = pawn.GetStatValue(StatDefOf.MeleeDPS) };} catch {}
                if(pawn.equipment.Primary!=null) row = row with { PrimaryId = Id(pawn.equipment.Primary.GetUniqueLoadID()) };
                else row = row with { Issues = [.. row.Issues, Issue("primary_id",Common.UnavailableReason.NotApplicable,"No equipped primary weapon.")] };
            }
            if(pawn.apparel==null) row = row with { Issues = [.. row.Issues, Missing("apparel")] };
            else {foreach(var thing in pawn.apparel.WornApparel) row = row with { Apparel = [.. row.Apparel, Gear(thing)] };}
            if(pawn.inventory?.innerContainer==null) {row = row with { Issues = [.. row.Issues, Missing("inventory_weapons")] };row = row with { Issues = [.. row.Issues, Missing("inventory_item_count")] };}
            else {
                var list=pawn.inventory.innerContainer; row = row with { InventoryItemCount = checked((uint)list.Count) };
                for(var i=0;i<list.Count;i++) if(list[i].def.IsWeapon) row = row with { InventoryWeapons = [.. row.InventoryWeapons, Gear(list[i])] };
            }
            if(pawn.carryTracker==null) row = row with { Issues = [.. row.Issues, Missing("carried_thing_id")] };
            else if(pawn.carryTracker.CarriedThing!=null) row = row with { CarriedThingId = Id(pawn.carryTracker.CarriedThing.GetUniqueLoadID()) };
            else row = row with { Issues = [.. row.Issues, Issue("carried_thing_id",Common.UnavailableReason.NotApplicable,"Pawn is carrying no thing.")] };
            foreach(var collection in new[]{"equipped","apparel","inventory_weapons"})
                foreach(var field in new[]{"forced","locked","armor_sharp","armor_blunt","insulation_cold","insulation_heat"})
                    row = row with { Issues = [.. row.Issues, Unsupported(collection+"."+field,"Gear ownership and protection stats are not projected.")] };
            return row;
        }
        private static Obs.GearItem Gear(Thing thing)
        {
            var row=new Obs.GearItem {Thing=NativeRef.Thing(thing)};
            row = NativeGearFacts.Biocode(thing, row);
            if(thing.Stuff!=null) row = row with { Stuff = Id(thing.Stuff.defName) };
            if(thing.TryGetQuality(out var quality)) row = row with { Quality = (Defs.QualityCategory)(int)quality };
            if(thing.def.useHitPoints) {
                if(thing.MaxHitPoints<=0 || thing.HitPoints<0) throw new InvalidOperationException("Invalid native hit points.");
                row = row with { HitPoints = thing.HitPoints };row = row with { MaxHitPoints = thing.MaxHitPoints };
                row = row with { ConditionFraction = Number((double)thing.HitPoints/thing.MaxHitPoints) };
            }
            return row;
        }

        private static readonly WorkTags[] SingleWorkTags=((WorkTags[])Enum.GetValues(typeof(WorkTags)))
            .Where(tag => tag!=WorkTags.None && ((int)tag&((int)tag-1))==0).ToArray();

        internal static Obs.PawnBiography Biography(Pawn pawn)
        {
            var row=new Obs.PawnBiography();
            if(pawn.ageTracker!=null) {row = row with { BiologicalAgeYears = Number(pawn.ageTracker.AgeBiologicalYearsFloat) };row = row with { ChronologicalAgeYears = Number(pawn.ageTracker.AgeChronologicalYearsFloat) };}
            else {row = row with { Issues = [.. row.Issues, Missing("biological_age_years")] };row = row with { Issues = [.. row.Issues, Missing("chronological_age_years")] };}
            if(pawn.story==null) {
                row = row with { Issues = [.. row.Issues, Missing("childhood")] };row = row with { Issues = [.. row.Issues, Missing("adulthood")] };
                row = row with { Issues = [.. row.Issues, Missing("traits")] };
            }
            else {
                if(pawn.story.Childhood!=null) {
                    row = row with { ChildhoodDefName = Id(pawn.story.Childhood.defName) };
                } else row = row with { Issues = [.. row.Issues, Issue("childhood",Common.UnavailableReason.NotApplicable,"No childhood backstory.")] };
                if(pawn.story.Adulthood!=null) {
                    row = row with { AdulthoodDefName = Id(pawn.story.Adulthood.defName) };
                } else row = row with { Issues = [.. row.Issues, Issue("adulthood",Common.UnavailableReason.NotApplicable,"No adulthood backstory.")] };
                if(pawn.story.traits==null) row = row with { Issues = [.. row.Issues, Missing("traits")] };
                else {foreach(var trait in pawn.story.traits.allTraits) row = row with { Traits = [.. row.Traits, new Obs.Trait {DefName=Id(trait.def.defName),Degree=trait.Degree}] };}
            }
            if(pawn.skills==null) row = row with { Issues = [.. row.Issues, Missing("skills")] };
            else {
                foreach(var skill in pawn.skills.skills) {
                    var defName=Id(skill.def.defName);
                    row = row with { Skills = [.. row.Skills, new Obs.Skill {DefName=defName,Level=skill.Level,StoredLevel=skill.levelInt,Passion=NativeEnums.Passion(skill.passion),Disabled=skill.TotallyDisabled}] };
                }
            }
            var tags=pawn.CombinedDisabledWorkTags;
            foreach(var tag in SingleWorkTags) if((tags&tag)!=0) row = row with { DisabledWorkTags = [.. row.DisabledWorkTags, tag.ToString()] };
            var defs=DefDatabase<WorkTypeDef>.AllDefsListForReading; 
            foreach(var def in defs) if(pawn.WorkTypeIsDisabled(def)) row = row with { IncapableWorkTypes = [.. row.IncapableWorkTypes, Id(def.defName)] };
            row = row with { Issues = [.. row.Issues, Unsupported("incapable_sources","Individual incapability sources are not projected.")] };
            row = row with { Issues = [.. row.Issues, Unsupported("title","Royal, role and story title selection is not projected.")] };
            row = row with { Issues = [.. row.Issues, Unsupported("title_source","Title source is not projected.")] };
            return row;
        }

        // Populates ONLY the care-policy fields (medical_care, self_tend and
        // hostility_response, which PawnSettingsIntent writes) that
        // go/internal/bridge/work_pawns.go's validateSettings allows through
        // when a caller asks for `care`. The follow flags,
        // master_id and the 24-hour schedule are the wider Assign-tab row --
        // deliberately never requested via this protobuf
        // (rimgovernor/observations_list_pawns) path, so they must never be
        // set here regardless of what native happens to hold.
        // allowed_area_id belongs to the work snapshot (AllowedArea below).
        private static Obs.PawnSettings CarePolicy(Pawn pawn,Obs.PawnSettings row)
        {
            var settings=pawn.playerSettings;
            if(settings==null) {
                row = row with { Issues = [.. row.Issues, Missing("medical_care")] };row = row with { Issues = [.. row.Issues, Missing("self_tend")] };row = row with { Issues = [.. row.Issues, Missing("hostility_response")] };
            } else {
                row = row with { MedicalCare = NativeEnums.Care(settings.medCare) };row = row with { SelfTend = settings.selfTend };
                if(settings.UsesConfigurableHostilityResponse) row = row with { HostilityResponse = NativeEnums.Hostility(settings.hostilityResponse) };
                else row = row with { Issues = [.. row.Issues, Issue("hostility_response",Common.UnavailableReason.NotApplicable,"Pawn has no configurable hostility response.")] };
            }
            return row;
        }

        private static Obs.PawnSettings Work(Pawn pawn,Obs.PawnSettings row)
        {
            row = row with { WorkApplies = pawn.workSettings?.EverWork==true };
            var manual=PawnSettingsRead.ManualPriorities();
            if(manual.HasValue) row = row with { ManualWorkPriorities = manual.Value };
            else row = row with { Issues = [.. row.Issues, Missing("manual_work_priorities")] };
            // GetPriority can initialize an unreadable tracker; never read it then.
            if(pawn.workSettings?.Initialized!=true || !row.WorkApplies.GetValueOrDefault()) { row = row with { Issues = [.. row.Issues, Missing("work")] };return row; }
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
                    CarryCapacity=Number(MassUtility.Capacity(pawn)),
                    ToxicEnvironmentResistance=Number(pawn.GetStatValue(StatDefOf.ToxicEnvironmentResistance)),
                    Hypothermia=Severity(pawn,"Hypothermia"), Heatstroke=Severity(pawn,"Heatstroke"),
                    Frostbite=Severity(pawn,"Frostbite"), ToxicBuildup=Severity(pawn,"ToxicBuildup") };
                if(pawn.Spawned) body = body with { CellTemperatureC = Number(pawn.AmbientTemperature) };
                var capacities=pawn.health.capacities;
                body = body with { Moving = Number(capacities.GetLevel(PawnCapacityDefOf.Moving)) };
                body = body with { Manipulation = Number(capacities.GetLevel(PawnCapacityDefOf.Manipulation)) };
                body = body with { Consciousness = Number(capacities.GetLevel(PawnCapacityDefOf.Consciousness)) };
                row = row with { Body = body };
            } catch(InvalidOperationException) {
                row = row with { Issues = [.. row.Issues, Missing("body")] };
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
            if(settings==null) { row = row with { Issues = [.. row.Issues, Missing("allowed_area_id")] }; return row; }
            var area=settings.AreaRestrictionInPawnCurrentMap;
            if(area!=null) row = row with { AllowedAreaId = Id(area.GetUniqueLoadID()) };
            return row;
        }

        // Projects the pawn's 24-hour timetable as one TimetableSlot per hour
        // (hour 0 first), the exact list RimWorld's Pawn_TimetableTracker
        // resolves CurrentAssignment from, so the Go side can mirror the
        // schedule fence NativeMoodReliefOperations applies
        // (boundary.ExpectedScheduleDef). Pawns without a timetable tracker
        // (animals, other factions) or with an unreadable/odd-length one
        // report a "schedule" issue rather than a partial list.
        private static Obs.PawnSettings Schedule(Pawn pawn,Obs.PawnSettings row)
        {
            var times=pawn.timetable?.times;
            if(times==null) { row = row with { Issues = [.. row.Issues, Issue("schedule",Common.UnavailableReason.NotApplicable,"Pawn has no timetable tracker.")] }; return row; }
            if(times.Count!=24 || times.Any(t=>t==null)) { row = row with { Issues = [.. row.Issues, Missing("schedule")] }; return row; }
            for(var hour=0;hour<times.Count;hour++) row = row with { Schedule = [.. row.Schedule, new Obs.TimetableSlot {Hour=(uint)hour,AssignmentDefName=Id(times[hour].defName)}] };
            return row;
        }

        // The one animal state builder: body, herd and training
        // facts for every animal; for a player animal its pen, area, care
        // and the release/slaughter guards; for a wild one its tame facts.
        internal static Obs.AnimalState Animal(Pawn pawn)
        {
            var row=new Obs.AnimalState();
            if(pawn.ageTracker!=null) row = NativeHusbandryOperations.HerdFacts(pawn,row);
            else {row = row with { Gender = pawn.gender.ToString() };row = row with { Issues = [.. row.Issues, Missing("age_years")] };}
            if(pawn.training==null) row = row with { Issues = [.. row.Issues, Missing("training")] };
            else {
                var defs=DefDatabase<TrainableDef>.AllDefsListForReading;
                foreach(var def in defs) {
                    var report=pawn.training.CanAssignToTrain(def,out var visible);
                    var item=new Obs.TrainingEntry {DefName=Id(def.defName),Learned=pawn.training.HasLearned(def),Wanted=pawn.training.GetWanted(def),Available=report.Accepted && visible};
                    if(!item.Available.GetValueOrDefault()) item = item with { Reason = Text(report.Reason??"Native training option is unavailable.") };
                    row = row with { Training = [.. row.Training, item] };
                }
            }
            var map=pawn.MapHeld;
            if(map?.designationManager==null) {row = row with { Issues = [.. row.Issues, Missing("slaughter")] };row = row with { Issues = [.. row.Issues, Missing("release")] };}
            else {
                var all=map.designationManager.AllDesignationsOn(pawn);
                row = row with { Slaughter = all.Any(d=>d.def==DesignationDefOf.Slaughter) };row = row with { Release = all.Any(d=>d.def==DesignationDefOf.ReleaseAnimalToWild) };
            }
            var milk=pawn.GetComp<CompMilkable>();if(milk!=null) row = row with { MilkFullness = Number(milk.Fullness) };else row = row with { Issues = [.. row.Issues, Issue("milk_fullness",Common.UnavailableReason.NotApplicable,"No milk component.")] };
            var wool=pawn.GetComp<CompShearable>();if(wool!=null) row = row with { WoolFullness = Number(wool.Fullness) };else row = row with { Issues = [.. row.Issues, Issue("wool_fullness",Common.UnavailableReason.NotApplicable,"No wool component.")] };
            foreach(var field in new[]{"fertile_adult","gestation","parent_ids"}) row = row with { Issues = [.. row.Issues, Unsupported(field,"Animal reproduction detail is not projected.")] };
            // Live spawned animals only: the guards and pen reads assume one.
            if(pawn.Dead || !pawn.Spawned) return row;
            if(pawn.Faction!=null && pawn.Faction==Faction.OfPlayerSilentFail) {
                var eligible=NativeHusbandryOperations.Eligible(pawn);
                row = row with { SafeToRelease = eligible && NativeHusbandryOperations.SafeToRelease(pawn) };
                if(eligible) row = NativeHusbandryOperations.SlaughterFacts(pawn,row);
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
            } else if(pawn.Faction==null) {
                // The tame target facts a MaintainHerd tame write needs.
                row = row with { Tameable = NativeHusbandryOperations.Tameable(pawn) };
                row = row with { Tame = map?.designationManager?.DesignationOn(pawn,DesignationDefOf.Tame)!=null };
            }
            return row;
        }
        // Non-mutating: never calls Pawn_RelationsTracker.OpinionOf or anything that
        // recalculates situational social thoughts -- see PawnConfigTool's class
        // remarks for why (Thought_Situational.Notify_BecameActive deletes memories
        // of the def it produces). Ported from PawnSettingsRead.RelationsBlock/OpinionRow.
        private static Obs.PawnSocial Social(Pawn pawn,System.Collections.Generic.List<Pawn> colonists)
        {
            var row=new Obs.PawnSocial();
            var current=ExpectationsUtility.CurrentExpectationFor(pawn);
            if(current!=null)row = row with { ExpectationDefName = current.defName };
            var high=DefDatabase<ExpectationDef>.GetNamedSilentFail("High");
            if(high!=null&&current!=null)row = row with { HighExpectations = current.order>=high.order };
            var memories=PawnSettingsRead.LiveMemories(pawn);
            if(memories==null) row = row with { Issues = [.. row.Issues, Missing("memories")] };
            else foreach(var t in PawnSettingsRead.GroupThoughtRows(memories))
                row = row with { Memories = [.. row.Memories, new Obs.Thought {DefName=t.DefName??"",Count=(uint)t.Count,MoodOffsetEach=Number(t.Each),MoodOffsetTotal=Number(t.Total)}] };

            var (situational,stale)=PawnSettingsRead.LiveSituational(pawn);
            if(situational==null) row = row with { Issues = [.. row.Issues, Missing("situational")] };
            else foreach(var t in PawnSettingsRead.GroupThoughtRows(situational))
                row = row with { Situational = [.. row.Situational, new Obs.Thought {DefName=t.DefName??"",Count=(uint)t.Count,MoodOffsetEach=Number(t.Each),MoodOffsetTotal=Number(t.Total)}] };
            if(stale.HasValue) row = row with { SituationalCacheStale = stale.Value };
            else row = row with { Issues = [.. row.Issues, Missing("situational_cache_stale")] };

            var direct=PawnSettingsRead.DirectRelationTargets(pawn);
            foreach(var other in colonists) {
                if(other==pawn) continue;
                var opinion=PawnSettingsRead.ReconstructedOpinion(pawn,other,out _);
                var relation=new Obs.Relation {Other=NativeRef.Thing(other),Opinion=opinion,OpinionReconstructed=true};
                if(direct.TryGetValue(other,out var defName)) relation = relation with { RelationDefName = defName };
                row = row with { Relations = [.. row.Relations, relation] };
            }
            return row;
        }

        private static Obs.DefinitionRef Definition(Def def)=>new Obs.DefinitionRef {DefName=Id(def.defName)};
        private static Obs.ReadIssue Missing(string field)=>Issue(field,Common.UnavailableReason.NativeComponentMissing,"Native component is absent or uninitialized.");
        private static Obs.ReadIssue Skipped(string field)=>Issue(field,Common.UnavailableReason.NotRequested,"Detail was explicitly disabled.");
        private static Obs.ReadIssue Unsupported(string field,string detail)=>Issue(field,Common.UnavailableReason.Unsupported,detail);
    }
}
