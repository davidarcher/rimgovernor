using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;
/// <summary>
/// One native census of every free colonist against every bench bill, patient and surgery (#3341): the demands, the treatments
/// each colonist can give now (a worker row only where it has a path and the speed to do the work) and the bench deliveries.
/// <see cref="Findings"/> name the census's standing limits; <see cref="Patients"/> are the care patients and the beds each could be
/// cared in (task 4.1 of <c>redefine-habitat-requirements</c>).
/// </summary>
public sealed record SupportActivityRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportDemand>))]
    public required EquatableArray<SupportDemand> Demands { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportWorker>))]
    public required EquatableArray<SupportWorker> Workers { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportDelivery>))]
    public required EquatableArray<SupportDelivery> Deliveries { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Findings { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportCarePatient>))]
    public required EquatableArray<SupportCarePatient> Patients { get; init; }
}
/// <summary>
/// Who a bed takes a pawn as (RestUtility.CanUseBedNow's guest status and CanUseBedEver's humanlike check): a free colonist, a
/// slave of the colony (GuestStatus.Slave), a prisoner of the colony (GuestStatus.Prisoner), a hosted guest (GuestStatus.Guest)
/// or an animal (not RaceProps.Humanlike).
/// </summary>
public enum BedOccupant { Colonist, Slave, Prisoner, Guest, Animal }
/// <summary>
/// Whether a patient lets a doctor treat it (WorkGiver_Tend.HasJobOnThing and the patient's own job): Calm; Fleeing (its job is
/// JobDefOf.Flee, FleeAndCower or FleeAndCowerShort); Aggressive (InAggroMentalState without Scaria, which HasJobOnThing refuses);
/// PrisonBreaking (PrisonBreakUtility.IsPrisonBreaking); Hostile (HostileTo the player's faction).
/// </summary>
public enum CareConduct { Calm, Fleeing, Aggressive, PrisonBreaking, Hostile }
/// <summary>
/// One care demand's patient (task 4.1): its demand, pawn, <see cref="BedOccupant"/>, posture now, conduct and the beds it could be
/// cared in (a no-medicine tend is always allowed short of medCare NoCare, which HealthAIUtility.ShouldBeTendedNowByPlayer already
/// excludes from the demands, and each medicine a worker carries passed medCare.AllowsMedicine): its current bed when it lies
/// in one, else the nearest beds it fits (RestUtility.CanUseBedEver; at most four medical and two other beds,
/// <c>care_beds_truncated:&lt;demand&gt;</c> naming a cut). A worker of <see cref="SupportWorker.CareBed"/> treats it in one of them.
/// Task 4.4: a patient that is not <see cref="Tendable"/> is a recovering one (HealthAIUtility.ShouldSeekMedicalRest, not downed)
/// lying in no bed or in a sleeping spot: it has no demand row and no tend, and its beds are its current one and the nearest it fits.
/// </summary>
public sealed record SupportCarePatient(string Demand, string Patient, BedOccupant Occupant, CarePosture Posture, CareConduct Conduct)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportCareBed>))]
    public required EquatableArray<SupportCareBed> Beds { get; init; }

    /// <summary>HealthAIUtility.ShouldBeTendedNowByPlayer: false for a recovering patient listed only for its transfer.</summary>
    public required bool Tendable { get; init; }

    /// <summary>Its blood loss now (HediffSet.BleedRateTotal and HealthUtility.TicksUntilDeathDueToBloodLoss).</summary>
    public required CareBleeding Bleeding { get; init; }

    /// <summary>The temperature at the cell it lies or stands on (GenTemperature.TryGetTemperatureForCell).</summary>
    public required CareTemperature Temperature { get; init; }

    /// <summary>Its StatDefOf.ComfyTemperatureMin and ComfyTemperatureMax, the range HediffGiver_Hypothermia and HediffGiver_Heat widen by 10.</summary>
    public required double ComfortMin { get; init; }
    public required double ComfortMax { get; init; }

    /// <summary>Where a medical sleeping spot could be placed for it (task 4.4), or why none is read.</summary>
    public required CareFieldSpot FieldSpot { get; init; }
}
/// <summary>A patient's blood loss: Stable below the native's 0.0001 bleed floor, else its BleedRateTotal (severity a day) and the ticks until it dies of it.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Stable), "stable")]
[JsonDerivedType(typeof(Bleeding), "bleeding")]
public abstract record CareBleeding
{
    private protected CareBleeding() { }

