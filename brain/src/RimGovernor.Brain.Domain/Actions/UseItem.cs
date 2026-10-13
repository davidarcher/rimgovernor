namespace RimGovernor.Brain.Domain;

/// <summary>
/// Orders one colonist to use one item: a worn or equipped item's target verb on a pawn (the psychic shock and
/// insanity lances' Verb_CastTargetEffect), a CompTargetable item's use job on a pawn, or, when the pawn is its
/// own target, the use job of a CompUsable item without a target comp (a neuroformer). Native validates the
/// user, the item's verb or use comp and the target live when the GiveJobIntent UseItem applies.
/// </summary>
public sealed record UseItem : IActionPayload
{
    /// <summary>Go <c>NewUseItem</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public UseItem(PawnId pawn, string item, PawnId target)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(item) && target.IsValid && item != pawn.Value && item != target.Value,
            "use item requires valid pawn, item and target identities, the item distinct from both"
        );
        Pawn = pawn;
        Item = item;
        Target = target;
    }

    public PawnId Pawn { get; }

    public string Item { get; }

    public PawnId Target { get; }

    /// <summary>Whether the colonist uses the item on itself.</summary>
    public bool SelfUse => Pawn == Target;
}

public sealed partial record DomainAction
{
    public static DomainAction NewUseItemAction(ActionId id, UseItem use) => Create(id, ActionKind.UseItem, use);

    public UseItem? UseItem => PayloadOf<UseItem>(ActionKind.UseItem);
}
