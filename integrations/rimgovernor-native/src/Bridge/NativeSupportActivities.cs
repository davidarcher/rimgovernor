using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Cell = RimGovernor.Brain.Domain.Cell;
using Obs = RimGovernor.Brain.GameFacts.Observations;
namespace RimGovernor.Native;

// One native census of every free colonist against every bench bill, patient and surgery (#3341), and each care patient's
// candidate beds (task 4.1 of redefine-habitat-requirements). These are
// conditional mechanics and current bill deficits, never new demand. A path is searched once for every colonist (NativePathLegs).
internal static class NativeSupportActivities
{
    internal static RimGovernor.Brain.GameFacts.Observations.SupportActivityRead Read(Map map, NativePathLegs? legs = null)
    {
        legs ??= new(map);
        var demands = new List<RimGovernor.Brain.GameFacts.Observations.SupportDemand>();
        var workers = new List<RimGovernor.Brain.GameFacts.Observations.SupportWorker>(); var deliveries = new List<RimGovernor.Brain.GameFacts.Observations.SupportDelivery>();
        var findings = new HashSet<string>(StringComparer.Ordinal);
        var carePatients = new List<RimGovernor.Brain.GameFacts.Observations.SupportCarePatient>();
        var roster = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var benches = map.listerThings.AllThings.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayer
            && b.BillStack.Bills.OfType<Bill_Production>().Any(bill => bill.ShouldDoNow())).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        foreach (var bench in benches)
        {
            AddRoom(bench, bench.InteractionCell, "bench", served: Serving(map, job => job.def == JobDefOf.DoBill && job.targetA.Thing == bench));
            foreach (var bill in bench.BillStack.Bills.OfType<Bill_Production>().Where(b => b.ShouldDoNow()).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                // Mixed ingredient substitution requires coupled recipe alternatives, not independent refill credit.
                var sources = map.listerThings.AllThings.Where(t => t.Spawned && t.def.category == ThingCategory.Item
                    && bill.IsFixedOrAllowedIngredient(t) && bill.recipe.ingredients.Count > 0
                    && bill.recipe.ingredients.All(i => i.filter.Allows(t))).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
                foreach (var source in sources)
                {
                    var destination = Staging(map, bench, source.def);
                    var needed = Required(bill, source.def);
                    var deficit = needed - Staged(map, destination, source.def);
                    if (deficit <= 0 || source.Position == destination) continue;
                    var id = "bill:" + bill.GetUniqueLoadID() + ":" + source.def.defName;
                    if (!demands.Any(d => d.Id == id)) demands.Add(Demand(id, "delivery", bench.GetUniqueLoadID(), destination, 0, 0,
                        Serving(map, job => job.def == JobDefOf.DoBill && job.bill == bill)));
                    foreach (var pawn in roster)
                    {
                        if (!Available(pawn) || source.IsForbidden(pawn) || bench.IsForbidden(pawn)
                            || !NativeProductionBills.Usable(bench) || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                            || pawn.WorkTagIsDisabled(WorkTags.ManualDumb) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                            || !pawn.CanReserveAndReach(source, PathEndMode.Touch, Danger.None)
                            || !DestinationAllowed(pawn, source, destination, Math.Min(deficit, source.stackCount))) continue;
                        if (Route(pawn, pawn.Position, source.Position, PathEndMode.Touch) is not { } approach
                            || Route(pawn, source.Position, destination, PathEndMode.OnCell) is not { } travel) continue;
                        deliveries.Add(new(id, pawn.GetUniqueLoadID(), source.GetUniqueLoadID(), source.def.defName,
                            bench.GetUniqueLoadID(), bill.GetUniqueLoadID(), At(source.Position), At(destination), deficit,
                            source.stackCount, pawn.carryTracker.MaxStackSpaceEver(source.def), approach, travel));
                    }
                }
            }
        }
        // The player's own pawns and the pawns it hosts (prisoners, slaves, guests, colony animals) with an operation to do now (task
        // 4.5): a Bill_Medical exists only where ITab_Pawn_Health allowed it, so every such patient is one the colony operates on.
        var surgical = map.mapPawns.AllPawnsSpawned.Where(p => (p.Faction == Faction.OfPlayer || p.HostFaction == Faction.OfPlayer)
                && p.BillStack?.Bills.OfType<Bill_Medical>().Any(b => b.ShouldDoNow()) == true)
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        // The same pawns each as the native tends it (task 4.1), the recovering ones a later transfer may move (task 4.4) and the
        // surgical ones, whose beds are the places the operation could be done in (task 4.5).
        var patients = map.mapPawns.SpawnedPawnsWithAnyHediff.Where(p => (p.Faction == Faction.OfPlayer || p.HostFaction == Faction.OfPlayer)
                && (HealthAIUtility.ShouldBeTendedNowByPlayer(p) || Recovering(p)))
            .Concat(surgical).Distinct().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var colonyBeds = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        foreach (var patient in patients)
        {
            // A recovering patient has no demand row (nothing to tend): its id keys its decision only.
            var tendable = HealthAIUtility.ShouldBeTendedNowByPlayer(patient);
            var id = tendable ? AddRoom(patient, patient.Position, "care", served: Serving(map, job => job.def == JobDefOf.TendPatient && job.targetA.Thing == patient))
                : "care:" + patient.GetUniqueLoadID();
            var current = patient.CurrentBed();
            var operated = surgical.Contains(patient);
            // A recovering patient in a sleeping spot, and a surgical one in any bed, is listed with that bed and the nearest beds it
            // could move to; a surgical one also with the bed the native would operate it in (RestUtility.FindPatientBedFor).
            var beds = current == null ? CareBeds(patient, colonyBeds)
                : tendable && !operated ? new[] { current } : new[] { current }.Concat(CareBeds(patient, colonyBeds.Where(b => b != current).ToArray())).ToArray();
            if (operated && NativePawnDetails.SurgeryBed(patient) is { } operatedIn && !beds.Contains(operatedIn)) beds = beds.Append(operatedIn).ToArray();
            var site = tendable ? FieldSite(patient, current) : new Obs.CareFieldSpot.None(operated && !Recovering(patient) ? "surgery" : "recovering");
            carePatients.Add(new(id, patient.GetUniqueLoadID(), OccupantOf(patient), PostureOf(patient, current), ConductOf(patient))
            {
                Beds = EquatableArray.From(beds.Select(bed => CareBed(patient, bed))), Tendable = tendable, Bleeding = BleedingOf(patient),
                Temperature = TemperatureAt(patient.Position), ComfortMin = patient.GetStatValue(StatDefOf.ComfyTemperatureMin),
                ComfortMax = patient.GetStatValue(StatDefOf.ComfyTemperatureMax), FieldSpot = site,
            });
            if (!tendable) continue;
            foreach (var pawn in roster)
            {
                if (!Available(pawn)) continue;
                // A colonist with no tend speed cannot tend; nothing tendable now leaves no batch to give.
                var speed = pawn.GetStatValue(StatDefOf.MedicalTendSpeed);
                var count = patient.health.hediffSet.hediffs.Count(h => h.TendableNow());
                if (speed <= 0 || count == 0) continue;
                var tend = TendGiver(pawn, patient);
                // The medicine this doctor could fetch for this patient, scanned once and shared by every place the tend could happen.
                var medicines = Medicines(pawn, patient);
                // Where the patient lies now (GoodLayingStatusForTend: a humanlike patient in bed, an animal lying down, or a self-tend).
                if (tend != null && pawn.CanReserveAndReach(patient, PathEndMode.Touch, Danger.None))
                    Tends(pawn, patient, id, tend, "", patient.Position, medicines, medicine => TendUtility.CalculateBaseTendQuality(pawn, patient, medicine));
                // In a bed the patient is moved to first: WorkGiver_Tend's checks short of GoodLayingStatusForTend, the doctor reaching the bed.
                if (current != null || pawn == patient || BedTendGiver(pawn) is not { } bedTend || !pawn.CanReserve(patient)) continue;
                foreach (var bed in beds) Tends(pawn, patient, id, bedTend, bed.GetUniqueLoadID(), bed.Position, medicines, medicine => TendQualityIn(pawn, medicine, bed));
                // On the field spot once it stands: the spot def's own MedicalTendQualityOffset (none in vanilla), never an invented ward's.
                if (site is Obs.CareFieldSpot.Site spot)
                    Tends(pawn, patient, id, bedTend, spot.Id, new IntVec3(spot.Cell.X, 0, spot.Cell.Z), medicines,
                        medicine => TendQualityAt(pawn, medicine, ThingDefOf.SleepingSpot.GetStatValueAbstract(StatDefOf.MedicalTendQualityOffset)));
            }
        }
        findings.Add("routes_are_conditional_duration_estimates");
        findings.Add("surgery_mixed_or_unique_ingredients_unassessed");
        findings.Add("care_one_native_tend_batch_remaining_wounds_require_observed_reconsideration");
        foreach (var patient in surgical)
        {
            foreach (var bill in patient.BillStack.Bills.OfType<Bill_Medical>().Where(b => b.ShouldDoNow()).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
            {
            var id = AddRoom(patient, patient.Position, "surgery", bill.GetUniqueLoadID(), Serving(map, job => job.def == JobDefOf.DoBill && job.bill == bill));
            demands[^1] = SurgeryOutcomesOf(demands[^1], patient, bill);
            // The bed the patient is operated in, and the factor SurgeryOutcomeComp_BedAndRoomQuality takes from it.
            if (NativePawnDetails.SurgeryBed(patient) is { } surgeryBed)
                demands[^1] = demands[^1] with { SurgeryBed = surgeryBed.GetUniqueLoadID(), SurgerySuccessFactor = surgeryBed.GetStatValue(StatDefOf.SurgerySuccessChanceFactor) };
            foreach (var pawn in roster.Where(Available))
            {
                var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(patient));
                if (giver == null) continue;
                var skillWork = NativeProductiveSkill.ReadSurgery(pawn, bill.recipe);
                var sources = map.listerThings.AllThings.Where(t => t.Spawned && t.def.category == ThingCategory.Item
                    && bill.IsFixedOrAllowedIngredient(t) && bill.recipe.ingredients.Count > 0 && bill.recipe.ingredients.All(i => i.filter.Allows(t))).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
                foreach (var source in sources)
                {
                    var count = bill.recipe.ingredients.Sum(i => (int)i.CountRequiredOfFor(source.def, bill.recipe, bill));
                    if (count <= 0 || source.stackCount < count) continue;
                    var recipe = new PawnActivityRecipe(bill.GetUniqueLoadID(), bill.recipe.defName, [new(source.GetUniqueLoadID(), count)]) { Resource = source.def.defName };
                    if (!NativeSupportSurgery.Resolve(pawn, patient, giver, recipe, out var job)) continue;
                    JobMaker.ReturnToPool(job);
                    // A surgeon with no operation speed cannot operate.
                    var speed = bill.recipe.workSpeedStat == null ? 1 : pawn.GetStatValue(bill.recipe.workSpeedStat);
                    if (speed <= 0 || Add(Route(pawn, pawn.Position, source.Position, PathEndMode.Touch), Route(pawn, source.Position, patient.Position, PathEndMode.Touch)) is not { } walk) continue;
                    workers.Add(new(id, pawn.GetUniqueLoadID(), patient.GetUniqueLoadID(), "DoBill", giver.defName, At(patient.Position),
                        walk, (double)bill.recipe.WorkAmountTotal(null) / speed)
                    {
                        ServicePosition = legs.ServicePosition(pawn, source.Position, patient.Position, PathEndMode.Touch),
                        Medicine = new(source.GetUniqueLoadID(), source.def.defName, count, At(source.Position), source.stackCount),
                        Treatment = new RimGovernor.Brain.GameFacts.Observations.SupportTreatment.Surgery(recipe, SurgeryQualityOf(pawn, patient, bill.recipe, bill.Part, bill), skillWork)
                    });
                }
            }
        }
        }
        return new() { Demands = EquatableArray.From(demands),
            Workers = EquatableArray.From(workers), Deliveries = EquatableArray.From(deliveries), Findings = EquatableArray.From(findings.OrderBy(x => x, StringComparer.Ordinal)),
            Patients = EquatableArray.From(carePatients) };

