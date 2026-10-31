using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One way a care patient's bed is provided (task 4.2 of <c>redefine-habitat-requirements</c>), compared by its effects rather than
/// picked from a ladder: a ready medical bed reused, the place it lies now, its own bed or another free bed converted to medical,
/// a medical sleeping spot placed where it lies, a tend where it lies before a later transfer (task 4.4), or no bed yet. New
/// capacity is the layout comparison's (<see cref="LayoutHospitalBedRules"/>): a planned medical bed the comparator chose is built
/// through the planned-site path, and once it stands it is another bed to convert.
/// </summary>
public enum CareCapacityKind
{
    /// <summary>A medical bed that stands ready for the patient (CareBedState.Ready); nothing is ordered.</summary>
    Reuse,

    /// <summary>The bed the patient lies in now, or where it lies when in none; nothing is ordered.</summary>
    InPlace,

    /// <summary>The patient's own bed made medical (Building_Bed.Medical evicts its owners).</summary>
    ConvertOwn,

    /// <summary>Another free legal bed made medical, only when no sleeper loses its slot for it.</summary>
    ConvertOther,

    /// <summary>
    /// A sleeping spot placed at the patient's field site (<see cref="CareFieldSpot.Site"/>), made medical once it stands, the patient
    /// moved there and tended there (task 4.4); once placed, the spot is this patient's bed: its own stats, no sleeper's slot.
    /// </summary>
    FieldSpot,

    /// <summary>
    /// Tended where it stands now (a self-tend), then walked to a ready medical bed: the transfer is a later step, bound only once
    /// the census reads the patient stabilized (no longer tendable, its bleeding stopped).
    /// </summary>
    Stabilize,

    /// <summary>No bed now: the wounds go untended until a later review.</summary>
    Defer,
}

/// <summary>
/// What an alternative does to the patient's care: the factor its best tend puts on the wounds' infection chance
/// (<see cref="HarmConsequences.TendInfectionFactor"/> of the tend quality given there, the bed's MedicalTendQualityOffset and linked
/// facilities included, and the room's InfectionChanceFactor) or 1 when the tend lands after the batch's pending infection check,
/// the ticks until that tend ends (the patient's move and the doctor's walk, then the tend) and the orders it takes. Task 4.4:
/// <see cref="Exposure"/> is the hypothermia and heatstroke severity the places it lies in add over the comparison's horizon
/// (<see cref="ForecastValue.ThermalGainPerTick"/> at each place's temperature against the patient's comfort range);
/// <see cref="HealPerDay"/> is the natural healing where it settles (Pawn_HealthTracker: 8, 4 more lying, plus the bed's
/// bed_healPerDay). A recovering patient's alternative has no tend: weight 0, its tend ticks the move. <see cref="BleedsOut"/> marks
/// the one alternative kept although no tend reaches a bleeding patient in time (Defer): every other such alternative is excluded.
/// Task 4.5: <see cref="SurgeryLoss"/> is what the patient's queued operations are expected to cost in the bed the alternative has
/// them done in (<see cref="CareCapacityRules.SurgeryLossOf"/>): each operation's failure outcomes at its actual chance there, and the
/// move to that bed; infinite when the alternative leaves an operation no bed; 0 for a patient with none queued.
/// </summary>
public sealed record CareEffect(double InfectionWeight, double TendTicks, double TendQuality, double RoomInfection, int Orders, double Exposure, double HealPerDay)
{
    public bool BleedsOut { get; init; }

    public double SurgeryLoss { get; init; }
}

/// <summary>An alternative's standing: assessed with its effects, excluded by a named constraint, or not assessed for a named unread fact.</summary>
public abstract record CareAssessment
{
    private CareAssessment() { }

    public sealed record Assessed(CareEffect Effect) : CareAssessment;

    /// <summary>A constraint rules it out: a care bed state, another sleeper losing its slot, a bed another patient took or a refused order.</summary>
    public sealed record Excluded(string Reason) : CareAssessment;

    /// <summary>A fact it needs is unread (rule 9): it is never chosen and names the fact.</summary>
    public sealed record Unassessed(string Fact) : CareAssessment;
}

public sealed record CareCapacityOption(CareCapacityKind Kind, string Bed, CareAssessment Assessment);

/// <summary>One care patient's compared alternatives and the one chosen: the best assessed, Defer when none is.</summary>
public sealed record CareCapacityDecision(string Demand, string Patient, EquatableArray<CareCapacityOption> Options, CareCapacityOption Chosen);

/// <summary>
/// One Building_Bed.Medical order the comparison makes (a BedUse intent): Medical true converts a bed for a patient or the goal's bed
/// floor, false restores a medical bed nobody is cared in once care ended. Reason names what asked for it: the patient's demand,
/// <see cref="CareCapacityRules.FloorReason"/> or <see cref="CareCapacityRules.RestoreReason"/>.
/// </summary>
public sealed record CareBedOrder(string Bed, bool Medical, string Reason);

/// <summary>A bed order the native refused in the current world (its receipt's refusal): a fact the comparison reads, never retried in that world.</summary>
public sealed record RefusedCareOrder(string Bed, bool Medical, string Reason);

/// <summary>
/// The facts the care capacity comparison reads: the native care census (patients, their beds and the doctors' tends there), the
/// sleeping census the slots are counted in, the room census and the goal's bed floor the hosted medical beds are counted against,
/// the surgical patients (a sleeping spot never counts while an operation waits on a better bed), the refused orders and the cells of
/// the planned medical beds the layout comparison chose.
/// </summary>
public sealed record CareCapacityRequest
{
    public required SupportActivityRead Support { get; init; }
    public SleepingObservation Sleeping { get; init; } = new();
    public RoomObservation Rooms { get => field ?? throw new RequiredFactException("rooms"); init; }
    public IReadOnlyList<PawnId> Surgical { get; init; } = [];

