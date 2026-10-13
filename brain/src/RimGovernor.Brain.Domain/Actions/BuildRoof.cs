namespace RimGovernor.Brain.Domain;

/// <summary>
/// Designates vanilla BuildRoof (the Build Roof area) over cells (a BuildRoofIntent on Actions/Apply), the
/// mirror of <see cref="RemoveRoof"/>. The planner issues it over the shelter porch with the first battery
/// placed there. Applied means designated; the roof read decides when pawns finished. Canonical cells
/// (sorted, deduplicated), at least one.
/// </summary>
public sealed record BuildRoof : IActionPayload
{
    /// <summary>Go <c>NewBuildRoof</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public BuildRoof(IReadOnlyCollection<Cell> cells)
    {
        DomainException.Require(cells.Count != 0, "build roof needs cells");
        Cells = CanonicalCells.Sort(cells, "invalid build roof cell");
    }

    public EquatableArray<Cell> Cells { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewBuildRoofAction(ActionId id, BuildRoof roof) => Create(id, ActionKind.BuildRoof, roof);

    /// <summary>Go <c>Action.BuildRoof() (BuildRoof, bool)</c>: the payload when this is a build roof action, otherwise null.</summary>
    public BuildRoof? BuildRoof => PayloadOf<BuildRoof>(ActionKind.BuildRoof);
}
