using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Bounded, read-only mechanics and target census. Null read is unknown, never no needs.</summary>
public sealed record NeedsForecastRead
{
    public long Tick { get; init; }
    public bool Truncated { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedsForecastPawn>))]
    public EquatableArray<NeedsForecastPawn> Pawns { get; init; }
}
public sealed record NeedRate(string Kind,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> Level,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> FallPerTick,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> Threshold,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> Capacity)
{
    /// <summary>Current-rate approximation; observed threshold crossings require fresh native rates.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Unsupported { get; init; }
}
public sealed record NeedMemory(string Id, double Offset,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> RemainingTicks, bool Fades)
{ public bool Permanent { get; init; } }
/// <summary>Native mood-point contribution in a half-open local-hour window; EndHour less than StartHour wraps midnight. CurrentOffset is already present in MoodUnclampedTarget.</summary>
public sealed record NeedConditionalThought(string Id, double StartHour, double EndHour, bool AwakeOnly,
    double Offset, double CurrentOffset, string Provenance);
public sealed record NeedsForecastPawn
{
    public string Pawn { get; init; } = "";
    public string State { get; init; } = "unknown";
    [JsonConverter(typeof(ObservedConditionalThoughtsJsonConverter))]
    public Fact<EquatableArray<NeedConditionalThought>> ConditionalThoughts { get; init; }
    public Fact<double> LocalHour { get; init; }
    public Fact<bool> Awake { get; init; }
    public Fact<double> SleepThreshold { get; init; }
    public Fact<double> WakeThreshold { get; init; }
    /// <summary>Earliest possible sleep relative to observation tick; unknown for blockers with no known recovery bound.</summary>
    public Fact<double> SleepBlockedUntilTicks { get; init; }
    public Fact<bool> SleepWhileStarving { get; init; }
    /// <summary>Unclamped native target in normalized mood units, including CurrentOffset contributions once.</summary>
    public Fact<double> MoodUnclampedTarget { get; init; }
    public Fact<double> MoodRisePerTick { get; init; }
    public Fact<double> MoodFallPerTick { get; init; }
    public Fact<double> MoodIntervalTicks { get; init; }
    public Fact<bool> MoodFreezesWhileAsleep { get; init; }
    public Fact<double> ComfortableMin { get; init; }
    public Fact<double> ComfortableMax { get; init; }
    [JsonConverter(typeof(ObservedNumberJsonConverter))]
    public Fact<double> Mood { get; init; }
    [JsonConverter(typeof(ObservedNumberJsonConverter))]
    public Fact<double> MoodTarget { get; init; }
    [JsonConverter(typeof(ObservedNumberJsonConverter))]
    public Fact<double> BreakThreshold { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedRate>))]
    public EquatableArray<NeedRate> Needs { get; init; }
    [JsonConverter(typeof(ObservedMemoriesJsonConverter))]
    public Fact<EquatableArray<NeedMemory>> Memories { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedTarget>))]
    public EquatableArray<NeedTarget> Targets { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Unsupported { get; init; }
}
/// <summary>Native legality and mechanics, not a selected need action. Travel is an observed route cost.</summary>
public sealed record NeedTarget(string Id, string Kind, string Resource, int AvailableCount,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> TravelTicks,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> ServiceTicks,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> GainPerUnit,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> GainPerTick)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedMemory>))]
    public EquatableArray<NeedMemory> MealThoughts { get; init; }
    public Cell? Location { get; init; }
    public Fact<double> StockCount { get; init; }
    public Fact<double> FreshTicks { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Unsupported { get; init; }
}