    /// <summary>The hosted medical beds a Hospital goal asks for whatever the patients (#3496); 0 for none.</summary>
    public int MinBeds { get; init; }

    public IReadOnlyList<RefusedCareOrder> Refused { get; init; } = [];

    /// <summary>The beds the patients' transports hold now (<see cref="CareTransportRules.Holds"/>): no other patient, bed order or sleeper takes them.</summary>
    public IReadOnlyList<CareBedHold> Holds { get; init; } = [];

    /// <summary>The transports the native refused in this world under the refusal budget: each excludes its patient's bed.</summary>
    public IReadOnlyList<RefusedCareTransport> RefusedTransports { get; init; } = [];

    /// <summary>The cells of the layout plan's medical bed pieces (StationPurpose.SleepMed): capacity the layout comparison chose.</summary>
    public IReadOnlySet<Cell> PlannedMedical { get; init; } = new HashSet<Cell>();

    /// <summary>The value constants an operation's failure is priced in (<see cref="ForecastValuePolicy.DeathTerminalValue"/>, task 4.5).</summary>
    public ForecastValuePolicy Value { get; init; } = new();

    /// <summary>The field spots the care Need placed for its patients (task 4.4), re-derived from the journal: in flight or standing.</summary>
    public IReadOnlyList<CareFieldChain> FieldSpots { get; init; } = [];

    /// <summary>The patients whose field spot placement the native refused in this world under the refusal budget: each excludes its new spot.</summary>
    public IReadOnlyList<RefusedCareFieldSpot> RefusedFieldSpots { get; init; } = [];
}

/// <summary>
/// One patient's field spot placement (task 4.4) as the journal holds it: the cell its spot was placed at, and whether the receipt
/// was accepted under the current native authority (Placed) or the placement is still open (in flight).
/// </summary>
public sealed record CareFieldChain(string Patient, Cell Cell, bool Placed);

/// <summary>A field spot placement the native refused for the patient in the current world: a fact the comparison reads, never retried there.</summary>
public sealed record RefusedCareFieldSpot(string Patient, string Reason);

/// <summary>One field spot to place: the patient it is for and the cell of its sleeping spot, facing south.</summary>
public sealed record CareFieldPlacement(string Patient, Cell Cell);

/// <summary>
/// Every patient's decision and the bed orders they make, in the order they are issued (patients first, then the floor, then restores),
/// and the field spots to place (task 4.4).
/// </summary>
public sealed record CareCapacityPlan(EquatableArray<CareCapacityDecision> Decisions, EquatableArray<CareBedOrder> Orders)
{
    public EquatableArray<CareFieldPlacement> Placements { get; init; } = [];
}

public static class HospitalRules
{
    /// <summary>
    /// The living patients who need a hosted medical bed: those whose health read says they need rest, and the
    /// surgical patients. A hospital bed serves medical rest; the medical/care Need's wider census (any bad
    /// condition, e.g. a scar) keeps the deficit and does not size the ward.
    /// </summary>
    internal static HashSet<PawnId> IllPatients(EquatableArray<CarePawn> patients, IReadOnlyList<PawnId> surgical)
    {
        var ill = new HashSet<PawnId>();
        foreach (var p in patients)
        {
            if (!p.Dead && p.NeedsRest)
            {
                ill.Add(p.Id);
            }
        }
        foreach (var id in surgical)
        {
            ill.Add(id);
        }
        return ill;
    }

    /// <summary>The beds in the complete native room census, regardless of its scored role.</summary>
    private static HashSet<string> HostedBeds(RoomObservation rooms) =>
        new(rooms.Rooms.SelectMany(room => room.BedIds()), StringComparer.Ordinal);

    /// <summary>Whether a bed counts towards the ward: hosted, humanlike and no prisoner's, and not a sleeping spot while an operation waits on a better bed.</summary>
    private static bool Eligible(SleepingBed bed, HashSet<string> hosted, bool surgical) =>
        hosted.Contains(bed.Id) && !(surgical && bed.Definition.Value == SleepingRules.SleepingSpotDefinition.ToString()) && bed.Humanlike && !bed.Prisoners;

    /// <summary>The hosted medical beds standing.</summary>
    public static int HostedMedicalBeds(RoomObservation rooms, SleepingObservation sleeping, bool surgical)
    {
        var hosted = HostedBeds(rooms);
        return sleeping.Beds.Count(b => b.Medical && Eligible(b, hosted, surgical));
    }

    /// <summary>
    /// Whether the hosted medical beds fall below a Hospital goal's bed floor, the unmet half of the care Need when no
    /// patient is sick (#3496); a known false without a floor.
    /// </summary>
    public static bool BedsShort(int floor, RoomObservation rooms, SleepingObservation sleeping, bool surgical) =>
        floor > 0 && HostedMedicalBeds(rooms, sleeping, surgical) < floor;

    /// <summary>The cells of the plan's medical bed pieces (StationPurpose.SleepMed), each piece's footprint; none without a plan.</summary>
    public static IReadOnlySet<Cell> PlannedMedicalCells(LayoutPlan? plan) =>
        (plan?.Pieces.Values ?? []).SelectMany(pieces => pieces).Where(p => PlannedRoomCredits.PurposeOf(p) == StationPurpose.SleepMed)
            .SelectMany(p => RegionRules.RectRegion(p.Rect).Cells()).ToHashSet();

