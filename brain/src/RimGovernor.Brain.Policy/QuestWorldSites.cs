using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record SiteHomeRoute
{
    public int Tile { get; init; }

    public Fact<bool> Reachable { get; init; }

    public Fact<long> TravelTicks { get; init; }
}

public sealed record QuestHackTarget
{
    public string Id { get; init; } = "";

    public MapId Map { get; init; }

    public bool Spawned { get; init; }

    public bool Satisfied { get; init; }

    public bool LockedOut { get; init; }

    public bool Autohack { get; init; }

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

public sealed partial record QuestMonument
{
    public string Marker { get; init; } = "";

    public string Def { get; init; } = "";

    public MapId Map { get; init; }

    public Fact<bool> Packed { get; init; }

    public Fact<bool> Installed { get; init; }

    public Fact<bool> AllDone { get; init; }

    public Cell Cell { get; init; }

    public string DisallowedBuilding { get; init; } = "";

    public IReadOnlyList<Cell> InstallCells { get; init; } = [];

    public IReadOnlyList<QuestMonumentPiece> Pieces { get; init; } = [];

    public IReadOnlyList<QuestMonumentResource> Resources { get; init; } = [];

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

public sealed record PeaceTalksPawn
{
    public PawnId Id { get; init; }

    public Fact<int> Social { get; init; }

    public Fact<bool> Leader { get; init; }
}
