#nullable enable

namespace RimGovernor.Fixtures.Contracts;

/// <summary>
/// The reply of <c>test/recovery_area_prepare</c>: a roofed refuge allowed Area and an outdoor allowed Area near the
/// first able colonist, that colonist restricted to the outdoor one, and a timed ToxicFallout registered on the map. The
/// stage primitives cannot make an allowed Area or restrict a pawn to one.
/// </summary>
public sealed record RecoveryAreaReply
{
    /// <summary>Whether the fixture ran; a precondition it cannot meet throws instead.</summary>
    public bool Success { get; init; }

    /// <summary>The restricted colonist's load id.</summary>
    public string Pawn { get; init; } = "";

    /// <summary>The roofed refuge Area's load id.</summary>
    public string Refuge { get; init; } = "";

    /// <summary>The outdoor Area's load id, the colonist's restriction.</summary>
    public string Outdoor { get; init; } = "";
}
