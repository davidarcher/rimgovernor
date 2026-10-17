#nullable enable

namespace RimGovernor.Fixtures.Contracts;

/// <summary>
/// <c>test/support_read</c> as the clean cases read it (#3408): the prepared support fixture's physical outcome. A
/// record carries only what a case reads.
/// </summary>
public sealed record SupportRead
{
    /// <summary>The prepared fixture is still active.</summary>
    public bool Success { get; init; }

    /// <summary>A participant cleaned and the room's cleanliness rose above its prepared value.</summary>
    public bool Cleaned { get; init; }

    /// <summary>The prepared bill completed a product.</summary>
    public bool Produced { get; init; }

    /// <summary>The patient was tended.</summary>
    public bool Tended { get; init; }

    /// <summary>The tend landed within the fixture's 2500-tick service budget.</summary>
    public bool CareWithinServiceBudget { get; init; }

    /// <summary>Some participant spent occupied ticks on the prepared support, none negative.</summary>
    public bool ParticipantAccounting { get; init; }
}
