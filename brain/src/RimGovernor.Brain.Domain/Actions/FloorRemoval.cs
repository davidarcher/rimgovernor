namespace RimGovernor.Brain.Domain;

/// <summary>
/// Designates the constructed floor laid on one cell for removal (vanilla RemoveFloor) so clearance can free
/// planned ground. The designation is the whole write; ordinary construction work removes the floor, and a
/// cell whose floor is already gone or designated applies again.
/// </summary>
public sealed record FloorRemoval : IActionPayload
{
    /// <summary>Go <c>NewFloorRemoval</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public FloorRemoval(string definition, Cell cell)
    {
        DomainException.Require(Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0, "invalid floor removal definition or cell");
        Definition = definition;
        Cell = cell;
    }

    public string Definition { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewFloorRemovalAction(ActionId id, FloorRemoval floor) => Create(id, ActionKind.FloorRemoval, floor);

    /// <summary>Go <c>Action.FloorRemoval() (FloorRemoval, bool)</c>: the payload when this is a floor removal action, otherwise null.</summary>
    public FloorRemoval? FloorRemoval => PayloadOf<FloorRemoval>(ActionKind.FloorRemoval);
}
