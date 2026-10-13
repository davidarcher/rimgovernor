
namespace RimGovernor.Brain.Domain;

/// <summary>
/// The complete desired native filter. Token binds the observed assignment and filter state; intervening edits
/// invalidate it.
/// </summary>
public sealed record ApparelPolicySpec(
    PawnId Pawn,
    string Token,
    string Name,
    EquatableArray<string> Definitions,
    double MinHP,
    double MaxHP,
    int MinQuality,
    int MaxQuality
);

/// <summary>An apparel policy action payload: a validated <see cref="ApparelPolicySpec"/> with sorted, unique definitions.</summary>
public sealed record ApparelPolicy : IActionPayload
{
    /// <summary>Go <c>NewApparelPolicy</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public ApparelPolicy(ApparelPolicySpec spec)
    {
        DomainException.Require(
            spec.Pawn.IsValid
                && Identity.IsValidText(spec.Token)
                && Identity.IsValidText(spec.Name)
                && spec.Definitions.Count != 0
                && !double.IsNaN(spec.MinHP)
                && !double.IsNaN(spec.MaxHP)
                && spec.MinHP >= 0
                && spec.MaxHP <= 1
                && spec.MinHP <= spec.MaxHP
                && spec.MinQuality >= 0
                && spec.MaxQuality <= 6
                && spec.MinQuality <= spec.MaxQuality,
            "invalid apparel policy"
        );
        var sorted = spec.Definitions.OrderBy(d => d, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            DomainException.Require(Identity.IsValidText(sorted[i]) && !(i > 0 && sorted[i] == sorted[i - 1]), "invalid apparel definition");
        }
        Spec = spec with { Definitions = EquatableArray.From(sorted) };
    }

    public ApparelPolicySpec Spec { get; }

    public PawnId Pawn => Spec.Pawn;
}

public sealed partial record DomainAction
{
    public static DomainAction NewApparelPolicyAction(ActionId id, ApparelPolicy policy) => Create(id, ActionKind.ApparelPolicy, policy);

    public ApparelPolicy? ApparelPolicy => PayloadOf<ApparelPolicy>(ActionKind.ApparelPolicy);
}