        // The medicine stacks the patient's care allows and the doctor can fetch, in id order, and none: one scan per doctor and
        // patient, whichever bed or spot the tend happens at.
        Thing?[] Medicines(Pawn pawn, Pawn patient) => NoMedicine(patient) ? new Thing?[] { null } : map.listerThings.AllThings
            .Where(t => t.Spawned && t.def.IsMedicine && patient.playerSettings != null && patient.playerSettings.medCare.AllowsMedicine(t.def)
                && !t.IsForbidden(pawn) && pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.None))
            .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Cast<Thing?>().Append(null).ToArray();
        // One worker per medicine (and none), its walk ending where the patient is tended.
        void Tends(Pawn pawn, Pawn patient, string id, WorkGiverDef giver, string careBed, IntVec3 at, Thing?[] medicines, Func<ThingDef?, float> quality)
        {
            var learning = NativeSkillLearning.Read(pawn, pawn.skills.GetSkill(SkillDefOf.Medicine));
            var speed = pawn.GetStatValue(StatDefOf.MedicalTendSpeed);
            // One selected native treatment batch. Remaining wounds need fresh observation and selection.
            foreach (var medicine in medicines)
            {
                var approach = medicine == null ? Route(pawn, pawn.Position, at, PathEndMode.Touch)
                    : Add(Route(pawn, pawn.Position, medicine.Position, PathEndMode.Touch), Route(pawn, medicine.Position, at, PathEndMode.Touch));
                if (approach is not { } walk) continue;
                workers.Add(new(id, pawn.GetUniqueLoadID(), patient.GetUniqueLoadID(), "TendPatient", giver.defName,
                    At(at), walk, Math.Ceiling(600d / speed))
                {
                    ServicePosition = legs.ServicePosition(pawn, medicine?.Position ?? pawn.Position, at, PathEndMode.Touch),
                    CareBed = careBed,
                    Medicine = medicine == null ? null : new(medicine.GetUniqueLoadID(), medicine.def.defName, 1, At(medicine.Position), medicine.stackCount),
                    Treatment = new RimGovernor.Brain.GameFacts.Observations.SupportTreatment.Tend(
                        quality(medicine?.def),
                        Infection(patient, medicine != null),
                        new(learning, SkillDefOf.Medicine.defName,
                            RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveTendLearning(patient.RaceProps.Animal, medicine?.def), "native Toils_Tend.FinalizeTend"))
                });
            }
        }
        RimGovernor.Brain.GameFacts.Observations.SupportCareBed CareBed(Pawn patient, Building_Bed bed) =>
            new(bed.GetUniqueLoadID(), At(bed.Position), bed.SleepingSlotsCount)
            {
                Medical = bed.Medical, Prisoners = bed.ForPrisoners, Slaves = bed.ForSlaves,
                Humanlike = bed.def.building.bed_humanlike,
                Occupants = EquatableArray.From(bed.CurOccupants.Select(o => o.GetUniqueLoadID())),
                UsableNow = RestUtility.CanUseBedNow(bed, patient, checkSocialProperness: false),
                Movement = MovementTo(patient, bed),
                InfectionChanceFactor = Stat(bed.GetRoom(), RoomStatDefOf.InfectionChanceFactor),
                Temperature = TemperatureAt(bed.Position), HealPerDay = bed.def.building.bed_healPerDay,
                // The factor SurgeryOutcomeComp_BedAndRoomQuality reads in this bed: def, facilities, cleanliness, glow, outdoors, quality.
                SurgerySuccessFactor = bed.Spawned && bed.GetStatValue(StatDefOf.SurgerySuccessChanceFactor) is var factor && !float.IsNaN(factor)
                    ? new Obs.CareSurgeryFactor.Read(factor) : new Obs.CareSurgeryFactor.Unread("surgery_success_factor:" + bed.GetUniqueLoadID()),
            };
        // The nearest cell to a patient lying in no bed where a sleeping spot can be placed facing south, clear of pawns, that the
        // patient reaches (task 4.4), searched outward as far as the game's radial pattern goes (GenRadial.MaxRadialPatternRadius);
        // the preview and the receipt decide the placement itself.
        Obs.CareFieldSpot FieldSite(Pawn patient, Building_Bed? current)
        {
            if (current != null) return new Obs.CareFieldSpot.None("in_bed");
            if (!patient.RaceProps.Humanlike) return new Obs.CareFieldSpot.None("animal_tended_lying");
            if (patient.IsPrisonerOfColony) return new Obs.CareFieldSpot.None("prisoner_needs_prison_bed");
            var def = ThingDefOf.SleepingSpot;
            foreach (var cell in GenRadial.RadialCellsAround(patient.Position, GenRadial.MaxRadialPatternRadius, true))
            {
                if (!cell.InBounds(map) || cell.Fogged(map)) continue;
                var rect = GenAdj.OccupiedRect(cell, Rot4.South, def.size);
                if (!rect.InBounds(map) || rect.Cells.Any(c => c.GetFirstPawn(map) != null)) continue;
                if (!GenConstruct.CanPlaceBlueprintAt(def, cell, Rot4.South, map, false, null, null, null).Accepted) continue;
                if (MovementToCell(patient, cell) is not { } movement || movement is Obs.CareMovement.Unreachable) continue;
                return new Obs.CareFieldSpot.Site("field_spot:" + cell.x + "," + cell.z, At(cell), movement,
                    Stat(cell.GetRoom(map), RoomStatDefOf.InfectionChanceFactor), TemperatureAt(cell), def.building.bed_healPerDay);
            }
            return new Obs.CareFieldSpot.None("no_spot_cell");
        }
        Obs.CareTemperature TemperatureAt(IntVec3 cell) => GenTemperature.TryGetTemperatureForCell(cell, map, out var celsius)
            ? new Obs.CareTemperature.Read(celsius) : new Obs.CareTemperature.Unread("cell_temperature:" + cell.x + "," + cell.z);
        // How the patient reaches the bed: lying in it, its own walk (a hosted pawn's own path grid: its doors are not a colonist's),
        // or the shortest rescue by a free colonist able to do Doctor work (WorkGiver_RescueDowned's carry).
        RimGovernor.Brain.GameFacts.Observations.CareMovement MovementTo(Pawn patient, Building_Bed bed)
        {
            if (patient.CurrentBed() == bed) return new RimGovernor.Brain.GameFacts.Observations.CareMovement.AlreadyHere();
            return MovementToCell(patient, bed.Position);
        }
        // The patient's own walk to the cell, or the shortest carry there.
        RimGovernor.Brain.GameFacts.Observations.CareMovement MovementToCell(Pawn patient, IntVec3 cell)
        {
            if (!patient.Downed && patient.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                return (patient.IsFreeColonist ? Route(patient, patient.Position, cell, PathEndMode.OnCell) : OwnWalk(map, patient, cell)) is { } walk
                    ? new RimGovernor.Brain.GameFacts.Observations.CareMovement.Walk(walk) : new RimGovernor.Brain.GameFacts.Observations.CareMovement.Unreachable();
            var rescue = roster.Where(r => r != patient && Available(r) && !r.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                    && r.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && r.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
                    && r.CanReserveAndReach(patient, PathEndMode.Touch, Danger.None))
                .Select(r => (Rescuer: r, Ticks: Add(Route(r, r.Position, patient.Position, PathEndMode.Touch), Route(r, patient.Position, cell, PathEndMode.OnCell))))
                .Where(c => c.Ticks != null).OrderBy(c => c.Ticks).ThenBy(c => c.Rescuer.GetUniqueLoadID(), StringComparer.Ordinal).FirstOrDefault();
            return rescue.Rescuer != null ? new RimGovernor.Brain.GameFacts.Observations.CareMovement.Carried(rescue.Rescuer.GetUniqueLoadID(), rescue.Ticks!.Value)
                : new RimGovernor.Brain.GameFacts.Observations.CareMovement.Unreachable();
        }

        double? Route(Pawn pawn, IntVec3 from, IntVec3 to, PathEndMode mode) => legs.Cost(pawn, from, to, mode);
        string AddRoom(Thing target, IntVec3 location, string kind, string suffix = "", bool served = false)
        {
            var id = kind + ":" + target.GetUniqueLoadID() + (suffix.Length == 0 ? "" : ":" + suffix);
            // A bench demand's target is a building, which does not bleed.
            demands.Add(Demand(id, kind, target.GetUniqueLoadID(), location, Stat(location.GetRoom(map), RoomStatDefOf.FoodPoisonChance),
                target is Pawn patient ? patient.health.hediffSet.BleedRateTotal : 0, served));
            return id;
        }
        RimGovernor.Brain.GameFacts.Observations.SupportDemand Demand(string id, string kind, string target, IntVec3 location, double poison, double bleed, bool served)
        {
            var room = location.GetRoom(map);
            return new(id, kind, target, At(location), poison, bleed)
            {
                InfectionChanceFactor = Stat(room, RoomStatDefOf.InfectionChanceFactor), SurgerySuccessFactor = 0, SurgeryBed = "", Served = served,
                SurgeryOutcomes = [], SurgeryEnvironment = false, SurgeryQualityMin = 0, SurgeryQualityMax = float.MaxValue
            };
        }
    }
    /// <summary>
    /// A surgery bill's outcome model (#3804): its recipe's surgeryOutcomeEffect outcomes in GetOutcome's order, whether the bed and
    /// room comp applies and the clamp range. Read-only: no outcome is applied and no Rand is drawn.
    /// </summary>
    private static RimGovernor.Brain.GameFacts.Observations.SupportDemand SurgeryOutcomesOf(RimGovernor.Brain.GameFacts.Observations.SupportDemand demand, Pawn patient, Bill_Medical bill)
    {
        var (rows, min, max, environment) = SurgeryModel(patient, bill.recipe, bill.Part, false);
        if (rows == null) return demand with { SurgeryQualityMax = float.MaxValue };
        return demand with { SurgeryOutcomes = EquatableArray.From(rows), SurgeryQualityMin = min, SurgeryQualityMax = max, SurgeryEnvironment = environment };
    }

    /// <summary>
    /// The outcome model of <paramref name="recipe"/> on <paramref name="part"/> (#3804; null rows without an outcome effect), the part
    /// taken as missing when <paramref name="partMissing"/> (an organ replacement, #3830).
    /// </summary>
    internal static (List<RimGovernor.Brain.GameFacts.Observations.SurgeryOutcomeRead>? Rows, float Min, float Max, bool Environment) SurgeryModel(Pawn patient, RecipeDef recipe, BodyPartRecord? part, bool partMissing)
    {
        if (recipe.surgeryOutcomeEffect is not { } effect) return (null, 0, float.MaxValue, false);
        var comps = effect.comps ?? new List<SurgeryOutcomeComp>();
        var clamp = comps.OfType<SurgeryOutcomeComp_ClampToRange>().LastOrDefault()?.range ?? new FloatRange(0, float.MaxValue);
        var deathless = ModsConfig.BiotechActive && patient.genes != null && patient.genes.HasActiveGene(GeneDefOf.Deathless);
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.SurgeryOutcomeRead>();
        foreach (var outcome in effect.outcomes ?? new List<SurgeryOutcome>())
        {
            var wound = WoundSeverity(patient, outcome.applyEffectsToPart ? part : null, partMissing);
            rows.Add(outcome switch
            {
                SurgeryOutcomeSuccess => new("success", 0, outcome.totalDamage, wound),
                SurgeryOutcome_Death => new("death", deathless ? 0 : recipe.deathOnFailedSurgeryChance, outcome.totalDamage, wound),
                SurgeryOutcome_FailureWithHediff hediff => new("failure", hediff.failedHediff != null && (hediff.applyToRecipes.NullOrEmpty() || hediff.applyToRecipes.Contains(recipe)) ? outcome.chance : 0, outcome.totalDamage, wound),
                SurgeryOutcome_Failure => new("failure", outcome.chance, outcome.totalDamage, wound),
                _ => new(outcome.GetType().Name, outcome.chance, outcome.totalDamage, wound),
            });
        }
        return (rows, clamp.min, clamp.max, !recipe.surgeryIgnoreEnvironment && comps.OfType<SurgeryOutcomeComp_BedAndRoomQuality>().Any());
    }

    /// <summary>
    /// The mean injury GiveRandomSurgeryInjuries deals at a time (#3804): over the parts it may hit (the operated part, its parent and
    /// children, or every part without one; not conceptual; not the operated part when it is taken as missing), by coverageAbs,
    /// max(3, 0.75 x the part's health).
    /// </summary>
    private static double WoundSeverity(Pawn patient, BodyPartRecord? part, bool partMissing)
    {
        var parts = patient.health.hediffSet.GetNotMissingParts().Where(p => !p.def.conceptual
            && (part == null || p == part && !partMissing || p.parent == part && !partMissing || part.parent == p)).ToArray();
        var cover = parts.Sum(p => (double)p.coverageAbs);
        return cover > 0 ? parts.Sum(p => p.coverageAbs * Math.Max(3, 0.75 * patient.health.hediffSet.GetPartHealth(p))) / cover : 3;
    }

    /// <summary>The surgeon's quality without the bed and room comp and the clamp (#3804): each other comp that applies, in order.</summary>
    internal static double SurgeryQualityOf(Pawn surgeon, Pawn patient, RecipeDef recipe, BodyPartRecord? part, Bill_Medical bill)
    {
        var quality = 1f;
        foreach (var comp in recipe.surgeryOutcomeEffect?.comps ?? new List<SurgeryOutcomeComp>())
        {
            if (comp is SurgeryOutcomeComp_BedAndRoomQuality or SurgeryOutcomeComp_ClampToRange || !comp.Affects(recipe, surgeon, patient, part)) continue;
            comp.AffectQuality(recipe, surgeon, patient, new List<Thing>(), part, bill, ref quality);
        }
        return quality;
    }
    /// <summary>Whether a spawned colonist's current job matches <paramref name="serves"/> (SupportDemand.Served): the current job
    /// only, whoever ordered it; a queued job or a finished treatment does not serve the demand now.</summary>
    private static bool Serving(Map map, Func<Job, bool> serves) => map.mapPawns.FreeColonistsSpawned.Any(p => p.CurJob is { } job && serves(job));
    private static double? Add(double? a, double? b) => a is { } x && b is { } y ? x + y : null;
    /// <summary>A room stat of <paramref name="room"/>, its roomlessScore without a room.</summary>
    private static double Stat(Room? room, RoomStatDef stat) => room == null ? stat.roomlessScore : room.GetStat(stat);
    /// <summary>
    /// The beds a patient lying in none could be cared in: every colony bed it fits (RestUtility.CanUseBedEver: humanlike or
    /// animal, body size), nearest first; none is cut.
    /// </summary>
    private static Building_Bed[] CareBeds(Pawn patient, Building_Bed[] colonyBeds) => colonyBeds
        .Where(b => b.Spawned && RestUtility.CanUseBedEver(patient, b.def))
        .OrderBy(b => (b.Position - patient.Position).LengthHorizontalSquared).ThenBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
    /// <summary>Who a bed takes the patient as: RestUtility.CanUseBedNow's guest status, an animal by RaceProps.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.BedOccupant OccupantOf(Pawn patient) => !patient.RaceProps.Humanlike
        ? RimGovernor.Brain.GameFacts.Observations.BedOccupant.Animal : patient.GuestStatus switch
        {
            GuestStatus.Prisoner => RimGovernor.Brain.GameFacts.Observations.BedOccupant.Prisoner,
            GuestStatus.Slave => RimGovernor.Brain.GameFacts.Observations.BedOccupant.Slave,
            GuestStatus.Guest => RimGovernor.Brain.GameFacts.Observations.BedOccupant.Guest,
            _ => RimGovernor.Brain.GameFacts.Observations.BedOccupant.Colonist,
        };
    private static RimGovernor.Brain.GameFacts.Observations.CarePosture PostureOf(Pawn patient, Building_Bed? bed) =>
        bed != null ? new RimGovernor.Brain.GameFacts.Observations.CarePosture.InBed(bed.GetUniqueLoadID())
        : patient.GetPosture() != PawnPosture.Standing ? new RimGovernor.Brain.GameFacts.Observations.CarePosture.Lying()
        : new RimGovernor.Brain.GameFacts.Observations.CarePosture.Standing();
    /// <summary>Whether the patient lets a doctor treat it (<see cref="RimGovernor.Brain.GameFacts.Observations.CareConduct"/>).</summary>
    private static RimGovernor.Brain.GameFacts.Observations.CareConduct ConductOf(Pawn patient) =>
        PrisonBreakUtility.IsPrisonBreaking(patient) ? RimGovernor.Brain.GameFacts.Observations.CareConduct.PrisonBreaking
        : patient.InAggroMentalState && !patient.health.hediffSet.HasHediff(HediffDefOf.Scaria) ? RimGovernor.Brain.GameFacts.Observations.CareConduct.Aggressive
        : patient.HostileTo(Faction.OfPlayer) ? RimGovernor.Brain.GameFacts.Observations.CareConduct.Hostile
        : patient.CurJobDef == JobDefOf.Flee || patient.CurJobDef == JobDefOf.FleeAndCower || patient.CurJobDef == JobDefOf.FleeAndCowerShort
            ? RimGovernor.Brain.GameFacts.Observations.CareConduct.Fleeing : RimGovernor.Brain.GameFacts.Observations.CareConduct.Calm;
    /// <summary>A hosted patient's own walk: its own traverse parameters (a prisoner's doors are not a colonist's), so not a shared leg.</summary>
    private static double? OwnWalk(Map map, Pawn pawn, IntVec3 to)
    {
        if (!pawn.CanReach(to, PathEndMode.OnCell, Danger.Deadly)) return null;
        using var path = map.pathFinder.FindPathNow(pawn.Position, to, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
        return path.Found ? path.TotalCost : null;
    }
    /// <summary>
    /// TendUtility.CalculateBaseTendQuality with the patient lying in <paramref name="bed"/> rather than where it is now: the
    /// doctor's MedicalTendQuality times the medicine's potency (0.3 without), plus that bed's MedicalTendQualityOffset (its def,
    /// linked facilities and room), clamped to the medicine's MedicalQualityMax (0.7 without). Never a self-tend.
    /// </summary>
    private static float TendQualityIn(Pawn doctor, ThingDef? medicine, Building_Bed bed) => TendQualityAt(doctor, medicine, bed.GetStatValue(StatDefOf.MedicalTendQualityOffset));
    /// <summary>The same tend quality with the bed's MedicalTendQualityOffset given (a sleeping spot's def value before it stands).</summary>
    private static float TendQualityAt(Pawn doctor, ThingDef? medicine, float offset) => Math.Min(Math.Max(0f,
        doctor.GetStatValue(StatDefOf.MedicalTendQuality) * (medicine?.GetStatValueAbstract(StatDefOf.MedicalPotency) ?? 0.3f)
            + offset), medicine?.GetStatValueAbstract(StatDefOf.MedicalQualityMax) ?? 0.7f);
    /// <summary>
    /// A patient recovering rather than waiting on a tend (task 4.4): not downed, HealthAIUtility.ShouldSeekMedicalRest, lying in no
    /// bed or in a sleeping spot, so a transfer to a better bed is a later step of its care.
    /// </summary>
    private static bool Recovering(Pawn patient) => !patient.Downed && HealthAIUtility.ShouldSeekMedicalRest(patient)
        && (patient.CurrentBed() is not { } bed || bed.def == ThingDefOf.SleepingSpot);
    /// <summary>Its BleedRateTotal and Verse.HealthUtility.TicksUntilDeathDueToBloodLoss; Stable under the native's 0.0001 floor.</summary>
    private static Obs.CareBleeding BleedingOf(Pawn patient) => patient.health.hediffSet.BleedRateTotal is var rate && rate < 0.0001f
        ? new Obs.CareBleeding.Stable() : new Obs.CareBleeding.Bleeding(rate, Verse.HealthUtility.TicksUntilDeathDueToBloodLoss(patient));
    private static Cell At(IntVec3 cell) => new(cell.x, cell.z);
    private static bool Available(Pawn pawn) => !pawn.Downed && !pawn.Drafted && !pawn.InMentalState && NativeFoodActivity.Available(pawn)
        && !NativePawnActivities.Owned(pawn) && pawn.CurJob?.playerForced != true && pawn.jobs.jobQueue.Count == 0 && pawn.carryTracker.CarriedThing == null;
    /// <summary>The selected batch's earliest pending native infection check (HediffComp_Infecter.ticksUntilInfect, -1 once the
    /// wound's check has run), or None when no wound in the batch has one.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.InfectionCheck Infection(Pawn patient, bool medicine)
    {
        var batch = new List<Hediff>();
        TendUtility.GetOptimalHediffsToTendWithSingleTreatment(patient, medicine, batch);
        var pending = new List<int>();
        var timer = HarmonyLib.AccessTools.Field(typeof(HediffComp_Infecter), "ticksUntilInfect")
            ?? throw new InvalidOperationException("infecter_timer_missing");
        foreach (var wound in batch)
        {
            var infecter = wound.TryGetComp<HediffComp_Infecter>();
            if (infecter == null) continue;
            var ticks = timer.GetValue(infecter) as int? ?? throw new InvalidOperationException("infecter_timer_unread");
            if (ticks == -1) continue;
            if (ticks <= 0 || patient.health.hediffSet.HasRegeneration
                || patient.health.immunity.DiseaseContractChanceFactor(HediffDefOf.WoundInfection, wound.Part) <= .001f
                || patient.Faction == Faction.OfPlayer && Find.Storyteller.difficulty.playerPawnInfectionChanceFactor <= 0) continue;
            if (wound.TryGetComp<HediffComp_TendDuration>() == null) continue;
            pending.Add(ticks);
        }
        return pending.Count == 0 ? new RimGovernor.Brain.GameFacts.Observations.InfectionCheck.None() : new RimGovernor.Brain.GameFacts.Observations.InfectionCheck.Pending(pending.Min());
    }

    private static bool CanWork(Pawn pawn, WorkGiver giver) => giver.MissingRequiredCapacity(pawn) == null
        && !pawn.WorkTagIsDisabled(giver.def.workTags) && (giver.def.workType == null || !pawn.WorkTypeIsDisabled(giver.def.workType));
    // Native tend subclasses have different patient/urgency predicates. The first registered
    // subclass is not a general tending capability. Equivalent legal givers execute the same
    // exact treatment batch; retain one stable legal identity per worker/patient pair.
    internal static WorkGiverDef? TendGiver(Pawn pawn, Pawn patient) => DefDatabase<WorkGiverDef>.AllDefsListForReading
        .Where(d => d.Worker is WorkGiver_Tend giver && CanWork(pawn, giver) && giver.HasJobOnThing(pawn, patient, false))
        .OrderBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();
    /// <summary>The general tend giver (class WorkGiver_Tend itself, not an urgent or self subclass) the doctor may work.</summary>
    private static WorkGiverDef? BedTendGiver(Pawn pawn) => DefDatabase<WorkGiverDef>.AllDefsListForReading
        .Where(d => d.Worker is WorkGiver_Tend giver && giver.GetType() == typeof(WorkGiver_Tend) && CanWork(pawn, giver))
        .OrderBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();
    internal static bool NoMedicine(Pawn patient) => patient.playerSettings?.medCare == MedicalCareCategory.NoMeds;
    private static int Required(Bill_Production bill, ThingDef def) => (int)Math.Ceiling(bill.recipe.ingredients.Sum(i => (double)i.CountRequiredOfFor(def, bill.recipe, bill)));
    // A finished product drops where the cook stands, the interaction cell. Staging there is blocked by it for every later delivery,
    // so stage on the nearest cell that is free of unrelated items; the bill finds ingredients anywhere within its radius.
    internal static IntVec3 Staging(Map map, Building_WorkTable bench, ThingDef def) =>
        Staging(map, bench, c => c.GetThingList(map).All(t => t.def.category != ThingCategory.Item || t.def == def));
    /// <summary>The nearest staging cell <paramref name="free"/> admits; a strict-exact-destination delivery admits only an item-free cell.</summary>
    internal static IntVec3 Staging(Map map, Building_WorkTable bench, Func<IntVec3, bool> free) =>
        GenRadial.RadialCellsAround(bench.InteractionCell, 1.5f, false).Where(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && map.haulDestinationManager.SlotGroupAt(c) == null
            && free(c)).Cast<IntVec3?>().FirstOrDefault() ?? bench.InteractionCell;
    private static int Staged(Map map, IntVec3 destination, ThingDef def) => destination.GetThingList(map).Where(t => t.def == def).Sum(t => t.stackCount);
    internal static int Remaining(Map map, PawnActivityStep step, ThingDef resource)
    {
        if (step.SupportDemand.Length == 0) return int.MaxValue;
        var bench = RefIndex.Thing<Building_WorkTable>(map, step.Container);
        if (bench == null || !bench.Spawned || step is not CellActivityStep { Destination: var destination }) return 0;
        var bill = bench.BillStack.Bills.OfType<Bill_Production>().FirstOrDefault(b => "bill:" + b.GetUniqueLoadID() + ":" + resource.defName == step.SupportDemand);
        if (bill == null || !bill.ShouldDoNow() || !bill.IsFixedOrAllowedIngredient(resource) || !bill.recipe.ingredients.All(i => i.filter.Allows(resource))) return 0;
        return Math.Max(0, Required(bill, resource) - Staged(map, new(destination.X, 0, destination.Z), resource));
    }
    internal static bool DestinationAllowed(Pawn pawn, Thing source, IntVec3 cell, int count) => DestinationRefusal(pawn, source, cell, count) == null;
    // Names the failed condition so a repeated refusal is diagnosable from the receipt.
    internal static string? DestinationRefusal(Pawn pawn, Thing source, IntVec3 cell, int count)
    {
        if (!cell.InBounds(pawn.Map) || !cell.Standable(pawn.Map) || cell.Fogged(pawn.Map) || cell.IsForbidden(pawn)) return "cell not standable, fogged or forbidden";
        if (!pawn.CanReserveAndReach(cell, PathEndMode.OnCell, Danger.None))
            return pawn.Map.reservationManager.FirstRespectedReserver(cell, pawn) is { } holder ? "cell reserved by " + holder.GetUniqueLoadID() : "cell unreachable";
        var group = pawn.Map.haulDestinationManager.SlotGroupAt(cell);
        if (group != null && !StoreUtility.IsGoodStoreCell(cell, pawn.Map, source, pawn, pawn.Faction)) return "stockpile cell not accepting";
        var blocker = cell.GetThingList(pawn.Map).FirstOrDefault(t => t.def.category == ThingCategory.Item && (!t.CanStackWith(source) || t.stackCount + count > t.def.stackLimit));
        return blocker == null ? null : "cell holds " + blocker.def.defName + " x" + blocker.stackCount;
    }
}











