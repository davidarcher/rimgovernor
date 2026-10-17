#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Cell = RimGovernor.Brain.Domain.Cell;
namespace HomeBridge.BridgeTools;

// One native census of every free colonist against every bench bill, patient, surgery and room filth (#3341). These are
// conditional mechanics and current bill deficits, never new demand. A path is searched once for every colonist (NativePathLegs).
internal static class NativeSupportActivities
{
    internal static Obs.SupportActivityRead Read(Map map, NativePathLegs? legs = null)
    {
        legs ??= new(map);
        var demands = new List<Obs.SupportDemand>(); var filthRows = new List<Obs.SupportFilth>();
        var workers = new List<Obs.SupportWorker>(); var deliveries = new List<Obs.SupportDelivery>();
        var findings = new HashSet<string>(StringComparer.Ordinal);
        var roster = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var clean = NativeCleanOperations.Giver();
        var benches = map.listerThings.AllThings.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayer
            && b.BillStack.Bills.OfType<Bill_Production>().Any(bill => bill.ShouldDoNow())).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        foreach (var bench in benches)
        {
            AddRoom(bench, bench.InteractionCell, "bench");
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
                    if (!demands.Any(d => d.Id == id)) demands.Add(new(id, "delivery", bench.GetUniqueLoadID(), At(destination), default, default, default, default));
                    foreach (var pawn in roster)
                    {
                        if (!Available(pawn) || source.IsForbidden(pawn) || bench.IsForbidden(pawn)
                            || !NativeProductionBills.Usable(bench) || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                            || pawn.WorkTagIsDisabled(WorkTags.ManualDumb) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                            || !pawn.CanReserveAndReach(source, PathEndMode.Touch, Danger.None)
                            || !DestinationAllowed(pawn, source, destination, Math.Min(deficit, source.stackCount))) continue;
                        var approach = Route(pawn, pawn.Position, source.Position, PathEndMode.Touch);
                        var travel = Route(pawn, source.Position, destination, PathEndMode.OnCell);
                        deliveries.Add(new(id, pawn.GetUniqueLoadID(), source.GetUniqueLoadID(), source.def.defName,
                            bench.GetUniqueLoadID(), bill.GetUniqueLoadID(), At(source.Position), At(destination), deficit,
                            source.stackCount, pawn.carryTracker.MaxStackSpaceEver(source.def), approach, travel));
                    }
                }
            }
        }
        var patients = map.mapPawns.SpawnedPawnsWithAnyHediff.Where(p => p.Faction == Faction.OfPlayer && HealthAIUtility.ShouldBeTendedNowByPlayer(p))
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        foreach (var patient in patients)
        {
            var id = AddRoom(patient, patient.Position, "care");
            if (patient.RaceProps.Humanlike && !patient.InBed()) findings.Add("care_patient_not_in_bed_external_doctor_unavailable:" + id);
            foreach (var pawn in roster)
            {
                if (!Available(pawn)) continue;
                var tend = TendGiver(pawn, patient);
                if (tend == null || !pawn.CanReserveAndReach(patient, PathEndMode.Touch, Danger.None)) continue;
                var medicines = NoMedicine(patient) ? new Thing?[] { null } : map.listerThings.AllThings
                    .Where(t => t.Spawned && t.def.IsMedicine && patient.playerSettings != null && patient.playerSettings.medCare.AllowsMedicine(t.def)
                        && !t.IsForbidden(pawn) && pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.None))
                    .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Cast<Thing?>().Append(null).ToArray();
                var speed = pawn.GetStatValue(StatDefOf.MedicalTendSpeed);
                // One selected native treatment batch. Remaining wounds need fresh observation and selection.
                var count = patient.health.hediffSet.hediffs.Count(h => h.TendableNow());
                foreach (var medicine in medicines)
                {
                var infection = InfectionEffect(patient, medicine != null);
                workers.Add(new(id, pawn.GetUniqueLoadID(), patient.GetUniqueLoadID(), "TendPatient", tend.defName,
                    At(patient.Position), medicine == null ? Route(pawn, pawn.Position, patient.Position, PathEndMode.Touch)
                        : Add(Route(pawn, pawn.Position, medicine.Position, PathEndMode.Touch), Route(pawn, medicine.Position, patient.Position, PathEndMode.Touch)),
                    speed > 0 && count > 0 ? Fact.Known(Math.Ceiling(600d / speed)) : default) { Medicine = medicine?.GetUniqueLoadID() ?? "", MedicineResource = medicine?.def.defName ?? "", MedicineCount = medicine == null ? 0 : 1, MedicineLocation = medicine == null ? null : At(medicine.Position), MedicineAvailable = medicine?.stackCount ?? 0,
                        TendQuality = Fact.Known((double)TendUtility.CalculateBaseTendQuality(pawn, patient, medicine?.def)),
                        InfectionRoomEffectApplies = infection.Applies, InfectionCheckTicks = infection.Ticks });
                }
            }
        }
        findings.Add("routes_and_cleaning_are_conditional_duration_estimates");
        findings.Add("surgery_mixed_or_unique_ingredients_unassessed");
        findings.Add("care_one_native_tend_batch_remaining_wounds_require_observed_reconsideration");
        foreach (var patient in map.mapPawns.FreeColonistsSpawned.Where(p => p.BillStack?.Bills.OfType<Bill_Medical>().Any(b => b.ShouldDoNow()) == true).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            foreach (var bill in patient.BillStack.Bills.OfType<Bill_Medical>().Where(b => b.ShouldDoNow()).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
            {
            var id = AddRoom(patient, patient.Position, "surgery", bill.GetUniqueLoadID());
            foreach (var pawn in roster.Where(Available))
            {
                var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(patient));
                if (giver == null) continue;
                var sources = map.listerThings.AllThings.Where(t => t.Spawned && t.def.category == ThingCategory.Item
                    && bill.IsFixedOrAllowedIngredient(t) && bill.recipe.ingredients.Count > 0 && bill.recipe.ingredients.All(i => i.filter.Allows(t))).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
                foreach (var source in sources)
                {
                    var count = bill.recipe.ingredients.Sum(i => (int)i.CountRequiredOfFor(source.def, bill.recipe, bill));
                    if (count <= 0 || source.stackCount < count) continue;
                    var recipe = new PawnActivityRecipe(bill.GetUniqueLoadID(), bill.recipe.defName, [new(source.GetUniqueLoadID(), count)]) { Resource = source.def.defName };
                    var step = new PawnActivityStep(PawnActivityKind.Work, patient.GetUniqueLoadID(), giver.defName, "DoBill", 0, Find.TickManager.TicksGame + 60000) { Recipe = recipe };
                    if (!NativeSupportSurgery.Resolve(pawn, patient, step, out var job)) continue;
                    JobMaker.ReturnToPool(job!);
                    var speed = bill.recipe.workSpeedStat == null ? 1 : pawn.GetStatValue(bill.recipe.workSpeedStat);
                    workers.Add(new(id, pawn.GetUniqueLoadID(), patient.GetUniqueLoadID(), "DoBill", giver.defName, At(patient.Position),
                        Add(Route(pawn, pawn.Position, source.Position, PathEndMode.Touch), Route(pawn, source.Position, patient.Position, PathEndMode.Touch)),
                        speed > 0 ? Fact.Known((double)bill.recipe.WorkAmountTotal(null) / speed) : default) { Recipe = recipe,
                            Medicine = source.GetUniqueLoadID(), MedicineResource = source.def.defName, MedicineCount = count, MedicineLocation = At(source.Position), MedicineAvailable = source.stackCount });
                }
            }
        }
        }
        return new() { Known = true, Demands = EquatableArray.From(demands), Filth = EquatableArray.From(filthRows),
            Workers = EquatableArray.From(workers), Deliveries = EquatableArray.From(deliveries), Findings = EquatableArray.From(findings.OrderBy(x => x, StringComparer.Ordinal)) };

        Fact<double> Route(Pawn pawn, IntVec3 from, IntVec3 to, PathEndMode mode) =>
            legs.Cost(pawn, from, to, mode) is { } ticks ? Fact.Known((double)ticks) : default;
        string AddRoom(Thing target, IntVec3 location, string kind, string suffix = "")
        {
            var id = kind + ":" + target.GetUniqueLoadID() + (suffix.Length == 0 ? "" : ":" + suffix); var room = location.GetRoom(map);
            var all = room?.ContainedAndAdjacentThings.OfType<Filth>().Where(f => f.Spawned).OrderBy(f => f.GetUniqueLoadID(), StringComparer.Ordinal).ToArray() ?? [];
            var cleanliness = room == null ? default : Fact.Known((double)room.GetStat(RoomStatDefOf.Cleanliness));
            var poison = room == null ? Fact.Known((double)RoomStatDefOf.FoodPoisonChance.roomlessScore) : Fact.Known((double)room.GetStat(RoomStatDefOf.FoodPoisonChance));
            var delta = 0d;
            foreach (var filth in all)
            {
                var change = room?.CellCount > 0 ? Fact.Known(-(double)filth.stackCount * filth.GetStatValue(StatDefOf.Cleanliness) / room.CellCount) : default;
                if (change.IsKnown) delta += change.Value;
                filthRows.Add(new(id, filth.GetUniqueLoadID(), At(filth.Position), change));
                foreach (var pawn in roster)
                {
                    if (!Available(pawn) || clean == null || !CanWork(pawn, clean) || !clean.HasJobOnThing(pawn, filth, false)
                        || !pawn.CanReserveAndReach(filth, PathEndMode.Touch, Danger.None)) continue;
                    var speed = pawn.GetStatValue(StatDefOf.CleaningSpeed);
                    var factor = filth.Position.GetTerrain(map).GetStatValueAbstract(StatDefOf.CleaningTimeFactor);
                    workers.Add(new(id, pawn.GetUniqueLoadID(), filth.GetUniqueLoadID(), "Clean", clean.def.defName, At(filth.Position),
                        Route(pawn, pawn.Position, filth.Position, PathEndMode.Touch), speed > 0
                            ? Fact.Known((Math.Floor(filth.def.filth.cleaningWorkToReduceThickness * (factor == 0 ? 1 : factor) / speed) + 1) * filth.thickness) : default));
                }
            }
            var definition = RoomStatDefOf.FoodPoisonChance;
            var surgeryFactor = DefDatabase<RoomStatDef>.GetNamedSilentFail("SurgerySuccessChanceCleanlinessFactor");
            Fact<double> after = cleanliness.IsKnown && definition.inputStat == RoomStatDefOf.Cleanliness && definition.curve != null
                ? Fact.Known((double)definition.curve.Evaluate((float)(cleanliness.Value + delta))) : default;
            demands.Add(new(id, kind, target.GetUniqueLoadID(), At(location), cleanliness, poison,
                target is Pawn patient ? Fact.Known((double)patient.health.hediffSet.BleedRateTotal) : default, default)
                { FoodPoisonChanceClean = after,
                    SurgerySuccessFactor = room != null && surgeryFactor != null ? Fact.Known((double)room.GetStat(surgeryFactor)) : default,
                    SurgerySuccessFactorClean = cleanliness.IsKnown && surgeryFactor?.inputStat == RoomStatDefOf.Cleanliness && surgeryFactor.curve != null
                        ? Fact.Known((double)surgeryFactor.curve.Evaluate((float)(cleanliness.Value + delta))) : default,
                    InfectionChanceFactor = room == null ? default : Fact.Known((double)room.GetStat(RoomStatDefOf.InfectionChanceFactor)),
                    InfectionChanceFactorClean = cleanliness.IsKnown && RoomStatDefOf.InfectionChanceFactor.inputStat == RoomStatDefOf.Cleanliness && RoomStatDefOf.InfectionChanceFactor.curve != null
                        ? Fact.Known((double)RoomStatDefOf.InfectionChanceFactor.curve.Evaluate((float)(cleanliness.Value + delta))) : default });
            return id;
        }
    }
    private static Fact<double> Add(Fact<double> a, Fact<double> b) => a.IsKnown && b.IsKnown ? Fact.Known(a.Value + b.Value) : default;
    private static Cell At(IntVec3 cell) => new(cell.x, cell.z);
    private static bool Available(Pawn pawn) => !pawn.Downed && !pawn.Drafted && !pawn.InMentalState && NativeFoodActivity.Available(pawn)
        && !NativePawnActivities.Owned(pawn) && pawn.CurJob?.playerForced != true && pawn.jobs.jobQueue.Count == 0 && pawn.carryTracker.CarriedThing == null;
    private static (Fact<bool> Applies, Fact<double> Ticks) InfectionEffect(Pawn patient, bool medicine)
    {
        var batch = new List<Hediff>();
        TendUtility.GetOptimalHediffsToTendWithSingleTreatment(patient, medicine, batch);
        if (batch.Count == 0) return default;
        var pending = new List<int>();
        var timer = HarmonyLib.AccessTools.Field(typeof(HediffComp_Infecter), "ticksUntilInfect");
        foreach (var wound in batch)
        {
            var infecter = wound.TryGetComp<HediffComp_Infecter>();
            if (infecter == null) continue;
            if (infecter.GetType() != typeof(HediffComp_Infecter) || timer?.GetValue(infecter) is not int ticks)
                return default;
            if (ticks == -1) return default;
            if (ticks <= 0 || patient.health.hediffSet.HasRegeneration
                || patient.health.immunity.DiseaseContractChanceFactor(HediffDefOf.WoundInfection, wound.Part) <= .001f
                || patient.Faction == Faction.OfPlayer && Find.Storyteller.difficulty.playerPawnInfectionChanceFactor <= 0) continue;
            if (wound.TryGetComp<HediffComp_TendDuration>() == null) continue;
            pending.Add(ticks);
        }
        return pending.Count == 0 ? (Fact.Known(false), Fact.Known(0d)) : (Fact.Known(true), Fact.Known((double)pending.Min()));
    }

    private static bool CanWork(Pawn pawn, WorkGiver giver) => giver.MissingRequiredCapacity(pawn) == null
        && !pawn.WorkTagIsDisabled(giver.def.workTags) && (giver.def.workType == null || !pawn.WorkTypeIsDisabled(giver.def.workType));
    // Native tend subclasses have different patient/urgency predicates. The first registered
    // subclass is not a general tending capability. Equivalent legal givers execute the same
    // exact treatment batch; retain one stable legal identity per worker/patient pair.
    internal static WorkGiverDef? TendGiver(Pawn pawn, Pawn patient) => DefDatabase<WorkGiverDef>.AllDefsListForReading
        .Where(d => d.Worker is WorkGiver_Tend giver && CanWork(pawn, giver) && giver.HasJobOnThing(pawn, patient, false))
        .OrderBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();
    internal static bool NoMedicine(Pawn patient) => patient.playerSettings?.medCare == MedicalCareCategory.NoMeds;
    private static int Required(Bill_Production bill, ThingDef def) => (int)Math.Ceiling(bill.recipe.ingredients.Sum(i => (double)i.CountRequiredOfFor(def, bill.recipe, bill)));
    // A finished product drops where the cook stands, the interaction cell. Staging there is blocked by it for every later delivery,
    // so stage on the nearest cell that is free of unrelated items; the bill finds ingredients anywhere within its radius.
    internal static IntVec3 Staging(Map map, Building_WorkTable bench, ThingDef def) =>
        GenRadial.RadialCellsAround(bench.InteractionCell, 1.5f, false).Where(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && map.haulDestinationManager.SlotGroupAt(c) == null
            && c.GetThingList(map).All(t => t.def.category != ThingCategory.Item || t.def == def)).Cast<IntVec3?>().FirstOrDefault() ?? bench.InteractionCell;
    private static int Staged(Map map, IntVec3 destination, ThingDef def) => destination.GetThingList(map).Where(t => t.def == def).Sum(t => t.stackCount);
    internal static int Remaining(Map map, PawnActivityStep step, ThingDef resource)
    {
        if (step.SupportDemand.Length == 0) return int.MaxValue;
        var bench = map.listerThings.AllThings.OfType<Building_WorkTable>().FirstOrDefault(b => b.GetUniqueLoadID() == step.Container);
        if (bench == null || !bench.Spawned || step.Destination is not { } destination) return 0;
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











