namespace RimGovernor.Brain.Domain;

/// <summary>
/// Designates one exact unprotected wastepack for hauling to storage: DesignateIntent HAUL on the pack under
/// the wastepack guard, which native holds to a pack that is neither frozen nor inside an atomizer. The
/// designation is the whole write; a hauler carries the pack to a stockpile that accepts it, and the goal
/// settles on the pack being frozen or atomized, never on the order. Names the stack by identity, definition
/// and cell.
/// </summary>
public sealed record WastepackHaul : IActionPayload
{
    /// <summary>Go <c>NewWastepackHaul</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public WastepackHaul(string thing, string definition, Cell cell)
    {
        DomainException.Require(
            Identity.IsValidText(thing) && Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0,
            "invalid wastepack haul identity or cell"
        );
        Thing = thing;
        Definition = definition;
        Cell = cell;
    }

    public string Thing { get; }

    public string Definition { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewWastepackHaulAction(ActionId id, WastepackHaul haul) => Create(id, ActionKind.WastepackHaul, haul);

    /// <summary>Go <c>Action.WastepackHaul() (WastepackHaul, bool)</c>: the payload when this is a wastepack haul action, otherwise null.</summary>
    public WastepackHaul? WastepackHaul => PayloadOf<WastepackHaul>(ActionKind.WastepackHaul);
}