    /// <summary>
    /// The planned medical bed still to build: the first room by id whose plan holds a medical bed piece (StationPurpose.SleepMed)
    /// and more pieces of that def than its built and sited ones, with the def. Null when every planned medical bed stands or the
    /// plan holds none. The def and count are the layout comparison's (<see cref="LayoutHospitalBedRules"/>); this binds them.
    /// </summary>
    public static (PlannedRoom Room, string Def)? UnbuiltMedicalBed(LayoutPlan? plan, CurrentConstruction census)
    {
        if (plan is null) return null;
        foreach (var room in plan.Rooms.OrderBy(r => r.Id))
        {
            var pieces = plan.Pieces.GetValueOrDefault(room.Id) ?? [];
            foreach (var def in pieces.Where(p => PlannedRoomCredits.PurposeOf(p) == StationPurpose.SleepMed).Select(p => p.Def).Distinct().Order(StringComparer.Ordinal))
            {
                var standing = census.Buildings.Count(b => b.Building.Definition == def && room.Region.Cells().Contains(b.Building.Cell))
                    + census.Sites.Count(s => s.Building is { } b && b.Definition == def && room.Region.Cells().Contains(b.Cell));
                if (pieces.Count(p => p.Def == def) > standing) return (room, def);
            }
        }
        return null;
    }
}

/// <summary>
/// The care capacity comparison (task 4.2 of <c>redefine-habitat-requirements</c>). Each care patient's alternatives
/// (<see cref="CareCapacityKind"/>) are priced by their effects on its care and ranked: the lower infection weight first, then the
/// earlier tend, then fewer orders. A conversion is excluded when the converted census (the bed medical and its owners evicted, as
/// Building_Bed.Medical does) leaves more sleepers short of a compatible slot than now (<see cref="SleepingRules.SlotShortfall"/>): a
/// partner sharing the patient's bed, or the owner of another bed, keeps its slot unless another one is free for it. The patient
/// itself counts once: while it seeks medical rest its converted bed holds it (<see cref="SleepingRules.HeldInMedicalBeds"/>). A
/// bed one patient takes is no slot for the next. With no patient choosing it and nobody lying in it, a medical bed is re-decided
/// each review (no store remembers whose bed it was): restored when that gives a sleeper short of a slot one and the hosted medical
/// beds stay at the goal's floor, kept as medical otherwise. Below the floor, a free bed whose conversion costs no slot is
/// converted, a planned medical bed's first. A refused order excludes its alternative in that world.
/// A bed a patient's transport holds (task 4.3, <see cref="CareTransportRules.Holds"/>) is that patient's: it is taken for every
/// other patient, never converted for the floor or restored, and its slot counts as lying there for the sleeping census. A bed a
/// walking patient that cannot be ordered would walk to is excluded naming the occupant, and a refused transport excludes its bed.
/// Task 4.4 prices each alternative's delay and exposure: one whose tend ends after the patient bleeds out is excluded
/// (<see cref="BleedsOut"/>); the hypothermia or heatstroke it adds over a common horizon ranks first, then the infection weight, the
/// natural healing where the patient settles, the earlier tend and fewer orders. A field spot (<see cref="CareCapacityKind.FieldSpot"/>)
/// is placed, made medical and moved to; a stabilizing self-tend (<see cref="CareCapacityKind.Stabilize"/>) moves the patient only once
/// a later census reads it recovering, when its transfer is that review's choice. A recovering patient is moved to standing medical
/// capacity only (<see cref="RecoveringReadyBedsOnly"/>).
/// Task 4.5 compares where a patient's queued operations are done (<see cref="SurgeryLossOf"/>): any legal bed it may be cared in,
/// a medical one or not, priced at the actual chance there (the surgeon's quality before the bed, times that bed's
/// SurgerySuccessChanceFactor, clamped), with the move to it; no hospital room label is asked for. A transfer to a better bed is
/// chosen when the failures it saves outweigh the move; otherwise the operation is done where the native would do it.
/// </summary>
public static class CareCapacityRules
{
    /// <summary>The order reason of a bed converted for the Hospital goal's bed floor.</summary>
    public const string FloorReason = "hospital_bed_floor";

    /// <summary>The order reason of a medical bed restored to sleeping once care ended.</summary>
    public const string RestoreReason = "post_care_restore";

    /// <summary>The exclusion of a conversion that leaves another sleeper without a compatible slot.</summary>
    public const string DisplacesSleeper = "displaces_sleeper";

    /// <summary>The exclusion of a bed an earlier patient's decision took.</summary>
    public const string BedTakenByPatient = "bed_taken_by_patient";

    /// <summary>The exclusion of a bed no doctor's tend reaches.</summary>
    public const string NoTend = "no_reaching_doctor";

    /// <summary>The exclusion of an alternative whose tend ends at or after the patient's TicksUntilDeathDueToBloodLoss.</summary>
    public const string BleedsOut = "bleeds_out_before_tend";

    /// <summary>The exclusion of a recovering patient's place out of bed: ShouldSeekMedicalRest asks for a bed.</summary>
    public const string RestNeedsBed = "rest_requires_bed";

    /// <summary>The exclusion of a conversion for a recovering patient: its transfer goes to medical capacity that stands.</summary>
    public const string RecoveringReadyBedsOnly = "recovering_ready_beds_only";

    /// <summary>The fact prefix of an alternative whose place's temperature is unread.</summary>
    public const string TemperatureFact = "temperature:";

    /// <summary>The fact prefix of a placed field spot the census does not list.</summary>
    public const string FieldSpotBedFact = "care_bed:field_spot:";

    /// <summary>The fact prefix of the bed the native would operate a patient in (SupportDemand.SurgeryBed) when its care beds do not list it.</summary>
    public const string SurgeryBedFact = "surgery_bed:";

