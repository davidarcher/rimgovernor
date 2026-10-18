namespace RimGovernor.Brain.Domain;

/// <summary>
/// Orders the moral guide's Convert ability on one colonist who holds another ideoligion (#3501). Native resolves the
/// guide, the target and the ability's charge when it applies; the order names the two pawns and nothing the world owns.
/// </summary>
public sealed record Conversion : IActionPayload
{
    /// <summary>Throws <see cref="DomainException"/> when invalid.</summary>
    public Conversion(PawnId guide, PawnId target)
    {
        DomainException.Require(guide.IsValid && target.IsValid && guide != target, "conversion requires a guide and a different target");
        Guide = guide;
        Target = target;
    }

    public PawnId Guide { get; }

    public PawnId Target { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewConversionAction(ActionId id, Conversion conversion) => Create(id, ActionKind.Conversion, conversion);

    public Conversion? Conversion => PayloadOf<Conversion>(ActionKind.Conversion);
}
