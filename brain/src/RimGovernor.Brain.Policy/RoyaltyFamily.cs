namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the royalty family reads (#3261): the royalty read the permit ranking runs on, and whether a psylink
/// neuroformer use is owed (PsylinkOwed over the candidates, the level-ups and the held neuroformer items).
/// </summary>
public sealed record RoyaltyFamilyFacts
{
    public Fact<RoyaltyFacts> Royalty { get; init; }

    public Fact<bool> PsylinkOwed { get; init; }
}

/// <summary>
/// The royalty family (#3261). It replaced the MaintainPermits and MaintainPsylink Concerns, their inspections and
/// planners:
/// <list type="bullet">
/// <item><c>royalty/permits</c> (Development): no worthwhile permit is takeable now (PermitsSpent). Its Method,
/// <c>permit-&lt;holder&gt;-&lt;permit&gt;</c>, takes the best one as a choose_permit pawn setting; the holder's settings
/// are its claim.</item>
/// <item><c>royalty/psylink</c> (Development): no willing colonist waits on a held neuroformer (PsylinkOwed).
/// Neuroformer use is not yet a supported forecast outcome, so an unmet Need is held as
/// <see cref="UnsupportedNeuroformer"/>. Acquiring the neuroformer stays the economy/resource Need's (NeuroformerNeeds).</item>
/// </list>
/// </summary>
public sealed class RoyaltyFamily(bool permits = true, bool psylink = true) : IFamily<RoyaltyFamilyFacts>
{
    public const string FamilyId = "royalty";

    public static readonly NeedId Permits = NeedId.Of(FamilyId, "permits");

    public static readonly NeedId Psylink = NeedId.Of(FamilyId, "psylink");

    /// <summary>The stable refusal subject of an unmet psylink Need.</summary>
    public const string UnsupportedNeuroformer = "unsupported_forecast_neuroformer";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns, FactFamily.Colony, FactFamily.Definitions, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(RoyaltyFamilyFacts facts)
    {
        var output = new List<Need>(2);
        if (permits)
        {
            output.Add(RoyaltyFamilyRules.PermitsNeedOf(facts.Royalty));
        }
        if (psylink)
        {
            output.Add(new Need
            {
                Id = Psylink,
                Tier = NeedTier.Development,
                Satisfaction = !facts.PsylinkOwed.TryGetValue(out var owed) ? Satisfaction.Unknown : owed ? Satisfaction.Unmet : Satisfaction.Met,
                Deficit = facts.PsylinkOwed.TryGetValue(out var d) ? Known(d ? 1.0 : 0.0) : Unknown<double>(),
            });
        }
        return output;
    }

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, RoyaltyFamilyFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Permits && PermitChoiceRules.NextPermitOf(facts.Royalty) is { } choice)
            {
                output.Add(PermitMethod(choice.Intent()));
            }
            else if (need.Id == Psylink)
            {
                output.Add(NeedMethod.Held(Psylink, UnsupportedNeuroformer, "Neuroformer use requires shared forecast participant, timing and need effects."));
            }
        }
        return output;
    }

    /// <summary>The permit's Method: one per holder and permit, so the same choice continues its open Method.</summary>
    public static NeedMethod PermitMethod(PermitIntent intent) =>
        new(Permits, new MethodId($"permit-{intent.Holder}-{intent.Permit}"), $"permits: {intent.Holder} takes {intent.Permit} with {intent.Faction}", id =>
            new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPawnSettingsAction(new ActionId($"{id.Value}-0"), intent.Setting())]));
}

public static partial class RoyaltyFamilyRules
{
    /// <summary>The permits Need: met once no worthwhile permit is takeable now (PermitsSpent), unmet while one is, unknown without the royalty read.</summary>
    public static Need PermitsNeedOf(Fact<RoyaltyFacts> royalty)
    {
        var spent = PermitChoiceRules.PermitsSpent(royalty);
        var known = spent.TryGetValue(out var done);
        return new Need
        {
            Id = RoyaltyFamily.Permits,
            Tier = NeedTier.Development,
            Satisfaction = !known ? Satisfaction.Unknown : done ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = known ? Known(done ? 0.0 : 1.0) : Unknown<double>(),
        };
    }
}
