#nullable enable

namespace RimGovernor.Fixtures.Contracts;

// The reply of the hazard injection fixture (#3403). A record carries only what a case reads.

/// <summary>
/// <c>test/hazard_inject</c>: the game tick the op acted on, and for an injury the game's own bleed-out estimate
/// (0 when the pawn is not bleeding out).
/// </summary>
public sealed record HazardInjected
{
    public bool Success { get; init; }

    public int Tick { get; init; }

    public int BleedOutTicks { get; init; }
}
