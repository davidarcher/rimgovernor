#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

/// <summary>The arguments of <c>test/throughput_prepare</c>: the Steel stacks to spawn and the wall segments to lay out.</summary>
public sealed record ThroughputPrepareRequest
{
    public int ItemCount { get; init; }

    public int WallSegments { get; init; }
}

/// <summary>One wall site <c>test/throughput_prepare</c> laid out.</summary>
public sealed record ThroughputSite
{
    public string DefName { get; init; } = "";

    public string Stuff { get; init; } = "";

    public string Rotation { get; init; } = "";

    public int X { get; init; }

    public int Z { get; init; }
}

/// <summary>The reply of <c>test/throughput_prepare</c>: the stockpile cells and the wall run the speed matrix stages.</summary>
public sealed record ThroughputPrepared
{
    public IReadOnlyList<FixtureCell> StorageCells { get; init; } = [];

    public IReadOnlyList<ThroughputSite> Sites { get; init; } = [];
}

/// <summary>The arguments of <c>test/throughput_control</c>: the identity it checks and the "x:z,x:z" cell lists it reads.</summary>
public sealed record ThroughputControlRequest
{
    public string ColonyId { get; init; } = "";

    public string LoadToken { get; init; } = "";

    public int MapId { get; init; }

    public string StorageCells { get; init; } = "";

    public string WallCells { get; init; } = "";
}

/// <summary>The reply of <c>test/throughput_control</c>: the stage's Steel and wall counters at the read's tick.</summary>
public sealed record ThroughputControlRead
{
    public bool Success { get; init; }

    public long Tick { get; init; }

    public int StoredUnits { get; init; }

    public int WallsBuilt { get; init; }

    public int WallBlueprints { get; init; }
}