    /// <summary>The fact prefix of a field spot not yet placed, whose SurgerySuccessChanceFactor no bed reads.</summary>
    public const string FieldSpotSurgeryFact = "surgery_success_factor:field_spot:";

    /// <summary>The ticks past the last alternative's settling that exposure is compared over: one day's rest.</summary>
    public const double RestHorizonTicks = GameTime.TicksPerDay;

    private const double Tolerance = 1e-9;

    public static CareCapacityPlan Plan(CareCapacityRequest r)
    {
        var sleeping = Holding(r.Sleeping, r.Holds);
        var sites = CareSites.Of(r.Support);
        var decisions = new List<CareCapacityDecision>();
        var orders = new List<CareBedOrder>();
        var placements = new List<CareFieldPlacement>();
        // Who takes each bed: the patients whose transports hold it, then each patient whose decision chose it.
        var taken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Take(string bed, string who)
        {
            if (!taken.TryGetValue(bed, out var takers)) taken[bed] = takers = new HashSet<string>(StringComparer.Ordinal);
            takers.Add(who);
        }
        foreach (var hold in r.Holds) Take(hold.Bed, hold.Patient);
        foreach (var patient in r.Support.Patients.OrderBy(p => p.Demand, StringComparer.Ordinal))
        {
            var options = Options(r, sleeping, sites, patient, taken);
            var chosen = options.Where(o => o.Assessment is CareAssessment.Assessed)
                .OrderBy(o => o, Comparer<CareCapacityOption>.Create(Compare)).First();
            decisions.Add(new(patient.Demand, patient.Patient, EquatableArray.From(options), chosen));
            if (chosen.Bed.Length != 0) Take(chosen.Bed, patient.Patient);
            if (chosen.Kind == CareCapacityKind.FieldSpot && patient.FieldSpot is CareFieldSpot.Site site && chosen.Bed == site.Id)
            {
                placements.Add(new(patient.Patient, site.Cell));
            }
            else if (chosen.Kind is CareCapacityKind.ConvertOwn or CareCapacityKind.ConvertOther
                || chosen.Kind == CareCapacityKind.FieldSpot && patient.Beds.Any(b => b.Id == chosen.Bed && !b.Medical))
            {
                orders.Add(new(chosen.Bed, true, patient.Demand));
                sleeping = Converted(sleeping, chosen.Bed, true);
            }
        }
        var surgical = r.Surgical.Count > 0;
        // The goal's floor: a free bed whose conversion costs no sleeper its slot, a planned medical bed's first.
        while (HospitalRules.HostedMedicalBeds(r.Rooms, sleeping, surgical) < r.MinBeds && FloorBed(r, sleeping, taken) is { } floor)
        {
            orders.Add(new(floor.Id, true, FloorReason));
            Take(floor.Id, FloorReason);
            sleeping = Converted(sleeping, floor.Id, true);
        }
        // Post-care: a medical bed nobody is cared in, restored when that gives a short sleeper a slot.
        foreach (var bed in sleeping.Beds.Where(b => b.Medical && b.Humanlike && !b.Crib && !b.Prisoners && b.Users.Count == 0 && !taken.ContainsKey(b.Id)
            && !r.PlannedMedical.Contains(b.Cell) && !Refused(r, b.Id, false)).OrderBy(b => b.Id, StringComparer.Ordinal).ToArray())
        {
            var restored = Converted(sleeping, bed.Id, false);
            if (Short(restored) < Short(sleeping) && HospitalRules.HostedMedicalBeds(r.Rooms, restored, surgical) >= r.MinBeds)
            {
                orders.Add(new(bed.Id, false, RestoreReason));
                sleeping = restored;
            }
        }
        return new(EquatableArray.From(decisions), EquatableArray.From(orders)) { Placements = EquatableArray.From(placements) };
    }

    /// <summary>
    /// An alternative that tends in time first, then the lower exposure, then the lower surgery loss, then the lower infection weight, then the faster natural healing, then the earlier tend, then fewer
    /// orders, then the bed id: a total order over assessed alternatives.
    /// </summary>
    public static int Compare(CareCapacityOption a, CareCapacityOption b)
    {
        var x = ((CareAssessment.Assessed)a.Assessment).Effect;
        var y = ((CareAssessment.Assessed)b.Assessment).Effect;
        if (x.BleedsOut != y.BleedsOut) return x.BleedsOut.CompareTo(y.BleedsOut);
        if (x.Exposure != y.Exposure && !(Math.Abs(x.Exposure - y.Exposure) <= Tolerance)) return x.Exposure.CompareTo(y.Exposure);
        if (x.SurgeryLoss != y.SurgeryLoss && !(Math.Abs(x.SurgeryLoss - y.SurgeryLoss) <= Tolerance)) return x.SurgeryLoss.CompareTo(y.SurgeryLoss);
        if (Math.Abs(x.InfectionWeight - y.InfectionWeight) > Tolerance) return x.InfectionWeight.CompareTo(y.InfectionWeight);
        if (Math.Abs(x.HealPerDay - y.HealPerDay) > Tolerance) return y.HealPerDay.CompareTo(x.HealPerDay);
        if (Math.Abs(x.TendTicks - y.TendTicks) > Tolerance) return x.TendTicks.CompareTo(y.TendTicks);
        if (x.Orders != y.Orders) return x.Orders.CompareTo(y.Orders);
        return string.CompareOrdinal(a.Bed, b.Bed);
    }

    /// <summary>
    /// One alternative before its exposure is priced: its effect so far and the places the patient lies in, each from the tick it
    /// gets there (the first from 0, where it lies now); or its final standing when it is excluded or unassessed.
    /// </summary>
    private sealed record Draft(CareCapacityKind Kind, string Bed, CareAssessment Assessment, IReadOnlyList<(double From, CareTemperature At)> Stays);

