#nullable enable

namespace RimGovernor.Fixtures.Contracts;

// The replies of the fixture ops the harness lifecycle itself calls (#3410): the lab start, spawn and crew, a case's
// prepare op and the speed matrix's throughput control. A record carries only what the harness reads.

/// <summary><c>test/lab_start</c>: the wiped lab's map size and digest (the rest of the reply is recorded as it came).</summary>
public sealed record LabStarted
{
    public bool Success { get; init; }

    public int MapSize { get; init; }

    /// <summary>The lab map's digest.</summary>
    public string? Digest { get; init; }
}

/// <summary><c>test/lab_spawn</c>: the spawned thing's load id.</summary>
public sealed record LabSpawned
{
    public bool Success { get; init; }

    public string Id { get; init; } = "";
}

/// <summary><c>test/lab_crew</c>: the colonists the colony holds and how many the op added.</summary>
public sealed record LabCrewed
{
    public bool Success { get; init; }

    public int Colonists { get; init; }

    public int Added { get; init; }
}

/// <summary>
/// A case's <c>test/&lt;name&gt;_prepare</c> op: whether it ran and, when it reports one, the identity of the game it
/// prepared (an op that reports a load token reports the whole identity).
/// </summary>
public sealed record FixturePrepared
{
    public bool Success { get; init; }

    public string? ColonyId { get; init; }

    public string? LoadToken { get; init; }

    public int? MapId { get; init; }
}

/// <summary><c>test/throughput_control</c>: the stockpile and wall counters a speed-matrix outcome compares; a missing counter is -1.</summary>
public sealed record ThroughputControlCounters
{
    public int StoredStacks { get; init; } = -1;

    public int StoredUnits { get; init; } = -1;

    public int LooseUnits { get; init; } = -1;

    public int WallsBuilt { get; init; } = -1;
}
