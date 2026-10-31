#nullable enable

namespace RimGovernor.Fixtures.Contracts;

// The replies of the control cases' bespoke fixture ops (#3398): Hands executions installed straight into the native
// execution table over a staged precondition, and the physical facts they left. A record carries only what a case reads.

/// <summary>A pawn's Hands execution as the native execution table reads it.</summary>
public sealed record ActivityExecution
{
    /// <summary>"running", "completed" or "failed".</summary>
    public string State { get; init; } = "";

    /// <summary>Why the execution stopped; empty while it runs.</summary>
    public string Reason { get; init; } = "";

    public long Revision { get; init; }
}

/// <summary><c>test/pawn_control_read</c>: the producer's execution, job and needs, and the physical work done.</summary>
public sealed record ControlRead
{
    /// <summary>The producer's execution; null once it owns none.</summary>
    public ActivityExecution? Worker { get; init; }

    /// <summary>The producer's current JobDef.</summary>
    public string? Job { get; init; }

    /// <summary>The producer's Food and Rest levels, 0..1.</summary>
    public float Food { get; init; }

    public float Rest { get; init; }

    public bool CropHarvested { get; init; }

    /// <summary>The producer's job became player-forced.</summary>
    public bool Forced { get; init; }
}

/// <summary><c>test/pawn_sow_read</c>: the sower's execution and how many cells of the zone stand sown.</summary>
public sealed record SowRead
{
    public ActivityExecution? Execution { get; init; }

    public int Sown { get; init; }

    public bool Forced { get; init; }
}

/// <summary><c>test/pawn_delivery_read</c>: the hauler's execution and the edible harvest at each cell.</summary>
public sealed record DeliveryRead
{
    public ActivityExecution? Execution { get; init; }

    /// <summary>Edible harvest at the harvested stack's cell, the first destination and the revised one.</summary>
    public int Source { get; init; }

    public int Destination { get; init; }

    public int RevisedDestination { get; init; }

    /// <summary>The harvested stack's units when its delivery was first selected.</summary>
    public int HarvestedCount { get; init; }

    public bool Forced { get; init; }
}

/// <summary><c>test/vanilla_chop_read</c>: the colonist's current JobDef and whether a Hands execution owns it.</summary>
public sealed record VanillaJobRead
{
    public string Job { get; init; } = "";

    public bool Owned { get; init; }
}

/// <summary><c>test/preempt_haul_start</c> and <c>test/preempt_haul_read</c>: the holder's current job's load id (null for none) and whether it targets and reserves the stack.</summary>
public sealed record PreemptHaulRead
{
    public int? JobId { get; init; }

    public bool Holds { get; init; }
}
