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

/// <summary><c>test/baby_care_prepare</c>: the staged baby, the kitchen's stove, its baby-edible recipes and the baby's staged food level.</summary>
public sealed record BabyCarePrepared
{
    public bool Success { get; init; }

    public string? Reason { get; init; }

    public string BabyId { get; init; } = "";

    public string BenchId { get; init; } = "";

    public IReadOnlyList<string> Recipes { get; init; } = [];

    public double FoodLevel { get; init; }
}

/// <summary><c>test/baby_care_inspect</c>: the baby's stage, food and malnutrition, and the stove's production bills.</summary>
public sealed record BabyCareRead
{
    public bool Success { get; init; }

    public string? Reason { get; init; }

    public bool Dead { get; init; }

    public string Stage { get; init; } = "";

    public double FoodLevel { get; init; }

    public double Malnutrition { get; init; }

    public IReadOnlyList<BabyCareBill> Bills { get; init; } = [];
}

/// <summary>One production bill on the first-child stove.</summary>
public sealed record BabyCareBill
{
    public string Recipe { get; init; } = "";

    public bool BabyEdible { get; init; }
}

/// <summary><c>test/population_setup</c>: the downed hostile candidate, the downed friendly visitor, the prisoner bed and each worker's bed check.</summary>
public sealed record PopulationSetup
{
    public bool Success { get; init; }

    public string Candidate { get; init; } = "";

    public string Visitor { get; init; } = "";

    public string Prison { get; init; } = "";

    public IReadOnlyList<PopulationBedCheck> BedChecks { get; init; } = [];
}

/// <summary>Whether one worker can carry the candidate to the prisoner bed native would pick.</summary>
public sealed record PopulationBedCheck
{
    public string Pawn { get; init; } = "";

    public bool Usable { get; init; }

    public bool Reachable { get; init; }

    public bool Reservable { get; init; }

    public string? NativeBed { get; init; }
}
