using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>
/// The wire shapes of the read routes (Go <c>httpapi</c> DTOs). A record property is the camelCase JSON name; an
/// unknown fact is a null; a 64-bit counter the launcher must not round is a string.
/// </summary>
internal static class PlayerJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";
}

// state

internal sealed record StateDto(
    string SessionId,
    bool Connected,
    string Mode,
    StatusDto Status,
    GameDto Game,
    GenerationDto? Generation,
    IdentityDto? Identity,
    string? ActivePlanId);

internal sealed record StatusDto(string Label);

internal sealed record GameDto(long? Tick, bool? Paused, string? ObservedAt, bool Stale);

internal sealed record GenerationDto(
    string Colony,
    int Map,
    string Load,
    string Plan,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] ulong Revision,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] ulong Native);

internal sealed record IdentityDto(string ColonyId, int MapId, string LoadToken);

// plan

internal sealed record PlanDto(
    string Id,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] ulong Revision,
    IReadOnlyList<ActionDto> Actions);

internal sealed record ActionDto(
    string Id,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BuildingDto? Building,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DraftDto? Draft,
    ProgressDto Progress);

internal sealed record DraftDto(string PawnId);

internal sealed record BuildingDto(string DefName, int X, int Z, string Rotation, string Stuff);

internal sealed record ProgressDto(
    string Stage,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] ulong Attempt,
    long Tick,
    bool Unresolved,
    string? Receipt,
    string? Effect,
    string? UnsuccessfulReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? HeldReasons);

// routines

internal sealed record RoundsStatusDto(
    IReadOnlyList<string> Emergency,
    ExtentEligibilityDto ExtentEligibility,
    RoundsExtentDto Extent,
    IReadOnlyList<ResourceRunwayDto> ResourceRunways,
    bool ReviewsEnabled,
    bool MethodsEnabled,
    IReadOnlyList<string> ActiveFamilies,
    long? LastReviewTick,
    IReadOnlyList<ConcernProgressDto> Progress,
    ColonyStageDto? Stage,
    RoundsRosterDto? Roster,
    IReadOnlyList<RoundsSectionDto> Sections,
    IReadOnlyList<NoOpDto> NoOps,
    IReadOnlyList<LootHoldDto> LootHolds);

internal sealed record ExtentEligibilityDto(bool Known, string Reason, IReadOnlyList<ExtentRegionEligibilityDto> Regions);

internal sealed record ExtentRegionEligibilityDto(
    int Region,
    string Stage,
    int Cells,
    IReadOnlyList<string> Origins,
    IReadOnlyList<string> Facilities,
    IReadOnlyList<string> ActiveFacilities,
    bool Eligible,
    IReadOnlyList<string> HoldReasons);

internal sealed record RoundsExtentDto(bool Known, int Regions, int Cells);

internal sealed record ResourceRunwayDto(
    string Resource,
    long Tick,
    double WindowDays,
    double ThresholdDays,
    long Reserve,
    long? Stock,
    long? SurfaceOre,
    double? ConsumptionPerDay,
    double? StockDays,
    double? DaysLeft,
    bool? Deficit,
    long Target);

internal sealed record NoOpDto(string Concern, string Reason);

internal sealed record LootHoldDto(string Thing, string Definition, int X, int Z, string Reason);

internal sealed record ColonyStageDto(string Stage, long Since, string Blocker, string Reason, bool Held);

internal sealed record ConcernProgressDto(
    string Concern,
    string Method,
    string Expected,
    long LastProgress,
    long NextReview,
    string Blocked,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subject,
    IReadOnlyList<ProgressCooldownDto> Cooldowns);

internal sealed record ProgressCooldownDto(string Key, long Until);

internal sealed record RoundsSectionDto(string Section, string Family, long AsOf, bool Complete, string Source, string StoredAt);

internal sealed record RoundsRosterDto(
    long Tick,
    IReadOnlyList<RoundsCoverageDto> Coverage,
    IReadOnlyList<RoundsDecayingDto> Decaying,
    IReadOnlyList<RoundsRosterPawnDto> Pawns);

