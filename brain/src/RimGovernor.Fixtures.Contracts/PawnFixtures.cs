#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the pawn, need and medical cases' fixture ops (#3402). A record carries only what a case reads.

/// <summary><c>test/freeze_needs</c>'s inspect and release reply: whether the freeze is active and each free colonist's needs.</summary>
public sealed record FrozenNeedsRead
{
    public bool Success { get; init; }

    public bool Frozen { get; init; }

    public IReadOnlyList<FrozenNeedsPawn> Needs { get; init; } = [];
}

/// <summary>One free colonist's need levels.</summary>
public sealed record FrozenNeedsPawn
{
    public string Pawn { get; init; } = "";

    public IReadOnlyList<FrozenNeed> Needs { get; init; } = [];
}

/// <summary>One need's level (CurLevelPercentage) and whether the freeze pins it.</summary>
public sealed record FrozenNeed
{
    public string Def { get; init; } = "";

    public double Level { get; init; }

    public bool Frozen { get; init; }
}

/// <summary><c>test/medical_plague_prepare</c>'s reply: the two Plague patients, the first untended and the second tended.</summary>
public sealed record PlagueSetup
{
    public bool Success { get; init; }

    public IReadOnlyList<string> Patients { get; init; } = [];
}

/// <summary>
/// <c>test/medical_management_setup</c>'s reply: the surgical colonist missing a leg and that leg's body-part index, and
/// under a condition the second colonist's condition part and any held prisoner.
/// </summary>
public sealed record MedicalSetup
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public string Surgical { get; init; } = "";

    public int Part { get; init; }

    public string? ConditionPatient { get; init; }

    public int ConditionPart { get; init; }

    public string? PrisonerId { get; init; }
}
