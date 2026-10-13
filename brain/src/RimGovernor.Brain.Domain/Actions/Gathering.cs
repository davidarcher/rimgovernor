namespace RimGovernor.Brain.Domain;

/// <summary>
/// Starts one vanilla GatheringDef through GatheringDef.CanExecute and GatheringWorker.TryExecute. It names no
/// spot: the game chooses it, and a built PartySpot controls that choice. Ideology precept gatherings are rituals.
/// </summary>
public sealed record Gathering : IActionPayload
{
    /// <summary>Go <c>NewGathering</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Gathering(string def, PawnId organizer)
    {
        DomainException.Require(Identity.IsValidText(def) && organizer.IsValid, "gathering requires a gathering def and an organizer");
        Def = def;
        Organizer = organizer;
    }

    public string Def { get; }

    public PawnId Organizer { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewGatheringAction(ActionId id, Gathering gathering) => Create(id, ActionKind.Gathering, gathering);

    public Gathering? Gathering => PayloadOf<Gathering>(ActionKind.Gathering);
}