    public sealed record Stable : CareBleeding;

    public sealed record Bleeding(double Rate, double TicksToDeath) : CareBleeding;
}
/// <summary>A cell's temperature (Celsius), or the fact that left it unread.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Read), "read")]
[JsonDerivedType(typeof(Unread), "unread")]
public abstract record CareTemperature
{
    private protected CareTemperature() { }

    public sealed record Read(double Celsius) : CareTemperature;

    public sealed record Unread(string Fact) : CareTemperature;
}
/// <summary>
/// A medical sleeping spot's site near a patient lying out of bed (task 4.4): the nearest cell where ThingDefOf.SleepingSpot can be
/// placed facing south (GenConstruct.CanPlaceBlueprintAt) that the patient reaches. <see cref="Site.Id"/> is the
/// <see cref="SupportWorker.CareBed"/> of the tends given there; the spot's own stats are those of the def (no
/// MedicalTendQualityOffset in vanilla, its bed_healPerDay), its room's InfectionChanceFactor and the cell's temperature. None names
/// why no site is read: an animal lies where it is tended, a prisoner needs a prison bed, a patient in a bed has one, or no cell
/// nearby takes the spot.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Site), "site")]
[JsonDerivedType(typeof(None), "none")]
public abstract record CareFieldSpot
{
    private protected CareFieldSpot() { }

    public sealed record Site(string Id, Cell Cell, CareMovement Movement, double InfectionChanceFactor, CareTemperature Temperature, double HealPerDay) : CareFieldSpot;

    public sealed record None(string Reason) : CareFieldSpot;
}
/// <summary>A patient's posture now: lying in a bed (CurrentBed), lying elsewhere (downed or laid down) or standing.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(InBed), "inBed")]
[JsonDerivedType(typeof(Lying), "lying")]
[JsonDerivedType(typeof(Standing), "standing")]
public abstract record CarePosture
{
    private protected CarePosture() { }

    public sealed record InBed(string Bed) : CarePosture;

    public sealed record Lying : CarePosture;

    public sealed record Standing : CarePosture;
}
/// <summary>
/// One bed a patient could be cared in, as it stands now: its class (Building_Bed.Medical, ForPrisoners, ForSlaves, the def's
/// bed_humanlike; a crib is listed only for a patient small enough to fit it), its SleepingSlotsCount and CurOccupants (a patient
/// holds a slot it lies in), RestUtility.CanUseBedNow for the patient
/// (the native's own full check, a prison bed's prison cell included), how the patient gets there and the care it gives: its
/// room's RoomStatDefOf.InfectionChanceFactor (HediffComp_Infecter.CompTended stores it); the bed's own
/// StatDefOf.MedicalTendQualityOffset (its def, linked facilities' CompAffectedByFacilities offsets and room parts) is in each
/// <see cref="SupportWorker"/> tend quality given there.
/// </summary>
public sealed record SupportCareBed(string Id, Cell Location, int Slots)
{
    public required bool Medical { get; init; }
    public required bool Prisoners { get; init; }
    public required bool Slaves { get; init; }
    public required bool Humanlike { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Occupants { get; init; }
    public required bool UsableNow { get; init; }
    public required CareMovement Movement { get; init; }
    public required double InfectionChanceFactor { get; init; }

    /// <summary>The temperature at the bed's cell (task 4.4).</summary>
    public required CareTemperature Temperature { get; init; }

    /// <summary>The def's building.bed_healPerDay, added to a lying patient's natural healing (Pawn_HealthTracker: 8, 4 more lying, then this).</summary>
    public required double HealPerDay { get; init; }

    /// <summary>
    /// The bed's StatDefOf.SurgerySuccessChanceFactor (task 4.5), the factor SurgeryOutcomeComp_BedAndRoomQuality multiplies an
    /// operation's quality by while the patient lies in it: the def's statBases, linked facilities' CompAffectedByFacilities offsets (a
    /// vitals monitor's), the room's SurgerySuccessChanceCleanlinessFactor, glow, outdoors and quality parts.
    /// </summary>
    public required CareSurgeryFactor SurgerySuccessFactor { get; init; }
}
/// <summary>A bed's SurgerySuccessChanceFactor as read, or the fact that left it unread.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Read), "read")]
[JsonDerivedType(typeof(Unread), "unread")]
public abstract record CareSurgeryFactor
{
    private protected CareSurgeryFactor() { }