internal sealed record RoundsCoverageDto(string Work, int Demand, int Owners, int Capable);

internal sealed record RoundsDecayingDto(string Pawn, string Skill, int Level);

internal sealed record RoundsRosterPawnDto(
    string Pawn,
    double Age,
    bool Child,
    bool Ranged,
    IReadOnlyList<RoundsTraitDto> Traits,
    RoundsTraitEffectsDto Effects,
    IReadOnlyList<RoundsProfileSkillDto> Skills,
    IReadOnlyList<string> Incapable,
    IReadOnlyList<string> Forbidden);

internal sealed record RoundsTraitDto(string Name, int Degree);

internal sealed record RoundsTraitEffectsDto(
    double WorkSpeed,
    double LearnRate,
    double MoveSpeed,
    int Sociable,
    int ChemicalInterest,
    IReadOnlyList<string> Flags);

internal sealed record RoundsProfileSkillDto(string Name, int Level, int Stored, string Passion, bool Disabled, double LearnFactor);

// colony

internal sealed record ColonyStatusDto(
    FoodPlanDto? FoodPlan,
    long? FoodPlanTick,
    long Tick,
    long RosterTick,
    long? Colonists,
    long? Workers,
    double? FoodNutrition,
    double? NutritionPerDay,
    double? FoodRunwayDays,
    double? PendingFoodNutrition,
    double? RaidPoints,
    double? WealthTotal,
    double? WealthItems,
    double? WealthBuildings,
    double? WealthPawns,
    string? PlayerTechLevel,
    string? TechTier,
    IReadOnlyList<ColonyShrineDto>? Shrines,
    IReadOnlyList<ColonyStockpileDto>? Stockpiles,
    bool? ForbiddenSupplies,
    int Downed,
    double? MoodMean,
    MoodLedgerDto? MoodLedger,
    IReadOnlyList<ColonyStatusPawnDto> Pawns);

internal sealed record ColonyStockpileDto(string Role, int Zones, int Cells, int Used);

internal sealed record ColonyShrineDto(
    string Id,
    bool Sealed,
    bool InHome,
    int Caskets,
    int FilledCaskets,
    bool GuardsKnown,
    bool GuardsAlive,
    int BreachWalls,
    bool? Ready,
    string? Reason,
    string? Wall,
    int Squad,
    int Traps);

internal sealed record ColonyStatusPawnDto(
    string Id,
    string Label,
    bool? Downed,
    double? Mood,
    double? Food,
    double? Share,
    double? Spent,
    double? Remaining);

internal sealed record MoodLedgerDto(
    IReadOnlyList<MoodSourceDto> Sources,
    IReadOnlyList<MoodSourceDto> Unowned,
    int UnknownPawns,
    IReadOnlyList<MoodExpectationDto> Expectation);

internal sealed record MoodSourceDto(string Def, int Pawns, double Lost, IReadOnlyList<string> Owners, int Unverified);

internal sealed record MoodExpectationDto(string Level, int Pawns);

internal sealed record FoodPlanDto(
    IReadOnlyList<FoodPlanEntryDto> Portfolio,
    IReadOnlyList<FoodPlanEntryDto> Unknown,
    double DeliveredPerDay,
    double DemandPerDay,
    double GapPerDay,
    string Explain,
    IReadOnlyList<PetShortfallDto> PetShortfalls);

internal sealed record PetShortfallDto(string Id, string? Label, double RunwayDays, double NutritionPerDay);

internal sealed record FoodPlanEntryDto(
    string Kind,
    string Id,
    string Decision,
    string Reason,
    double? NutritionPerDay,
    double? WorkPerDay,
    double? LeadDays,
    bool? Open,
    double DeliveredPerDay,
    IReadOnlyList<CandidateTermDto> Terms);

// Go serialized policy.CandidateTerm without tags, so its keys are the capitalized field names.
internal sealed record CandidateTermDto(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Value")] double Value);
