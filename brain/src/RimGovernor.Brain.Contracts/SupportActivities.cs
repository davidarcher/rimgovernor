using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
namespace RimGovernor.Contracts.Wire.Observations;
public sealed record SupportActivityRead
{
    public bool Known { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportDemand>))]
    public EquatableArray<SupportDemand> Demands { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportFilth>))]
    public EquatableArray<SupportFilth> Filth { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportWorker>))]
    public EquatableArray<SupportWorker> Workers { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SupportDelivery>))]
    public EquatableArray<SupportDelivery> Deliveries { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Findings { get; init; }
}
public sealed record SupportDemand(string Id, string Kind, string Target, Cell Location,
    Fact<double> FoodPoisonChance, Fact<double> BleedRate)
{
    public Fact<double> FoodPoisonChanceClean { get; init; }
    public Fact<double> InfectionChanceFactor { get; init; }
    public Fact<double> InfectionChanceFactorClean { get; init; }
    public Fact<double> SurgerySuccessFactor { get; init; }
    public Fact<double> SurgerySuccessFactorClean { get; init; }
}
public sealed record SupportFilth(string Demand, string Target, Cell Location);
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
}
public sealed record SupportDelivery(string Demand, string Pawn, string Target, string Resource, string Container, string Bill,
    Cell Source, Cell Destination, int Required, int Available, int Capacity, Fact<double> ApproachTicks, Fact<double> DeliveryTicks);





