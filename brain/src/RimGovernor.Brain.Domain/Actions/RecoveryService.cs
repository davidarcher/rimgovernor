namespace RimGovernor.Brain.Domain;

/// <summary>
/// Names the native service jobs an already-selected pawn can be sent to run on an already-selected building:
/// ordinary repair, breakdown restoration, refuel and the void monolith's two orders. It mirrors
/// policy.RecoveryMethod's wire names; domain cannot import policy, so they are converted at the boundary.
/// </summary>
public enum RecoveryMethod
{
    Repair,
    Breakdown,
    Refuel,

    /// <summary>The Inactive monolith is investigated.</summary>
    InvestigateMonolith,

    /// <summary>Every later monolith level is activated.</summary>
    ActivateMonolith,

    /// <summary>The awakening quest's interaction: a void structure, the Gleaming monolith or the void node.</summary>
    [WireName("interact_thing")]
    Interact,
    InspectGravEngine,
    OfferHelp,
}

/// <summary>
/// Explicit intent to send one already-observed undrafted pawn to repair, restore or refuel one
/// already-observed building. It reuses a native GiveJobIntent service job (Actions/Apply). Native checks
/// reachability and current job eligibility live when it applies.
/// </summary>
public sealed record RecoveryService : IActionPayload
{
    public RecoveryService(PawnId pawn, string thing, RecoveryMethod method)
    {
        DomainException.Require(pawn.IsValid && Identity.IsValidText(thing), "recovery service requires a valid pawn and thing identity");
        DomainException.Require(Wire.IsDefined(method), "recovery service requires a valid method");
        Pawn = pawn;
        Thing = thing;
        Method = method;
    }

    public PawnId Pawn { get; }

    public string Thing { get; }

    public RecoveryMethod Method { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewRecoveryServiceAction(ActionId id, RecoveryService service) => Create(id, ActionKind.RecoveryService, service);

    public RecoveryService? RecoveryService => PayloadOf<RecoveryService>(ActionKind.RecoveryService);
}
