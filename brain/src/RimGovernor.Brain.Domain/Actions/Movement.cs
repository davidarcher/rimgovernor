namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to walk one already-drafted pawn to one exact cell, layered on an existing owned draft
/// exactly like <see cref="Subdue"/>. It proves nothing about pathability, reachability or permission to
/// dispatch a job.
/// </summary>
public sealed record Movement : IActionPayload
{
    public Movement(PawnId pawn, Cell destination, ActionId prerequisite)
    {
        DomainException.Require(pawn.IsValid, "movement requires a valid pawn identity");
        DomainException.Require(destination.X >= 0 && destination.Z >= 0, "movement destination must be nonnegative");
        DomainException.Require(prerequisite.IsValid, "invalid movement draft prerequisite");
        Pawn = pawn;
        Destination = destination;
        DraftAction = prerequisite;
    }

    public PawnId Pawn { get; }

    public Cell Destination { get; }

    public ActionId DraftAction { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewMovementAction(ActionId id, Movement movement)
    {
        DomainException.Require(id != movement.DraftAction, "invalid movement action identity or self prerequisite");
        return Create(id, ActionKind.Movement, movement);
    }

    public Movement? Movement => PayloadOf<Movement>(ActionKind.Movement);
}
