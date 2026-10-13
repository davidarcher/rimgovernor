namespace RimGovernor.Brain.Domain;

/// <summary>
/// Mirrors the wire Assignment oneof (entity_id or Clear): the thing of the assigned kind (a bed, a throne, a
/// grave) the pawn currently owns, as the caller expects it at admission time, or explicitly none. Exactly one
/// of the two is set.
/// </summary>
public sealed record PreviousAssignment
{
    private PreviousAssignment(string id, bool clear)
    {
        Id = id;
        Clear = clear;
    }

    /// <summary>True when the pawn is asserted to own no thing of the kind.</summary>
    public bool Clear { get; }

    /// <summary>The exact thing the pawn is asserted to own (empty when <see cref="Clear"/>).</summary>
    public string Id { get; }

    /// <summary>Go <c>ClearPrevious</c>: asserts the pawn currently owns no thing of the kind.</summary>
    public static PreviousAssignment ClearPrevious() => new("", true);

    /// <summary>Go <c>KnownPrevious</c>: asserts the pawn currently owns the exact thing identified by <paramref name="id"/>.</summary>
    public static PreviousAssignment KnownPrevious(string id)
    {
        DomainException.Require(Identity.IsValidText(id), "invalid previous assignment identity");
        return new PreviousAssignment(id, false);
    }
}

/// <summary>
/// Explicit intent to assign one already-observed pawn to one already-observed assignable thing (a bed, a
/// throne: anything with CompAssignableToPawn), replacing the pawn's previously observed assignment of that
/// kind (if any), sent as an AssignIntent on Actions/Apply. Native resolves the thing's CompAssignableToPawn,
/// refuses a thing that has none, and checks reachability and current suitability when it applies.
/// </summary>
public sealed record Assign : IActionPayload
{
    /// <summary>Go <c>NewAssign</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Assign(PawnId pawn, string thing, PreviousAssignment previous)
        : this(pawn, thing, previous, false) { }

    private Assign(PawnId pawn, string thing, PreviousAssignment previous, bool swap)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(thing) && pawn.Value != thing,
            "assign requires a valid pawn, thing and previous-assignment expectation"
        );
        DomainException.Require(previous.Clear || previous.Id != thing, "assign previous assignment must differ from the assigned thing");
        Pawn = pawn;
        Thing = thing;
        Previous = previous;
        Swap = swap;
    }

    public PawnId Pawn { get; }

    public string Thing { get; }

    public PreviousAssignment Previous { get; }

    /// <summary>True when the assignment is a bedroom swap: native evicts the bed's current owner instead of refusing an owned bed.</summary>
    public bool Swap { get; }

    /// <summary>Go <c>AsSwap</c>: flags the assignment as a bedroom swap.</summary>
    public Assign AsSwap() => new(Pawn, Thing, Previous, true);
}

public sealed partial record DomainAction
{
    public static DomainAction NewAssignAction(ActionId id, Assign assign) => Create(id, ActionKind.Assign, assign);

    public Assign? Assign => PayloadOf<Assign>(ActionKind.Assign);
}
