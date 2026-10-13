namespace RimGovernor.Brain.Domain;

/// <summary>
/// Names one native-approved source and its harvested resource. The payload of the acquisition, mine
/// acquisition and acquisition withdraw kinds.
/// </summary>
public sealed record Acquisition : IActionPayload
{
    /// <summary>Go <c>NewAcquisition</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Acquisition(string thing, string definition, Cell cell)
    {
        DomainException.Require(
            Identity.IsValidText(thing) && Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0,
            "invalid acquisition identity or cell"
        );
        Thing = thing;
        Definition = definition;
        Cell = cell;
    }

    public string Thing { get; }

    public string Definition { get; }

    public Cell Cell { get; }

    /// <summary>Reports a hunt: the acquired resource of an animal is its corpse.</summary>
    public bool Hunt => Definition.StartsWith("Corpse_", StringComparison.Ordinal);
}

public sealed partial record DomainAction
{
    public static DomainAction NewAcquisitionAction(ActionId id, Acquisition acquisition) => Create(id, ActionKind.Acquisition, acquisition);

    /// <summary>Go <c>Action.Acquisition() (Acquisition, bool)</c>: the payload when this is an acquisition action, otherwise null.</summary>
    public Acquisition? Acquisition => PayloadOf<Acquisition>(ActionKind.Acquisition);

    /// <summary>
    /// Go <c>NewAcquisitionWithdrawAction</c>: removes the harvest, hunt or mine designation on one source, the
    /// planner's stall withdraw, filed as its own one-action method. Shares its intent arm (the acquisition
    /// Designate, withdraw=true) with the acquisition that placed it.
    /// </summary>
    public static DomainAction NewAcquisitionWithdrawAction(ActionId id, Acquisition acquisition) =>
        Create(id, ActionKind.AcquisitionWithdraw, acquisition);

    /// <summary>Go <c>Action.AcquisitionWithdraw() (Acquisition, bool)</c>: the payload when this is an acquisition withdraw action, otherwise null.</summary>
    public Acquisition? AcquisitionWithdraw => PayloadOf<Acquisition>(ActionKind.AcquisitionWithdraw);

    /// <summary>
    /// Go <c>NewMineAcquisitionAction</c>: designates one already-selected native mine source through the same
    /// Actions/Apply acquisition Designate as an acquisition. It keeps its own kind because the resource
    /// planner, not the census acquisition planner, owns it; the payload is the same <see cref="Acquisition"/>.
    /// </summary>
    public static DomainAction NewMineAcquisitionAction(ActionId id, Acquisition acquisition) =>
        Create(id, ActionKind.MineAcquisition, acquisition);

    /// <summary>Go <c>Action.MineAcquisition() (Acquisition, bool)</c>: the payload when this is a mine acquisition action, otherwise null.</summary>
    public Acquisition? MineAcquisition => PayloadOf<Acquisition>(ActionKind.MineAcquisition);

    /// <summary>
    /// Go <c>Action.AcquireIntent()</c>: the acquisition Designate payload of an acquisition, a mine
    /// acquisition or a withdraw, and whether it is a withdraw; null for any other kind.
    /// </summary>
    public (Acquisition Acquisition, bool Withdraw)? AcquireIntent =>
        Kind switch
        {
            ActionKind.Acquisition when Payload is Acquisition a => (a, false),
            ActionKind.AcquisitionWithdraw when Payload is Acquisition a => (a, true),
            ActionKind.MineAcquisition when Payload is Acquisition a => (a, false),
            _ => null,
        };
}
