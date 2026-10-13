namespace RimGovernor.Brain.Domain;

/// <summary>
/// Orders every non-crop plant on cells cut (an AreaPlantCutIntent on Actions/Apply). Wild plants get
/// CutPlant, harvestable trees chop-wood; growing-zone plants and sown crops are never touched. Native
/// validates the cells live; a cell with nothing to cut is a no-op. Applied means designated; the plant cut
/// census decides when pawns finished. Canonical cells (sorted, deduplicated), at least one.
/// </summary>
public sealed record AreaPlantCut : IActionPayload
{
    /// <summary>Go <c>NewAreaPlantCut</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public AreaPlantCut(IReadOnlyCollection<Cell> cells)
    {
        DomainException.Require(cells.Count != 0, "area plant cut needs at least one cell");
        Cells = CanonicalCells.Sort(cells, "invalid area plant cut cell");
    }

    public EquatableArray<Cell> Cells { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewAreaPlantCutAction(ActionId id, AreaPlantCut cut) => Create(id, ActionKind.AreaPlantCut, cut);

    /// <summary>Go <c>Action.AreaPlantCut() (AreaPlantCut, bool)</c>: the payload when this is an area plant cut action, otherwise null.</summary>
    public AreaPlantCut? AreaPlantCut => PayloadOf<AreaPlantCut>(ActionKind.AreaPlantCut);
}
