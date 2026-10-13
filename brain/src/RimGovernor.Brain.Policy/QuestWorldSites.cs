using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record WorldSite
{
    public string Id { get; init; } = "";

    public string Def { get; init; } = "";

    public string Label { get; init; } = "";

    public Fact<int> Tile { get; init; }

    public Fact<int> Layer { get; init; }

    public Pb.WorldSiteState State { get; init; }

    public Fact<MapId> Map { get; init; }

    public Fact<double> DistanceTiles { get; init; }

    public IReadOnlyList<QuestId> QuestIds { get; init; } = [];

    public Fact<bool> Threat { get; init; }

    public Fact<double> ThreatPoints { get; init; }

    public Fact<bool> Reachable { get; init; }

    public Fact<long> TravelTicks { get; init; }

    public IReadOnlyList<PawnId> RoutePawnIds { get; init; } = [];

    public Fact<SiteSecurity> Security { get; init; }

    public Fact<SiteExtraction> Extraction { get; init; }

    public Fact<PeaceTalksRisk> PeaceTalks { get; init; }

    public IReadOnlyList<SiteMiningTarget> MiningTargets { get; init; } = [];
}

public sealed record SiteMiningTarget
{
    public string Id { get; init; } = "";

    public string Def { get; init; } = "";

    public Cell Cell { get; init; }

    public MapId Map { get; init; }

    public Fact<bool> Designated { get; init; }
}

public sealed record SiteSecurity
{
    public Fact<bool> Known { get; init; }

    public Fact<bool> ActiveThreat { get; init; }

    public Fact<bool> DormantThreat { get; init; }

    public Fact<bool> DetectionActive { get; init; }

    public Fact<double> InitialPoints { get; init; }

    public Fact<double> PendingRaidPoints { get; init; }

    public Fact<int> TrapCount { get; init; }

    public Fact<int> RaidsSent { get; init; }

    public Fact<long> DetectionTicksLeft { get; init; }
}

public sealed record SiteExtraction
{
    public Fact<bool> CanReform { get; init; }

    public IReadOnlyList<PawnId> Crew { get; init; } = [];

    public Fact<double> CarryCapacity { get; init; }

    public Fact<double> CarriedMass { get; init; }

    public Fact<double> InventoryFoodDays { get; init; }

    public Fact<double> CrewNutritionPerDay { get; init; }

    public IReadOnlyList<SiteCargo> Cargo { get; init; } = [];

    public IReadOnlyList<SiteHomeRoute> HomeRoutes { get; init; } = [];

    public IReadOnlyList<Cell> ExitCells { get; init; } = [];
}

public sealed record SiteCargo
{
    public string Id { get; init; } = "";

    public string Def { get; init; } = "";

    public Fact<long> Count { get; init; }

    public Fact<double> UnitMass { get; init; }

    public Fact<double> MarketValue { get; init; }

    public Fact<double> Nutrition { get; init; }

    public Fact<bool> Held { get; init; }
}

public sealed record SiteHomeRoute
{
    public MapId Map { get; init; }

    public int Tile { get; init; }

    public Fact<bool> Reachable { get; init; }

    public Fact<long> TravelTicks { get; init; }
}

public sealed record QuestSurveyScanner
{
    public string SiteId { get; init; } = "";

    public string ScannerId { get; init; } = "";

    public Fact<long> DurationTicks { get; init; }

    public Fact<long> EndTick { get; init; }

    public Fact<long> RaidTick { get; init; }

    public Fact<bool> Alive { get; init; }

    public Fact<bool> Complete { get; init; }
}

public sealed record QuestGravEngine
{
    public string Id { get; init; } = "";

    public MapId Map { get; init; }

    public Fact<bool> Spawned { get; init; }

    public Fact<bool> Inspected { get; init; }

    public Cell Cell { get; init; }

    public IReadOnlyList<PawnId> EligiblePawns { get; init; } = [];

