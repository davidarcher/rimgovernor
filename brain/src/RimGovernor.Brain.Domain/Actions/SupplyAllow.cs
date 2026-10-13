namespace RimGovernor.Brain.Domain;

/// <summary>
/// Names one observed loose item, never everything at a map cell. Serves both the supply allow and the supply
/// forbid action kinds; <see cref="Forbidden"/> picks the kind.
/// </summary>
public sealed record SupplyAllow : IActionPayload
{
    /// <summary>Go <c>NewSupplyAllow</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public SupplyAllow(string thing, string definition, Cell cell)
        : this(thing, definition, cell, false) { }

    private SupplyAllow(string thing, string definition, Cell cell, bool forbid)
    {
        DomainException.Require(
            Identity.IsValidText(thing) && Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0,
            "invalid supply identity or cell"
        );
        Thing = thing;
        Definition = definition;
        Cell = cell;
        Forbidden = forbid;
    }

    /// <summary>Go <c>NewSupplyForbid</c>: the same value, marked forbid.</summary>
    public static SupplyAllow NewSupplyForbid(string thing, string definition, Cell cell) => new(thing, definition, cell, true);

    public bool Forbidden { get; }

    public string Designation => Forbidden ? "Forbid" : "Allow";

    public string Thing { get; }

    public string Definition { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    /// <summary>Go <c>NewSupplyAllowAction</c>: the kind is supply forbid when the payload is forbidden, supply allow otherwise.</summary>
    public static DomainAction NewSupplyAllowAction(ActionId id, SupplyAllow supply) =>
        Create(id, supply.Forbidden ? ActionKind.SupplyForbid : ActionKind.SupplyAllow, supply);

    /// <summary>Go <c>Action.SupplyAllow() (SupplyAllow, bool)</c>: the payload when this is a supply allow or forbid action, otherwise null.</summary>
    public SupplyAllow? SupplyAllow =>
        Kind is ActionKind.SupplyAllow or ActionKind.SupplyForbid ? Payload as SupplyAllow : null;
}
