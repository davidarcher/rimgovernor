using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Cell = RimGovernor.Brain.Domain.Cell;
namespace RimGovernor.Native;

// One native census of every free colonist against every bench bill, patient and surgery (#3341). These are
// conditional mechanics and current bill deficits, never new demand. A path is searched once for every colonist (NativePathLegs).
internal static class NativeSupportActivities
{
    internal static RimGovernor.Brain.GameFacts.Observations.SupportActivityRead Read(Map map, NativePathLegs? legs = null)
    {
        legs ??= new(map);
        var demands = new List<RimGovernor.Brain.GameFacts.Observations.SupportDemand>();
        var workers = new List<RimGovernor.Brain.GameFacts.Observations.SupportWorker>(); var deliveries = new List<RimGovernor.Brain.GameFacts.Observations.SupportDelivery>();
        var findings = new HashSet<string>(StringComparer.Ordinal);
        var roster = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
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
                    if (!demands.Any(d => d.Id == id)) demands.Add(Demand(id, "delivery", bench.GetUniqueLoadID(), destination, 0, 0));
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
                // A colonist with no tend speed cannot tend; nothing tendable now leaves no batch to give.
                var speed = pawn.GetStatValue(StatDefOf.MedicalTendSpeed);
                var count = patient.health.hediffSet.hediffs.Count(h => h.TendableNow());
                if (speed <= 0 || count == 0) continue;
                var learning = NativeSkillLearning.Read(pawn, pawn.skills.GetSkill(SkillDefOf.Medicine));
                // One selected native treatment batch. Remaining wounds need fresh observation and selection.
                foreach (var medicine in medicines)
                {
                    var approach = medicine == null ? Route(pawn, pawn.Position, patient.Position, PathEndMode.Touch)
                        : Add(Route(pawn, pawn.Position, medicine.Position, PathEndMode.Touch), Route(pawn, medicine.Position, patient.Position, PathEndMode.Touch));
                    if (approach is not { } walk) continue;
                    workers.Add(new(id, pawn.GetUniqueLoadID(), patient.GetUniqueLoadID(), "TendPatient", tend.defName,
                        At(patient.Position), walk, Math.Ceiling(600d / speed))
                    {
                        ServicePosition = legs.ServicePosition(pawn, medicine?.Position ?? pawn.Position, patient.Position, PathEndMode.Touch),
                        Medicine = medicine == null ? null : new(medicine.GetUniqueLoadID(), medicine.def.defName, 1, At(medicine.Position), medicine.stackCount),
                        Treatment = new RimGovernor.Brain.GameFacts.Observations.SupportTreatment.Tend(TendUtility.CalculateBaseTendQuality(pawn, patient, medicine?.def), Infection(patient, medicine != null),
                            new(learning, SkillDefOf.Medicine.defName,
                                RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveTendLearning(patient.RaceProps.Animal, medicine?.def), "native Toils_Tend.FinalizeTend"))
                    });
                }
            }
        }
        findings.Add("routes_are_conditional_duration_estimates");
        findings.Add("surgery_mixed_or_unique_ingredients_unassessed");
        findings.Add("care_one_native_tend_batch_remaining_wounds_require_observed_reconsideration");
        foreach (var patient in map.mapPawns.FreeColonistsSpawned.Where(p => p.BillStack?.Bills.OfType<Bill_Medical>().Any(b => b.ShouldDoNow()) == true).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            foreach (var bill in patient.BillStack.Bills.OfType<Bill_Medical>().Where(b => b.ShouldDoNow()).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
            {
            var id = AddRoom(patient, patient.Position, "surgery", bill.GetUniqueLoadID());
            demands[^1] = SurgeryOutcomesOf(demands[^1], patient, bill);
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
                    var step = new PawnActivityStep(PawnActivityKind.Work, patient.GetUniqueLoadID(), giver.defName, "DoBill", 0, Find.TickManager.TicksGame + 60000) { Recipe = recipe };
                    if (!NativeSupportSurgery.Resolve(pawn, patient, step, out var job)) continue;
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
            Workers = EquatableArray.From(workers), Deliveries = EquatableArray.From(deliveries), Findings = EquatableArray.From(findings.OrderBy(x => x, StringComparer.Ordinal)) };

        double? Route(Pawn pawn, IntVec3 from, IntVec3 to, PathEndMode mode) => legs.Cost(pawn, from, to, mode);
        string AddRoom(Thing target, IntVec3 location, string kind, string suffix = "")
        {
            var id = kind + ":" + target.GetUniqueLoadID() + (suffix.Length == 0 ? "" : ":" + suffix);
            // A bench demand's target is a building, which does not bleed.
            demands.Add(Demand(id, kind, target.GetUniqueLoadID(), location, Stat(location.GetRoom(map), RoomStatDefOf.FoodPoisonChance),
                target is Pawn patient ? patient.health.hediffSet.BleedRateTotal : 0));
            return id;
        }
        RimGovernor.Brain.GameFacts.Observations.SupportDemand Demand(string id, string kind, string target, IntVec3 location, double poison, double bleed)
        {
            var room = location.GetRoom(map);
            return new(id, kind, target, At(location), poison, bleed)
            {
                InfectionChanceFactor = Stat(room, RoomStatDefOf.InfectionChanceFactor), SurgerySuccessFactor = Stat(room, SurgeryRoomStat()),
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
    private static double? Add(double? a, double? b) => a is { } x && b is { } y ? x + y : null;
    /// <summary>A room stat of <paramref name="room"/>, its roomlessScore without a room.</summary>
    private static double Stat(Room? room, RoomStatDef stat) => room == null ? stat.roomlessScore : room.GetStat(stat);
    /// <summary>The room stat StatDefOf.SurgerySuccessChanceFactor multiplies: no RoomStatDefOf field names it; the stat's
    /// StatPart_RoomStat holds it, the typed route.</summary>
    private static RoomStatDef SurgeryRoomStat() => StatDefOf.SurgerySuccessChanceFactor.parts?.OfType<StatPart_RoomStat>()
        .Select(part => RimGovernor.Brain.Bridge.GameMembers.RoomStat(part)).FirstOrDefault(stat => stat != null)
        ?? throw new InvalidOperationException("surgery_room_stat_missing");
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
        if (!NativePawnActivities.Reservable(pawn, cell, PathEndMode.OnCell, Danger.None))
            return pawn.Map.reservationManager.FirstRespectedReserver(cell, pawn) is { } holder ? "cell reserved by " + holder.GetUniqueLoadID() : "cell unreachable";
        var group = pawn.Map.haulDestinationManager.SlotGroupAt(cell);
        if (group != null && !StoreUtility.IsGoodStoreCell(cell, pawn.Map, source, pawn, pawn.Faction)) return "stockpile cell not accepting";
        var blocker = cell.GetThingList(pawn.Map).FirstOrDefault(t => t.def.category == ThingCategory.Item && (!t.CanStackWith(source) || t.stackCount + count > t.def.stackLimit));
        return blocker == null ? null : "cell holds " + blocker.def.defName + " x" + blocker.stackCount;
    }
}











