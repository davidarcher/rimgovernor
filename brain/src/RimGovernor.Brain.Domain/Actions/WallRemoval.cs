namespace RimGovernor.Brain.Domain;

/// <summary>
/// Clears the colonist wall at one cell as one step of a staged stone-shell replacement bundle. It either
/// removes the pre-existing wall the bundle replaces (BackupOf empty, Original its native identity captured
/// at proposal time) or a temporary backup wall this same plan built to hold the shell open while the original
/// is rebuilt (BackupOf names that same-plan Wall building action, whose cell is Cell; Original empty). It is
/// an Actions/Apply intent: native resolves the wall-upgrade site from the wall live, and applied means the
/// guarded demolition is designated. Work that needs the cell clear waits on the census.
/// </summary>
public sealed record WallRemoval : IActionPayload
{
    /// <summary>Go <c>NewWallRemoval</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public WallRemoval(string original, ActionId backupOf, Cell cell)
    {
        DomainException.Require(
            !(backupOf.IsEmpty && !Identity.IsValidText(original)),
            "wall removal requires either an original native identity or a backup prerequisite"
        );
        DomainException.Require(
            !(!backupOf.IsEmpty && (original.Length != 0 || !backupOf.IsValid)),
            "backup wall removal must reference its backup prerequisite alone"
        );
        DomainException.Require(cell.X >= 0 && cell.Z >= 0, "wall removal cell must be nonnegative");
        Original = original;
        BackupOf = backupOf;
        Cell = cell;
    }

    public string Original { get; }

    public ActionId BackupOf { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewWallRemovalAction(ActionId id, WallRemoval removal)
    {
        DomainException.Require(id.IsValid && id != removal.BackupOf, "invalid wall removal action identity or self prerequisite");
        return Create(id, ActionKind.WallRemoval, removal);
    }

    /// <summary>Go <c>Action.WallRemoval() (WallRemoval, bool)</c>: the payload when this is a wall removal action, otherwise null.</summary>
    public WallRemoval? WallRemoval => PayloadOf<WallRemoval>(ActionKind.WallRemoval);
}