    private static List<CareCapacityOption> Options(CareCapacityRequest r, SleepingObservation sleeping, IReadOnlyList<CareSite> sites,
        SupportCarePatient patient, Dictionary<string, HashSet<string>> taken)
    {
        var drafts = new List<Draft>();
        var tends = patient.Tendable ? r.Support.Workers.Where(w => w.Demand == patient.Demand && w.Treatment is SupportTreatment.Tend).ToArray() : [];
        var demand = r.Support.Demands.FirstOrDefault(d => d.Id == patient.Demand);
        var owned = sleeping.Beds.Where(b => b.Owners.Contains(new PawnId(patient.Patient))).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var chain = r.FieldSpots.FirstOrDefault(f => f.Patient == patient.Patient);
        var lyingHeal = patient.Posture is CarePosture.Standing ? HarmConsequences.NaturalHealPerDay : HarmConsequences.LyingHealPerDay;
        void Add(CareCapacityKind kind, string bed, CareAssessment assessment, params (double From, CareTemperature At)[] stays) =>
            drafts.Add(new(kind, bed, assessment, stays));
        foreach (var bed in patient.Beds)
        {
            var site = sites.Single(s => s.Demand == patient.Demand && s.Bed == bed.Id);
            var here = bed.Movement is CareMovement.AlreadyHere;
            // The spot the care Need placed for this patient is its own: it displaces no sleeper and is made medical for it.
            var spot = !here && chain is { Placed: true } && bed.Location == chain.Cell;
            var kind = here ? CareCapacityKind.InPlace : spot ? CareCapacityKind.FieldSpot : bed.Medical ? CareCapacityKind.Reuse
                : owned.Contains(bed.Id) ? CareCapacityKind.ConvertOwn : CareCapacityKind.ConvertOther;
            var convert = kind is CareCapacityKind.ConvertOwn or CareCapacityKind.ConvertOther || spot && !bed.Medical;
            var others = bed.Occupants.Concat(taken.GetValueOrDefault(bed.Id) ?? []).Where(o => o != patient.Patient).ToHashSet(StringComparer.Ordinal);
            // No doctor reaching the bed blocks a tend there, not a stabilizing tend where the patient stands before it walks there.
            var doctorless = site.State == CareBedState.NoReachingDoctor;
            string? blocked = null, later = null;
            CareAssessment? unassessed = null;
            if (!here && r.Holds.Any(h => h.Bed == bed.Id && h.Patient != patient.Patient) && others.Count >= bed.Slots)
                blocked = CareTransportRules.HeldByPatient;
            else if (others.Count >= bed.Slots)
                blocked = BedTakenByPatient;
            else if (site.State != CareBedState.Ready && !(convert && site.State == CareBedState.ConversionRequired) && !doctorless)
                blocked = Wire.Name(site.State);
            else if (bed.Movement is CareMovement.Walk && !CareTransportRules.Orderable(patient.Occupant))
                later = CareTransportRules.NotOrderable + ":" + Wire.Name(patient.Occupant);
            else if (!here && r.RefusedTransports.FirstOrDefault(t => t.Patient == patient.Patient && t.Bed == bed.Id) is { } transport)
                later = "refused:" + transport.Reason;
            else if (convert && !patient.Tendable && !spot)
                later = RecoveringReadyBedsOnly;
            else if (convert && Refusal(r, bed.Id, true) is { } refused)
                later = "refused:" + refused;
            else if (convert && sleeping.Beds.All(b => b.Id != bed.Id))
                unassessed = new CareAssessment.Unassessed("sleeping_bed:" + bed.Id);
            else if (convert && !spot && Displaces(sleeping, bed.Id, patient.Patient))
                later = DisplacesSleeper;
            var heal = HarmConsequences.LyingHealPerDay + bed.HealPerDay;
            var orders = convert ? 1 : 0;
            // Tended where it stands now, then walked to this ready bed once stabilized.
            if (blocked is null && later is null && unassessed is null && kind == CareCapacityKind.Reuse && bed.Movement is CareMovement.Walk(var walk) && patient.Tendable)
            {
                var selfTend = Effect(tends.Where(w => w.CareBed.Length == 0), 0, demand?.InfectionChanceFactor ?? 0, 0, heal, patient);
                Add(CareCapacityKind.Stabilize, bed.Id, demand is null ? new CareAssessment.Unassessed("support_demand:" + patient.Demand) : selfTend,
                    (0, patient.Temperature), (((selfTend as CareAssessment.Assessed)?.Effect.TendTicks ?? 0) + walk, bed.Temperature));
            }
            if (blocked is null && doctorless) blocked = Wire.Name(site.State);
            blocked ??= later;
            Add(kind, bed.Id, blocked is not null ? new CareAssessment.Excluded(blocked) : unassessed
                ?? Effect(tends.Where(w => w.CareBed == (here ? "" : bed.Id)), site.MovementTicks, bed.InfectionChanceFactor, orders, heal, patient),
                (0, patient.Temperature), (site.MovementTicks, bed.Temperature));
        }
        // The patient's own bed the census did not list (it lists the nearest beds only): its conversion is not assessed.
        foreach (var bed in owned.Where(id => patient.Beds.All(b => b.Id != id)).Order(StringComparer.Ordinal))
        {
            Add(CareCapacityKind.ConvertOwn, bed, new CareAssessment.Unassessed("care_bed:" + bed));
        }
        // A medical sleeping spot placed where it lies (task 4.4): only the spot's own stats, never a ward's.
        // A placed spot the census lists is priced as that bed above; one it does not list is not assessed.
        if (patient.FieldSpot is CareFieldSpot.Site field && patient.Posture is not CarePosture.InBed
            && !(chain is { Placed: true } && patient.Beds.Any(b => b.Location == chain.Cell)))
        {
            var conduct = CareSites.Conduct(patient.Conduct);
            if (chain is { Placed: true })
                Add(CareCapacityKind.FieldSpot, field.Id, new CareAssessment.Unassessed(FieldSpotBedFact + chain.Cell.X + "," + chain.Cell.Z));
            else if (conduct is { } state)
                Add(CareCapacityKind.FieldSpot, field.Id, new CareAssessment.Excluded(Wire.Name(state)));
            else if (r.RefusedFieldSpots.FirstOrDefault(f => f.Patient == patient.Patient) is { } refused)
                Add(CareCapacityKind.FieldSpot, field.Id, new CareAssessment.Excluded("refused:" + refused.Reason));
            else if (field.Movement is CareMovement.Unreachable)
                Add(CareCapacityKind.FieldSpot, field.Id, new CareAssessment.Excluded(Wire.Name(CareBedState.Unreachable)));
            else if (field.Movement is CareMovement.Walk && !CareTransportRules.Orderable(patient.Occupant))
                Add(CareCapacityKind.FieldSpot, field.Id, new CareAssessment.Excluded(CareTransportRules.NotOrderable + ":" + Wire.Name(patient.Occupant)));
            else
            {
                var move = field.Movement is CareMovement.Walk(var w) ? w : field.Movement is CareMovement.Carried(_, var c) ? c : 0;
                // Placed, then made medical: two orders before the patient is moved.
                Add(CareCapacityKind.FieldSpot, field.Id, Effect(tends.Where(t => t.CareBed == field.Id), move, field.InfectionChanceFactor, 2,
                    HarmConsequences.LyingHealPerDay + field.HealPerDay, patient), (0, patient.Temperature), (move, field.Temperature));
            }
        }
        // Lying in no bed: tended where it lies.
        if (patient.Posture is not CarePosture.InBed)
        {
            Add(CareCapacityKind.InPlace, "", !patient.Tendable ? new CareAssessment.Excluded(RestNeedsBed)
                : demand is null ? new CareAssessment.Unassessed("support_demand:" + patient.Demand)
                : Effect(tends.Where(w => w.CareBed.Length == 0), 0, demand.InfectionChanceFactor, 0, lyingHeal, patient), (0, patient.Temperature));
        }
        // No bed now: it stays where it lies, untended.
        Add(CareCapacityKind.Defer, "", new CareAssessment.Assessed(new(1, double.PositiveInfinity, 0, 1, 0, 0, lyingHeal) { BleedsOut = patient.Bleeding is CareBleeding.Bleeding }),
            (0, patient.Temperature));
        return Exposed(Operated(r, drafts, patient), patient);
    }

