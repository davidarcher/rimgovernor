namespace RimGovernor.Brain.Domain;

/// <summary>A meme and precept selection for an ideoligion. Use <see cref="CanonicalIdeoligionDesign"/> to sort and validate it.</summary>
public sealed record IdeoligionDesign(EquatableArray<string> Memes, EquatableArray<string> Precepts, bool Fluid)
{
    /// <summary>Go <c>CanonicalIdeoligionDesign</c>: names sorted ordinally, validated, duplicate free.</summary>
    public static IdeoligionDesign CanonicalIdeoligionDesign(IdeoligionDesign v)
    {
        DomainException.Require(v.Memes.Count != 0 && v.Memes.Count <= 5 && v.Precepts.Count <= 256, "invalid ideoligion selection count");
        var memes = SortedNames(v.Memes);
        var precepts = SortedNames(v.Precepts);
        return v with { Memes = memes, Precepts = precepts };
    }

    private static EquatableArray<string> SortedNames(EquatableArray<string> names)
    {
        var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            DomainException.Require(Identity.IsValidText(sorted[i]) && !(i > 0 && sorted[i] == sorted[i - 1]), "invalid or duplicate ideoligion selection");
        }
        return EquatableArray.From(sorted);
    }
}

/// <summary>Reforms a fluid ideoligion from an expected design to a different one.</summary>
public sealed record IdeoligionReform : IActionPayload
{
    /// <summary>Go <c>NewIdeoligionReform</c>; throws <see cref="DomainException"/> when invalid. Designs are stored canonical.</summary>
    public IdeoligionReform(string ideo, IdeoligionDesign expected, IdeoligionDesign design, int count)
    {
        DomainException.Require(
            Identity.IsValidText(ideo) && count >= 0 && count < 2147483647 && expected.Fluid && design.Fluid,
            "invalid fluid ideoligion reform"
        );
        var canonicalExpected = IdeoligionDesign.CanonicalIdeoligionDesign(expected);
        var canonicalDesign = IdeoligionDesign.CanonicalIdeoligionDesign(design);
        DomainException.Require(canonicalExpected != canonicalDesign, "reform must change the design");
        IdeoId = ideo;
        Expected = canonicalExpected;
        Design = canonicalDesign;
        Count = count;
    }

    public string IdeoId { get; }

    public int Count { get; }

    public IdeoligionDesign Expected { get; }

    public IdeoligionDesign Design { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewIdeoligionReformAction(ActionId id, IdeoligionReform reform) => Create(id, ActionKind.IdeoligionReform, reform);

    public IdeoligionReform? IdeoligionReform => PayloadOf<IdeoligionReform>(ActionKind.IdeoligionReform);
}
