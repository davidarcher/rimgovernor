using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>A peace talks diplomat candidate: a capable negotiator's Social level, and whether they lead the colony's faction.</summary>
public sealed record PeaceTalksPawn
{
    public required PawnId Id { get; init; }

    public required int Social { get; init; }

    public required bool Leader { get; init; }
}
