namespace RimGovernor.Brain.Domain;

/// <summary>
/// Re-sites one exact installed player building through the game's Reinstall: a reinstall blueprint at the
/// destination, then ordinary construction work uninstalls the piece and installs it there. The piece keeps its
/// identity, quality and hit points; the uninstall waits on the piece's reservation, so nobody using it is
/// interrupted. Completion is the same building observed installed at the cell and rotation. It is also the
/// payload of an uninstall (Go <c>UninstallBuilding = MoveBuilding</c>): the building's current placement.
/// </summary>
public sealed record MoveBuilding : IActionPayload
{
    /// <summary>Go <c>NewMoveBuilding</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public MoveBuilding(string thing, string definition, Cell cell, Rotation rotation)
    {
        DomainException.Require(
            Identity.IsValidText(thing) && Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0,
            "invalid move identity or destination"
        );
        DomainException.Require(Wire.IsDefined(rotation), "invalid move rotation");
        Thing = thing;
        Definition = definition;
        Cell = cell;
        Rotation = rotation;
    }

    /// <summary>The installed building's load id; it survives the move.</summary>
    public string Thing { get; }

    public string Definition { get; }

    /// <summary>The destination placement cell.</summary>
    public Cell Cell { get; }

    /// <summary>The destination placement rotation.</summary>
    public Rotation Rotation { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewMoveBuildingAction(ActionId id, MoveBuilding move) => Create(id, ActionKind.MoveBuilding, move);

    /// <summary>Go <c>Action.MoveBuilding() (MoveBuilding, bool)</c>: the payload when this is a move building action, otherwise null.</summary>
    public MoveBuilding? MoveBuilding => PayloadOf<MoveBuilding>(ActionKind.MoveBuilding);

    /// <summary>
    /// Go <c>NewUninstallBuildingAction</c>: packs one exact installed player building through the game's
    /// Uninstall designation: ordinary work minifies it where it stands and vanilla hauling takes the packed
    /// item to storage. The value is the building's current placement (MoveBuilding's shape), which the native
    /// effect echoes; the uninstall waits on the building's reservation like a move.
    /// </summary>
    public static DomainAction NewUninstallBuildingAction(ActionId id, MoveBuilding building) =>
        Create(id, ActionKind.UninstallBuilding, building);

    /// <summary>Go <c>Action.UninstallBuilding()</c>: the placement when this is an uninstall building action, otherwise null.</summary>
    public MoveBuilding? UninstallBuilding => PayloadOf<MoveBuilding>(ActionKind.UninstallBuilding);

    /// <summary>
    /// Go <c>Action.Relocation()</c>: the building of a move or an uninstall (the two kinds share the executor,
    /// admission and bridge plumbing), and whether it is an uninstall; null for any other kind.
    /// </summary>
    public (MoveBuilding Building, bool Uninstall)? Relocation =>
        Kind switch
        {
            ActionKind.MoveBuilding when Payload is MoveBuilding move => (move, false),
            ActionKind.UninstallBuilding when Payload is MoveBuilding move => (move, true),
            _ => null,
        };
}
