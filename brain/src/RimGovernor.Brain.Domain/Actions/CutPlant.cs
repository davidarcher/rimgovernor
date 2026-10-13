namespace RimGovernor.Brain.Domain;

/// <summary>
/// Designates one observed blighted plant for cutting: the exact plant by identity, never everything at a map
/// cell. The designation is the whole write; ordinary plant-cutting work does the cut.
/// </summary>
public sealed record CutPlant : IActionPayload
{
    /// <summary>Go <c>NewCutPlant</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public CutPlant(string plant, string definition, Cell cell)
    {
        DomainException.Require(
            Identity.IsValidText(plant) && Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0,
            "invalid plant identity or cell"
        );
        Plant = plant;
        Definition = definition;
        Cell = cell;
    }

    public string Plant { get; }

    public string Definition { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewCutPlantAction(ActionId id, CutPlant cut) => Create(id, ActionKind.CutPlant, cut);

    /// <summary>Go <c>Action.CutPlant() (CutPlant, bool)</c>: the payload when this is a cut plant action, otherwise null.</summary>
    public CutPlant? CutPlant => PayloadOf<CutPlant>(ActionKind.CutPlant);
}
