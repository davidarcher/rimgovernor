namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to wear one already-observed apparel or weapon replacement already carried, produced or
/// otherwise reachable by an already-observed pawn. Native applies it as a GiveJobIntent Wear. Native
/// reachability, forced/locked gear and current outfit eligibility are established at inspection, not here.
/// </summary>
public sealed record GearReplace : IActionPayload
{
    /// <summary>Go <c>NewGearReplace</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public GearReplace(PawnId pawn, string thing, string definition)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(thing) && Identity.IsValidText(definition),
            "gear replace requires a valid pawn, thing and definition identity"
        );
        Pawn = pawn;
        Thing = thing;
        Definition = definition;
    }

    public PawnId Pawn { get; }

    public string Thing { get; }

    public string Definition { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewGearReplaceAction(ActionId id, GearReplace replace) => Create(id, ActionKind.GearReplace, replace);

    public GearReplace? GearReplace => PayloadOf<GearReplace>(ActionKind.GearReplace);
}
