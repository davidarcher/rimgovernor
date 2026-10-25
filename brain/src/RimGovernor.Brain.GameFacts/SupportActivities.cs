using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;
/// <summary>
/// One native census of every free colonist against every bench bill, patient and surgery (#3341): the demands, the treatments
/// each colonist can give now (a worker row only where it has a path and the speed to do the work) and the bench deliveries.
/// <see cref="Findings"/> name the census's standing limits and the patients an external doctor cannot reach.
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
}
/// <summary>
/// One support demand. <see cref="FoodPoisonChance"/> is the RoomStatDefOf.FoodPoisonChance of the room at
/// <see cref="Location"/> (the stat's roomlessScore without a proper room), the chance CompFoodPoisonable.Notify_RecipeProduced
/// first rolls for a meal made there; <see cref="InfectionChanceFactor"/> and <see cref="SurgerySuccessFactor"/> are that room's
/// InfectionChanceFactor and the room stat StatDefOf.SurgerySuccessChanceFactor multiplies, each its roomlessScore without a room.
/// <paramref name="BleedRate"/> is a patient's BleedRateTotal; a bench demand's target is a building, which does not bleed (0).
/// </summary>
public sealed record SupportDemand(string Id, string Kind, string Target, Cell Location,
    double FoodPoisonChance, double BleedRate)
{
    public required double InfectionChanceFactor { get; init; }
    public required double SurgerySuccessFactor { get; init; }
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
/// work ticks at its speed, what it consumes and the treatment's own mechanics.
/// </summary>
public sealed record SupportWorker(string Demand, string Pawn, string Target, string Job, string WorkGiver,
    Cell Location, double ApproachTicks, double WorkTicks)
{
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
