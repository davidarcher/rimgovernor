#nullable enable

using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the horror incident fixture and the letter pause-mode setter (#3404). A record carries only what a case reads.

/// <summary><c>test/letter_pause_mode</c>: the automatic pause mode now in force.</summary>
public sealed record LetterPauseMode
{
    public bool Success { get; init; }

    public string Mode { get; init; } = "";
}

/// <summary><c>test/horror_incident</c>: the incident fired, the colonists defending and the hostile arrivals, by load id.</summary>
public sealed record HorrorIncidentFired
{
    public bool Success { get; init; }

    public string Incident { get; init; } = "";

    public IReadOnlyList<string> ColonistIds { get; init; } = [];

    public IReadOnlyList<string> ThreatIds { get; init; } = [];
}

/// <summary><c>test/horror_inspect</c>: each named colonist's and arrival's state on the live game.</summary>
public sealed record HorrorInspectRead
{
    public bool Success { get; init; }

    public int Tick { get; init; }

    public IReadOnlyList<HorrorPawnRow> Colonists { get; init; } = [];

    public IReadOnlyList<HorrorPawnRow> Threats { get; init; } = [];
}

/// <summary>One pawn of <see cref="HorrorInspectRead"/>; <see cref="Found"/> false when the game no longer holds it.</summary>
public sealed record HorrorPawnRow
{
    public string Id { get; init; } = "";

    public bool Found { get; init; }

    public bool Dead { get; init; }

    public bool Destroyed { get; init; }

    public bool Spawned { get; init; }

    public bool Downed { get; init; }
}
