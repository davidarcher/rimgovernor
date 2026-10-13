namespace RimGovernor.Brain.Domain;

/// <summary>
/// Deletes one ordinary production bill from a bench: the bench and the native bill id a placing receipt
/// journaled (Progress view Bill). Native re-resolves both live and refuses only a bill that is gone or not
/// ordinary; a worked bill ends its job cleanly and an unfinished item survives for any same-recipe bill unless
/// CancelsUnfinished is set. Go does not restate those guards, and the Round retries.
/// </summary>
public sealed record RemoveProductionBill : IActionPayload
{
    /// <summary>Go <c>NewRemoveProductionBill</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public RemoveProductionBill(string bench, string bill)
    {
        DomainException.Require(Identity.IsValidText(bench) && Identity.IsValidText(bill), "invalid production bill removal");
        Bench = bench;
        Bill = bill;
    }

    public string Bench { get; }

    public string Bill { get; }

    /// <summary>Whether the bill's bound unfinished item is destroyed (DestroyMode.Cancel, returning its ingredient share).</summary>
    public bool CancelsUnfinished { get; private init; }

    /// <summary>The removal that also destroys the bill's bound unfinished item.</summary>
    public RemoveProductionBill CancelUnfinished() => this with { CancelsUnfinished = true };
}

public sealed partial record DomainAction
{
    public static DomainAction NewRemoveProductionBillAction(ActionId id, RemoveProductionBill removal) =>
        Create(id, ActionKind.RemoveProductionBill, removal);

    public RemoveProductionBill? RemoveProductionBill => PayloadOf<RemoveProductionBill>(ActionKind.RemoveProductionBill);
}