    /// <summary>
    /// Each assessed alternative's <see cref="CareEffect.SurgeryLoss"/> for a patient with queued operations (task 4.5): the bed it has
    /// them done in is its own bed once the patient lies there medical (a reuse, a conversion, a placed field spot, the bed it walks
    /// to once stabilized, the medical bed it lies in), else the bed the native operates it in (RestUtility.FindPatientBedFor, the
    /// demand's SurgeryBed). A fact that bed's loss needs and the census lacks leaves the alternative unassessed; Defer, which must
    /// stay choosable, is then ranked after every priced loss.
    /// </summary>
    private static List<Draft> Operated(CareCapacityRequest r, List<Draft> drafts, SupportCarePatient patient)
    {
        var surgeries = r.Support.Demands.Where(d => d.Kind == "surgery" && d.Target == patient.Patient).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        if (surgeries.Length == 0) return drafts;
        var native = surgeries[0].SurgeryBed;
        var result = new List<Draft>();
        foreach (var d in drafts)
        {
            if (d.Assessment is not CareAssessment.Assessed { Effect: var effect })
            {
                result.Add(d);
                continue;
            }
            var own = patient.Beds.FirstOrDefault(b => b.Id == d.Bed);
            var moved = d.Kind is CareCapacityKind.Reuse or CareCapacityKind.ConvertOwn or CareCapacityKind.ConvertOther or CareCapacityKind.Stabilize
                or CareCapacityKind.FieldSpot || d.Kind == CareCapacityKind.InPlace && own is { Medical: true };
            (double Loss, string? Fact) at = moved
                ? own is null ? (0, FieldSpotSurgeryFact + d.Bed) : SurgeryLossOf(r, surgeries, own)
                : native.Length == 0 ? (double.PositiveInfinity, null)
                : patient.Beds.FirstOrDefault(b => b.Id == native) is { } bed ? SurgeryLossOf(r, surgeries, bed) : (0, SurgeryBedFact + native);
            var (loss, fact) = at;
            result.Add(fact is null ? d with { Assessment = new CareAssessment.Assessed(effect with { SurgeryLoss = loss }) }
                : d.Kind == CareCapacityKind.Defer ? d with { Assessment = new CareAssessment.Assessed(effect with { SurgeryLoss = double.PositiveInfinity }) }
                : d with { Assessment = new CareAssessment.Unassessed(fact) });
        }
        return result;
    }