    public sealed record Read(double Factor) : CareSurgeryFactor;

    public sealed record Unread(string Fact) : CareSurgeryFactor;
}
/// <summary>
/// How the patient reaches a bed: already lying in it; walking there itself (its own path ticks); carried by the rescuer whose
/// walk to the patient and carry to the bed is shortest (a free colonist able to do Doctor work, with Manipulation and Moving); or
/// no walk and no rescuer reaches it.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AlreadyHere), "alreadyHere")]
[JsonDerivedType(typeof(Walk), "walk")]
[JsonDerivedType(typeof(Carried), "carried")]
[JsonDerivedType(typeof(Unreachable), "unreachable")]
public abstract record CareMovement
{
    private protected CareMovement() { }

    public sealed record AlreadyHere : CareMovement;

    public sealed record Walk(double Ticks) : CareMovement;

    public sealed record Carried(string Rescuer, double Ticks) : CareMovement;

    public sealed record Unreachable : CareMovement;
}
/// <summary>
/// One support demand. <see cref="FoodPoisonChance"/> is the RoomStatDefOf.FoodPoisonChance of the room at
/// <see cref="Location"/> (the stat's roomlessScore without a proper room), the chance CompFoodPoisonable.Notify_RecipeProduced
/// first rolls for a meal made there; <see cref="InfectionChanceFactor"/> is that room's InfectionChanceFactor (its roomlessScore
/// without a room). A surgery's <see cref="SurgeryBed"/> is the bed the patient is operated in (RestUtility.FindPatientBedFor: its
/// current bed while a medical bed it may use, else the closest such medical bed it reaches, else the bed it would sleep in; ""
/// with none) and <see cref="SurgerySuccessFactor"/> that bed's StatDefOf.SurgerySuccessChanceFactor, the factor
/// SurgeryOutcomeComp_BedAndRoomQuality multiplies (the bed's def, linked facilities and room); 0 without a bed or on a demand
/// that is not a surgery.
/// <paramref name="BleedRate"/> is a patient's BleedRateTotal; a bench demand's target is a building, which does not bleed (0).
/// </summary>
public sealed record SupportDemand(string Id, string Kind, string Target, Cell Location,
    double FoodPoisonChance, double BleedRate)
{
    public required double InfectionChanceFactor { get; init; }
    public required double SurgerySuccessFactor { get; init; }
    public required string SurgeryBed { get; init; }
    /// <summary>
    /// Whether a spawned colonist's current job serves this demand now (task 5.5 of <c>native-led-labor-execution</c>): a
    /// TendPatient job whose target is the patient (a self-tend included), a DoBill job of the demand's bill (a surgery or an
    /// ingredient delivery), or a DoBill job at the demand's bench (a bench demand). An observation of
    /// the current job only, never a completed treatment; the review's care windows (CareStalls) read it.
    /// </summary>
    public required bool Served { get; init; }
    /// <summary>
    /// A surgery bill's outcome model (#3804): the recipe's surgeryOutcomeEffect outcomes in the order GetOutcome tries them
    /// (empty without an effect: the surgery cannot fail), whether SurgeryOutcomeComp_BedAndRoomQuality applies (an effect comp of
    /// that class and not surgeryIgnoreEnvironment) and the SurgeryOutcomeComp_ClampToRange range the quality ends in (0 to
    /// float.MaxValue without one). Empty, false, 0 and float.MaxValue on a demand that is not a surgery.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<SurgeryOutcomeRead>))]
    public required EquatableArray<SurgeryOutcomeRead> SurgeryOutcomes { get; init; }
    public required bool SurgeryEnvironment { get; init; }
    public required double SurgeryQualityMin { get; init; }
    public required double SurgeryQualityMax { get; init; }
}
/// <summary>
/// One surgery outcome (#3804) by its class: "success" (SurgeryOutcomeSuccess, rolled at the quality), "death"
/// (SurgeryOutcome_Death at recipe.deathOnFailedSurgeryChance, 0 for a Deathless patient), "failure" (SurgeryOutcome_Failure,
/// and SurgeryOutcome_FailureWithHediff at 0 when it cannot apply to the recipe), else the class name. Damage is its totalDamage;
/// WoundSeverity the mean injury HealthUtility.GiveRandomSurgeryInjuries deals at a time over the parts it may hit, by coverage:
/// max(3, 0.75 x part health), the mean of max(3, health x U(0.5, 1)) where health x 0.5 is at least 3.
/// </summary>
public sealed record SurgeryOutcomeRead(string Kind, double Chance, double Damage, double WoundSeverity);
/// <summary>
/// One colonist's treatment of one demand: the walk to the patient (by way of the medicine or ingredient when it uses one), the
/// work ticks at its speed, what it consumes and the treatment's own mechanics. A worker with a <see cref="CareBed"/> tends the
/// patient once it lies in that bed (<see cref="SupportCarePatient.Beds"/>): its walk ends at the bed and its tend quality is the
/// one that bed gives.
/// </summary>
public sealed record SupportWorker(string Demand, string Pawn, string Target, string Job, string WorkGiver,
    Cell Location, double ApproachTicks, double WorkTicks)
{
    /// <summary>The native stationary service cell; target and route anchors are not service positions.</summary>
    public ServicePositionRead ServicePosition { get; init; } = new ServicePositionRead.Unprojected("synthetic_or_unsupported_service");
    /// <summary>The bed the tend is given in after the patient is moved there; "" for a treatment where the patient lies now.</summary>
    public string CareBed { get; init; } = "";
    /// <summary>The medicine a tend uses or the ingredient a surgery consumes; null for a tend without medicine.</summary>
    public required SupportMedicine? Medicine { get; init; }
    public required SupportTreatment Treatment { get; init; }
}

/// <summary>The stack a treatment draws from: its id, def, the count it uses, where it lies and how many it holds.</summary>
public sealed record SupportMedicine(string Id, string Resource, int Count, Cell Location, int Available);

/// <summary>What a treatment does: a tend (TendPatient) or a surgery (DoBill).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Tend), "tend")]
[JsonDerivedType(typeof(Surgery), "surgery")]
public abstract record SupportTreatment
{
    private protected SupportTreatment() { }

