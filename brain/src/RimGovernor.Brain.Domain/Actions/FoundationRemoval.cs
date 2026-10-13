namespace RimGovernor.Brain.Domain;

/// <summary>
/// Designates the foundation laid on one cell (a Bridge) for removal: a heavy bridge cannot be laid over a
/// plain one. The designation is the whole write; ordinary construction work lifts it, and a cell whose
/// foundation is already gone or designated applies again.
/// </summary>
public sealed record FoundationRemoval : IActionPayload
{
    /// <summary>Go <c>NewFoundationRemoval</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public FoundationRemoval(string definition, Cell cell)
    {
        DomainException.Require(Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0, "invalid foundation removal definition or cell");
        Definition = definition;
        Cell = cell;
    }

    public string Definition { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewFoundationRemovalAction(ActionId id, FoundationRemoval foundation) =>
        Create(id, ActionKind.FoundationRemoval, foundation);

    /// <summary>Go <c>Action.FoundationRemoval() (FoundationRemoval, bool)</c>: the payload when this is a foundation removal action, otherwise null.</summary>
    public FoundationRemoval? FoundationRemoval => PayloadOf<FoundationRemoval>(ActionKind.FoundationRemoval);
}