    /// <summary>
    /// What a patient's queued operations cost done in <paramref name="bed"/>, in patient-days: the move there (its own ticks walking;
    /// the rescuer's as well when carried), then per operation the outcomes SurgeryOutcomeEffectDef.GetOutcome tries in order at its
    /// chance there (SupportTreatment.Surgery's best quality, times the bed's SurgerySuccessChanceFactor when
    /// SurgeryOutcomeComp_BedAndRoomQuality applies, clamped to the recipe's range): a death the colonist's terminal value, a failure's
    /// totalDamage the days it lies healing at the bed's rate (Pawn_HealthTracker: 12 lying plus bed_healPerDay). An operation no
    /// surgeon can do now costs the same in every bed and is left out. An unread bed factor is returned as its fact.
    /// </summary>
    public static (double Loss, string? Fact) SurgeryLossOf(CareCapacityRequest r, IReadOnlyList<SupportDemand> surgeries, SupportCareBed bed)
    {
        // Lying there already moves nobody; an unreachable bed is excluded before its loss is read.
        var loss = bed.Movement is CareMovement.Walk(var walk) ? walk / GameTime.TicksPerDay
            : bed.Movement is CareMovement.Carried(_, var carry) ? 2 * carry / GameTime.TicksPerDay : 0;
        var heal = HarmConsequences.LyingHealPerDay + bed.HealPerDay;
        foreach (var surgery in surgeries)
        {
            var qualities = r.Support.Workers.Where(w => w.Demand == surgery.Id).Select(w => w.Treatment).OfType<SupportTreatment.Surgery>().Select(t => t.Quality).ToArray();
            if (qualities.Length == 0 || surgery.SurgeryOutcomes.Count == 0) continue;
            var factor = 1d;
            if (surgery.SurgeryEnvironment)
            {
                if (bed.SurgerySuccessFactor is CareSurgeryFactor.Unread unread) return (0, unread.Fact);
                factor = ((CareSurgeryFactor.Read)bed.SurgerySuccessFactor).Factor;
            }
            var success = Math.Clamp(qualities.Max() * factor, surgery.SurgeryQualityMin, surgery.SurgeryQualityMax);
            var left = 1.0;
            foreach (var outcome in surgery.SurgeryOutcomes)
            {
                var share = left * Math.Clamp(outcome.Kind == "success" ? success : outcome.Chance, 0, 1);
                left -= share;
                if (outcome.Kind == "death") loss += share * r.Value.DeathTerminalValue;
                else if (outcome.Kind == "failure" && outcome.Damage > 0) loss += share * outcome.Damage / heal;
            }
        }
        return (loss, null);
    }

    /// <summary>
    /// Each assessed alternative's exposure over the common horizon: the latest settling or tend end among them plus a day's rest
    /// (<see cref="RestHorizonTicks"/>), each place's hypothermia and heatstroke gain a tick times the ticks the patient lies there.
    /// The move's ticks are counted where the patient lies now (the path's cells are not read). An unread temperature leaves its
    /// alternative unassessed, naming the fact; Defer, which must stay choosable, is then ranked after every priced exposure.
    /// </summary>
    private static List<CareCapacityOption> Exposed(List<Draft> drafts, SupportCarePatient patient)
    {
        var assessed = drafts.Where(d => d.Assessment is CareAssessment.Assessed && d.Kind != CareCapacityKind.Defer).ToArray();
        var horizon = assessed.Length == 0 ? 0 : assessed.Max(d => Math.Max(d.Stays[^1].From, ((CareAssessment.Assessed)d.Assessment).Effect.TendTicks)) + RestHorizonTicks;
        var options = new List<CareCapacityOption>();
        foreach (var d in drafts)
        {
            if (d.Assessment is not CareAssessment.Assessed { Effect: var effect })
            {
                options.Add(new(d.Kind, d.Bed, d.Assessment));
                continue;
            }
            if (d.Stays.Select(s => s.At).OfType<CareTemperature.Unread>().FirstOrDefault() is { } unread)
            {
                options.Add(new(d.Kind, d.Bed, d.Kind == CareCapacityKind.Defer ? new CareAssessment.Assessed(effect with { Exposure = double.PositiveInfinity })
                    : new CareAssessment.Unassessed(TemperatureFact + unread.Fact)));
                continue;
            }
            var exposure = 0d;
            for (var i = 0; i < d.Stays.Count; i++)
            {
                var from = Math.Min(d.Stays[i].From, horizon);
                var to = i + 1 < d.Stays.Count ? Math.Min(d.Stays[i + 1].From, horizon) : horizon;
                var celsius = ((CareTemperature.Read)d.Stays[i].At).Celsius;
                exposure += Math.Max(0, to - from) * (ForecastValue.ThermalGainPerTick(true, celsius, patient.ComfortMin, patient.ComfortMax)
                    + ForecastValue.ThermalGainPerTick(false, celsius, patient.ComfortMin, patient.ComfortMax));
            }
            options.Add(new(d.Kind, d.Bed, new CareAssessment.Assessed(effect with { Exposure = exposure })));
        }
        return options;
    }

