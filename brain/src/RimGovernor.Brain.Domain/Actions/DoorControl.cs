namespace RimGovernor.Brain.Domain;

/// <summary>
/// The desired hold-open setting for one door cell, applied to one player door's hold-open latch through
/// CombatOrders. Applied confirms the setting; physical opening or closing requires a separate observation.
/// Opening requires ordinary pawn passage; closing waits for clearance.
/// </summary>
public sealed record DoorControl : IActionPayload
{
    /// <summary>Go <c>NewDoorControl</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public DoorControl(Cell cell, bool holdOpen)
    {
        DomainException.Require(cell.X >= 0 && cell.Z >= 0, "door control requires a valid cell");
        Cell = cell;
        HoldOpen = holdOpen;
    }

    public Cell Cell { get; }

    public bool HoldOpen { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewDoorControlAction(ActionId id, DoorControl door) => Create(id, ActionKind.DoorControl, door);

    /// <summary>Go <c>Action.DoorControl() (DoorControl, bool)</c>: the payload when this is a door control action, otherwise null.</summary>
    public DoorControl? DoorControl => PayloadOf<DoorControl>(ActionKind.DoorControl);
}
