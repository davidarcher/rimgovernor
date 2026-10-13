namespace RimGovernor.Brain.Domain;

/// <summary>
/// Asks vanilla GiveToPawn to fulfill the remaining request. The count is an observed precondition, not a cap on
/// the native job's delivery.
/// </summary>
public sealed record GiveItem : IActionPayload
{
    /// <summary>Go <c>NewGiveItem</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public GiveItem(PawnId hauler, PawnId recipient, string definition, long expectedRemaining)
    {
        DomainException.Require(
            hauler.IsValid && recipient.IsValid && hauler != recipient && Identity.IsValidText(definition) && expectedRemaining > 0 && expectedRemaining <= int.MaxValue,
            "give item requires exact actors, definition and positive native remaining count"
        );
        Hauler = hauler;
        Recipient = recipient;
        Definition = definition;
        ExpectedRemaining = expectedRemaining;
    }

    public PawnId Hauler { get; }

    public PawnId Recipient { get; }

    public string Definition { get; }

    public long ExpectedRemaining { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewGiveItemAction(ActionId id, GiveItem give) => Create(id, ActionKind.GiveItem, give);

    public GiveItem? GiveItem => PayloadOf<GiveItem>(ActionKind.GiveItem);
}