    /// <summary>A tend: its base quality (TendUtility.CalculateBaseTendQuality), the selected batch's pending infection check and
    /// the native learning the completed treatment pays (#3632).</summary>
    public sealed record Tend(double Quality, InfectionCheck Infection, TendLearning Learning) : SupportTreatment;

    /// <summary>
    /// A surgery (#3804): its recipe, SurgeryOutcomeEffectDef.GetQuality's product for this surgeon without the bed and room comp
    /// and the clamp (each other comp as it applies now; 1 without an effect), and the recipe's native work and learning at the
    /// doctor's operation speed (#3632; null when the recipe trains no skill).
    /// </summary>
    public sealed record Surgery(PawnActivityRecipe Recipe, double Quality, ProductiveSkillWork? SkillWork) : SupportTreatment;
}

/// <summary>A tend batch's infection check: the earliest pending native HediffComp_Infecter check among its wounds, relative to
/// this observation (Pending), or None when no wound in the batch has one.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(None), "none")]
[JsonDerivedType(typeof(Pending), "pending")]
public abstract record InfectionCheck
{
    private protected InfectionCheck() { }

    public sealed record None : InfectionCheck;

    public sealed record Pending(double Ticks) : InfectionCheck;
}

/// <summary>One colonist's delivery of a bench bill's ingredient: its walk to the source and the carry to the staging cell.</summary>
public sealed record SupportDelivery(string Demand, string Pawn, string Target, string Resource, string Container, string Bill,
    Cell Source, Cell Destination, int Required, int Available, int Capacity, double ApproachTicks, double DeliveryTicks);
