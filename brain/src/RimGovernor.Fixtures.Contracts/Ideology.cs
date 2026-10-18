#nullable enable

using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the ideoligion design prepare and first-ritual fixtures (#3404); the design inspect read is in
// StartReads.cs. Designs and the explicit ritual intent travel as wire JSON text the case decodes through the wire
// contracts. A record carries only what a case reads.

/// <summary><c>test/ideoligion_design_prepare</c>: the eligible fluid ideoligion, its current design and a legal reform target.</summary>
public sealed record IdeoligionDesignPrepared
{
    public bool Success { get; init; }

    public string IdeoId { get; init; } = "";

    public string Expected { get; init; } = "";

    public string Design { get; init; } = "";

    public int Count { get; init; }
}

/// <summary><c>test/first_ritual_prepare</c>: the staged ritual precept, its building and its aged last finished tick.</summary>
public sealed record FirstRitualPrepared
{
    public bool Success { get; init; }

    public string RitualId { get; init; } = "";

    public string BuildingId { get; init; } = "";

    public int LastFinishedTick { get; init; }

    public IReadOnlyList<string> PawnIds { get; init; } = [];
}

/// <summary><c>test/convert_colonist_prepare</c>: the colonist holding the Convert-granting role and the colonist staged with another ideoligion.</summary>
public sealed record ConvertColonistPrepared
{
    public bool Success { get; init; }

    public string GuideId { get; init; } = "";

    public string TargetId { get; init; } = "";

    public string TargetIdeoId { get; init; } = "";

    public double Certainty { get; init; }
}

/// <summary><c>test/convert_colonist_inspect</c>: the target's ideoligion and certainty.</summary>
public sealed record ConvertColonistRead
{
    public bool Success { get; init; }

    public string TargetIdeoId { get; init; } = "";

    public double Certainty { get; init; }
}

/// <summary><c>test/first_ritual_inspect</c>: whether the ritual's lord runs, its last finished tick and the native dialog's legal explicit intent.</summary>
public sealed record FirstRitualRead
{
    public bool Success { get; init; }

    public bool Running { get; init; }

    public int Participants { get; init; }

    public int LastFinishedTick { get; init; }

    public string ExplicitIntent { get; init; } = "";
}