    /// <summary>
    /// The best tend given at a site: its infection weight, and when it ends after the patient's move and the doctor's walk. A
    /// recovering patient has no tend: its effect is the move alone. One whose tend ends once the patient has bled out is excluded.
    /// </summary>
    private static CareAssessment Effect(IEnumerable<SupportWorker> tends, double movement, double room, int orders, double heal, SupportCarePatient patient)
    {
        if (!patient.Tendable) return new CareAssessment.Assessed(new(0, movement, 0, room, orders, 0, heal));
        CareEffect? best = null;
        foreach (var w in tends)
        {
            var tend = (SupportTreatment.Tend)w.Treatment;
            var at = Math.Max(movement, w.ApproachTicks) + w.WorkTicks;
            var weight = tend.Infection is InfectionCheck.Pending(var check)
                ? at < check ? HarmConsequences.TendInfectionFactor(tend.Quality, room) : 1
                : 0;
            var effect = new CareEffect(weight, at, tend.Quality, room, orders, 0, heal);
            if (best is null || weight < best.InfectionWeight - Tolerance || (Math.Abs(weight - best.InfectionWeight) <= Tolerance && at < best.TendTicks)) best = effect;
        }
        if (best is null) return new CareAssessment.Excluded(NoTend);
        // The earliest tend that ends before the patient bleeds out, when the best does not.
        if (patient.Bleeding is CareBleeding.Bleeding bleeding && best.TendTicks >= bleeding.TicksToDeath)
        {
            var first = tends.Select(w => Math.Max(movement, w.ApproachTicks) + w.WorkTicks).Min();
            return first >= bleeding.TicksToDeath ? new CareAssessment.Excluded(BleedsOut) : Effect(tends.Where(w => Math.Max(movement, w.ApproachTicks) + w.WorkTicks < bleeding.TicksToDeath), movement, room, orders, heal, patient);
        }
        return new CareAssessment.Assessed(best);
    }

    private static SleepingBed? FloorBed(CareCapacityRequest r, SleepingObservation sleeping, Dictionary<string, HashSet<string>> taken)
    {
        var hosted = new HashSet<string>(r.Rooms.Rooms.SelectMany(room => room.BedIds()), StringComparer.Ordinal);
        var surgical = r.Surgical.Count > 0;
        return sleeping.Beds
            .Where(b => !b.Medical && !b.Crib && b.Humanlike && !b.Prisoners && !b.Slaves && hosted.Contains(b.Id) && b.Owners.Count == 0 && b.Users.Count == 0
                && !taken.ContainsKey(b.Id) && !(surgical && b.Definition.Value == SleepingRules.SleepingSpotDefinition.ToString()) && !Refused(r, b.Id, true)
                && Short(Converted(sleeping, b.Id, true)) <= Short(sleeping))
            .OrderBy(b => r.PlannedMedical.Contains(b.Cell) ? 0 : 1).ThenBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    private static string? Refusal(CareCapacityRequest r, string bed, bool medical) =>
        r.Refused.FirstOrDefault(o => o.Bed == bed && o.Medical == medical)?.Reason;

    private static bool Refused(CareCapacityRequest r, string bed, bool medical) => Refusal(r, bed, medical) is not null;

    /// <summary>
    /// Whether converting <paramref name="bed"/> for <paramref name="patient"/> costs another sleeper its slot: the converted
    /// census leaves more sleepers short than now; or an owner it evicts besides the patient (a partner sharing the bed, the
    /// owner of another bed) is left short, the shortfall falling were it not counted; or such an owner is one the sleeping
    /// census does not count (a prisoner). The patient itself is held by the converted bed while it seeks medical rest
    /// (<see cref="SleepingRules.HeldInMedicalBeds"/>), so its own bed costs it no second slot.
    /// </summary>
    private static bool Displaces(SleepingObservation v, string bed, string patient)
    {
        var converted = Converted(v, bed, true);
        var displaced = v.Beds.Single(b => b.Id == bed).Owners.Where(o => o.Value != patient).ToHashSet();
        return Short(converted) > Short(v) || Uncounted(v, bed, patient)
            || (displaced.Count > 0 && Short(converted) > Short(Without(converted, displaced)));
    }

    private static SleepingObservation Without(SleepingObservation v, HashSet<PawnId> who) => v with
    {
        People = EquatableArray.From(v.People.Where(p => !who.Contains(p.Id))),
        Guests = EquatableArray.From(v.Guests.Where(p => !who.Contains(p.Id))),
        Slaves = EquatableArray.From(v.Slaves.Where(p => !who.Contains(p.Id))),
    };

    /// <summary>Whether the bed has an owner besides the patient whom the sleeping census does not count (a prisoner): converting evicts it with no slot counted for it.</summary>
    private static bool Uncounted(SleepingObservation v, string bed, string patient)
    {
        var counted = v.People.Concat(v.Guests).Concat(v.Slaves).Select(p => p.Id).ToHashSet();
        return v.Beds.Single(b => b.Id == bed).Owners.Any(o => o.Value != patient && !counted.Contains(o));
    }

    private static int Short(SleepingObservation v)
    {
        var (regular, slave) = SleepingRules.SlotShortfall(v);
        return regular + slave;
    }

    /// <summary>
    /// The census with each held bed's slot counted as its holder's (its transport reserves the bed, then the patient lies in
    /// it): the holder is one of the bed's users, so the bed is no free slot for a sleeper or another patient seeking medical
    /// rest (<see cref="SleepingRules.HeldInMedicalBeds"/>) and no post-care restore or floor conversion sees it empty.
    /// </summary>
    public static SleepingObservation Holding(SleepingObservation v, IReadOnlyList<CareBedHold> holds) => holds.Count == 0 ? v : v with
    {
        Beds = EquatableArray.From(v.Beds.Select(b => holds.Where(h => h.Bed == b.Id && !b.Users.Contains(new PawnId(h.Patient))).ToArray() is { Length: > 0 } held
            ? b with { Native = b.Native with { Users = EquatableArray.From(b.Users.Concat(held.Select(h => new PawnId(h.Patient)))) } } : b)),
    };

    /// <summary>The census with <paramref name="bed"/> set medical or not; any change of the flag evicts its owners (Building_Bed.Medical's setter).</summary>
    internal static SleepingObservation Converted(SleepingObservation v, string bed, bool medical) => v with
    {
        Beds = EquatableArray.From(v.Beds.Select(b => b.Id != bed || b.Medical == medical ? b
            : b with { Native = b.Native with { Medical = medical, Owners = [] } })),
    };
}
