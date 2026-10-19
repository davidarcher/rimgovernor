#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

/// <summary>The arguments of <c>test/throughput_prepare</c>: the Cloth stacks to spawn.</summary>
public sealed record ThroughputPrepareRequest
{
    public int ItemCount { get; init; }
}

/// <summary>The reply of <c>test/throughput_prepare</c>: the stockpile cells the speed matrix stages.</summary>
public sealed record ThroughputPrepared
{
    public IReadOnlyList<FixtureCell> StorageCells { get; init; } = [];
}

/// <summary>The arguments of <c>test/throughput_control</c>: the identity it checks and the "x:z,x:z" storage cells it reads.</summary>
public sealed record ThroughputControlRequest
{
    public string ColonyId { get; init; } = "";

    public string LoadToken { get; init; } = "";

    public int MapId { get; init; }

    public string StorageCells { get; init; } = "";
}

/// <summary>The reply of <c>test/throughput_control</c>: the stage's Cloth and wall counters at the read's tick.</summary>
public sealed record ThroughputControlRead
{
    public bool Success { get; init; }

    public long Tick { get; init; }

    public int StoredUnits { get; init; }

    public int WallsBuilt { get; init; }
}
