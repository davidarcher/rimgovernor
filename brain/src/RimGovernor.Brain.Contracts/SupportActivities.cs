using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
namespace RimGovernor.Contracts.Wire.Observations;
public sealed record SupportActivityRead
{
    public bool Known { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportDemand>))]
    public EquatableArray<SupportDemand> Demands { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportWorker>))]
    public EquatableArray<SupportWorker> Workers { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportDelivery>))]
    public EquatableArray<SupportDelivery> Deliveries { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Findings { get; init; }
}
/// <summary>
/// One support demand. <see cref="FoodPoisonChance"/> is the RoomStatDefOf.FoodPoisonChance of the room at
/// <see cref="Location"/> (the stat's roomlessScore without a proper room), the chance CompFoodPoisonable.Notify_RecipeProduced
/// first rolls for a meal made there; 0 on a delivery row.
/// </summary>
public sealed record SupportDemand(string Id, string Kind, string Target, Cell Location,
    double FoodPoisonChance, Fact<double> BleedRate)
{
    public Fact<double> InfectionChanceFactor { get; init; }
    public Fact<double> SurgerySuccessFactor { get; init; }
    /// <summary>
    /// A surgery bill's outcome model (#3804): the recipe's surgeryOutcomeEffect outcomes in the order GetOutcome tries them
    /// (empty without an effect: the surgery cannot fail), whether SurgeryOutcomeComp_BedAndRoomQuality applies (an effect comp of
    /// that class and not surgeryIgnoreEnvironment) and the SurgeryOutcomeComp_ClampToRange range the quality ends in (0 to
    /// float.MaxValue without one).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<SurgeryOutcomeRead>))]
    public EquatableArray<SurgeryOutcomeRead> SurgeryOutcomes { get; init; }
    public bool SurgeryEnvironment { get; init; }
    public double SurgeryQualityMin { get; init; }
    public double SurgeryQualityMax { get; init; }
}
/// <summary>
/// One surgery outcome (#3804) by its class: "success" (SurgeryOutcomeSuccess, rolled at the quality), "death"
/// (SurgeryOutcome_Death at recipe.deathOnFailedSurgeryChance, 0 for a Deathless patient), "failure" (SurgeryOutcome_Failure,
/// and SurgeryOutcome_FailureWithHediff at 0 when it cannot apply to the recipe), else the class name. Damage is its totalDamage;
/// WoundSeverity the mean injury HealthUtility.GiveRandomSurgeryInjuries deals at a time over the parts it may hit, by coverage:
/// max(3, 0.75 x part health), the mean of max(3, health x U(0.5, 1)) where health x 0.5 is at least 3.
/// </summary>
public sealed record SurgeryOutcomeRead(string Kind, double Chance, double Damage, double WoundSeverity);
public sealed record SupportWorker(string Demand, string Pawn, string Target, string Job, string WorkGiver,
    Cell Location, Fact<double> ApproachTicks, Fact<double> WorkTicks)
{
    public string Medicine { get; init; } = "";
    public string MedicineResource { get; init; } = "";
    public int MedicineCount { get; init; }
    public Cell? MedicineLocation { get; init; }
    public int MedicineAvailable { get; init; }
    public Fact<double> TendQuality { get; init; }
    public Fact<bool> InfectionRoomEffectApplies { get; init; }
    /// <summary>Earliest pending native infection check in the selected treatment batch, relative to this observation.</summary>
    public Fact<double> InfectionCheckTicks { get; init; }
    public PawnActivityRecipe? Recipe { get; init; }
    /// <summary>
    /// Surgery (#3804): SurgeryOutcomeEffectDef.GetQuality's product for this surgeon without the bed and room comp and the clamp
    /// (SurgeryOutcomeComp_BedAndRoomQuality, SurgeryOutcomeComp_ClampToRange), each other comp as it applies now; 1 without an effect.
    /// </summary>
    public double SurgeryQuality { get; init; }
    /// <summary>Surgery: the recipe's native work, learning and the doctor's operation speed at the current level (#3632).</summary>
    public ProductiveSkillWork? SkillWork { get; init; }
    /// <summary>Tend: the native learning the completed treatment pays (#3632).</summary>
    public TendLearning? TendLearning { get; init; }
}
public sealed record SupportDelivery(string Demand, string Pawn, string Target, string Resource, string Container, string Bill,
    Cell Source, Cell Destination, int Required, int Available, int Capacity, Fact<double> ApproachTicks, Fact<double> DeliveryTicks);