    public IReadOnlyList<PawnId> InspectingPawnIds { get; init; } = [];
}

public sealed record QuestHackTarget
{
    public string Id { get; init; } = "";

    public MapId Map { get; init; }

    public Fact<bool> Spawned { get; init; }

    public Fact<bool> Hacked { get; init; }

    public Fact<bool> Satisfied { get; init; }

    public Fact<bool> LockedOut { get; init; }

    public Fact<bool> Autohack { get; init; }

    public Fact<double> ProgressPercent { get; init; }

    public Fact<double> Defence { get; init; }

    public IReadOnlyList<PawnId> EligiblePawnIds { get; init; } = [];
}

public sealed record QuestHackRisk
{
    public string FactionId { get; init; } = "";

    public Fact<bool> Hostile { get; init; }
}

public sealed record QuestGiftRequest
{
    public PawnId Recipient { get; init; }

    public MapId Map { get; init; }

    public IReadOnlyList<PawnId> PawnIds { get; init; } = [];

    public string Def { get; init; } = "";

    public Fact<long> Remaining { get; init; }

    public IReadOnlyList<PawnId> HaulingPawnIds { get; init; } = [];

    public IReadOnlyList<PawnId> EligiblePawnIds { get; init; } = [];
}

public sealed record QuestMonument
{
    public string Marker { get; init; } = "";

    public string Def { get; init; } = "";

    public MapId Map { get; init; }

    public Fact<bool> Packed { get; init; }

    public Fact<bool> Installed { get; init; }

    public Fact<bool> Complete { get; init; }

    public Fact<bool> AllDone { get; init; }

    public Cell Cell { get; init; }

    public string DisallowedBuilding { get; init; } = "";

    public Fact<long> DisallowedTicks { get; init; }

    public IReadOnlyList<Cell> InstallCells { get; init; } = [];

    public IReadOnlyList<QuestMonumentPiece> Pieces { get; init; } = [];

    public IReadOnlyList<QuestMonumentResource> Resources { get; init; } = [];

    public Fact<bool> Offered { get; init; }

    public Fact<bool> ClearSite { get; init; }

    public IReadOnlyList<Amount> SuppliedResources { get; init; } = [];

    public IReadOnlyList<Amount> AvailableResources { get; init; } = [];
}

public sealed record QuestMonumentPiece
{
    public string Def { get; init; } = "";

    public string Stuff { get; init; } = "";

    public Cell Offset { get; init; }

    public Rotation Rotation { get; init; }

    public Fact<bool> Built { get; init; }

    public Fact<bool> Queued { get; init; }

    public Fact<bool> Allowed { get; init; }

    public IReadOnlyList<string> AllowedStuffs { get; init; } = [];

    public IReadOnlyList<Cell> Footprint { get; init; } = [];

    public IReadOnlyList<QuestMonumentBuildOption> BuildOptions { get; init; } = [];
}

public sealed record QuestMonumentBuildOption
{
    public string Stuff { get; init; } = "";

    public IReadOnlyList<Amount> Costs { get; init; } = [];

    public double Work { get; init; }
}

public sealed record QuestMonumentResource
{
    public string Id { get; init; } = "";

    public string Def { get; init; } = "";

    public Cell Cell { get; init; }

    public Fact<bool> InStorage { get; init; }

    public IReadOnlyList<PawnId> Haulers { get; init; } = [];
}

/// <summary>
/// Loss includes vanilla disaster's forced-hostility adjustment; gain is capped by native faction goodwill
/// limits. These are observations, not Go tuning.
/// </summary>
public sealed record PeaceTalksRisk
{
    public Fact<int> WorstGoodwillLoss { get; init; }

    public Fact<int> BestGoodwillGain { get; init; }

    public Fact<bool> IdeologyActive { get; init; }
}

public sealed record PeaceTalksPawn
{
    public PawnId Id { get; init; }

    public Fact<int> Social { get; init; }

    public Fact<bool> Leader { get; init; }
}
