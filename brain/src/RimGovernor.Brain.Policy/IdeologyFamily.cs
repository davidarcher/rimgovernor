using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the ideology family reads (#3260): the owed role assignments, the rituals that can begin now, and the
/// reform the colony's ideoligion owes. The reform's intent is what a Method commits: the pending record's intent
/// when it still matches the colony and the chosen design, otherwise a fresh one; null while nothing is owed or the
/// ideoligion or catalog is unread.
/// </summary>
public sealed record IdeologyFacts
{
    public Fact<EquatableArray<RoleAssignment>> Roles { get; init; }

    public Fact<EquatableArray<RitualPlan>> Rituals { get; init; }

    /// <summary>The design the policy chooses (<c>ChooseIdeoligionReform</c>); unknown while any input is unread.</summary>
    public Fact<DesignChoice> Reform { get; init; }

    /// <summary>The pending reform record was observed complete: the ideoligion now holds its design.</summary>
    public bool ReformObserved { get; init; }

    public IdeoligionReform? Intent { get; init; }

    /// <summary>The conversion the player's believer goals owe (<see cref="ConversionRules.Owed"/>); unknown while the ideoligion, a pawn row or the goals are unread.</summary>
    public Fact<ConversionOwed> Conversion { get; init; }

    /// <summary>The ideoligion's development state: what a reform waiting for points reports.</summary>
    public Fact<IdeoDevelopment> Development { get; init; }
}

/// <summary>
/// The ideology family (#3260). It replaced the MaintainIdeoRoles, MaintainRituals and ImproveIdeoligion Concerns,
/// their inspections and planners:
/// <list type="bullet">
/// <item><c>ideology/roles</c> (Development): no active role has a free place and a fitting believer. One Method
/// per owed role, <c>ideorole-&lt;pawn&gt;-&lt;role&gt;</c>, assigns it; the role is its claim.</item>
/// <item><c>ideology/rituals</c> (Development): no due ritual can begin now. Rituals are not yet a supported
/// forecast outcome, so an unmet Need is held as <c>unsupported_forecast_ritual</c>.</item>
/// <item><c>ideology/reform</c> (Development): the ideoligion holds the design the policy chooses. Its Method,
/// <c>ideoligion-&lt;reform count&gt;</c>, reforms toward it; the intent is kept in the <c>family/ideology/reform</c>
/// record until the reform is observed or the ideoligion moves on.</item>
/// <item><c>ideology/convert/&lt;pawn&gt;</c> (Development, one at a time): a Believers goal lacks believers and this
/// colonist, the lowest-certainty one of another ideoligion, is next. It waits on the roles Need; its Method,
/// <c>conversion-&lt;pawn&gt;</c>, orders the moral guide's Convert ability on the colonist, and is held while the
/// ideoligion has no such role or the ability is charging.</item>
/// </list>
/// </summary>
public sealed class IdeologyFamily(bool roles = true, bool rituals = true, bool reform = true, bool convert = true) : IFamily<IdeologyFacts>
{
    public const string FamilyId = "ideology";

    public static readonly NeedId Roles = NeedId.Of(FamilyId, "roles");

    public static readonly NeedId Rituals = NeedId.Of(FamilyId, "rituals");

    public static readonly NeedId Reform = NeedId.Of(FamilyId, "reform");

    /// <summary>The stable refusal subject of an unmet rituals Need.</summary>
    public const string UnsupportedRitual = "unsupported_forecast_ritual";

    /// <summary>The hold of an owed reform whose ideoligion or catalog is unread.</summary>
    public const string ReformUnread = "ideoligion";

    /// <summary>The stable hold subject of a goal's reform waiting for points; the counts follow as <c>:&lt;points&gt;/&lt;cost&gt;</c>.</summary>
    public const string DevelopmentPoints = "development_points";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns, FactFamily.Colony, FactFamily.Definitions, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(IdeologyFacts facts)
    {
        var needs = new List<Need>(3);
        if (roles)
        {
            needs.Add(Owed(Roles, "owed_roles", facts.Roles.TryGetValue(out var owed) ? Known(owed.Count) : Unknown<int>()));
        }
        if (rituals)
        {
            needs.Add(Owed(Rituals, "ritual_plans", facts.Rituals.TryGetValue(out var plans) ? Known(plans.Count) : Unknown<int>()));
        }
        if (reform)
        {
            needs.Add(IdeologyFamilyRules.ReformNeedOf(facts));
        }
        if (convert && facts.Conversion.TryGetValue(out var conversion) && conversion.IsOwed)
        {
            needs.Add(new Need
            {
                Id = ConversionRules.NeedOf(conversion.Target),
                Tier = NeedTier.Development,
                Satisfaction = Satisfaction.Unmet,
                Deficit = Known(1.0),
                Preconditions = roles ? [Roles] : [],
            });
        }
        return needs;
    }

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, IdeologyFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Roles && facts.Roles.TryGetValue(out var owed))
            {
                output.AddRange(owed.Select(RoleMethod));
            }
            else if (need.Id == Rituals)
            {
                output.Add(NeedMethod.Held(Rituals, UnsupportedRitual, "Ritual participants, duration and outcome require shared forecast admission."));
            }
            else if (need.Id == Reform)
            {
                if (facts.Reform.TryGetValue(out var awaited) && awaited.Comparison?.Decision == DesignDecision.Awaiting && facts.Development.TryGetValue(out var development))
                {
                    output.Add(NeedMethod.Held(Reform, FormattableString.Invariant($"{DevelopmentPoints}:{development.DevelopmentPoints}/{development.NextReformPoints}"), "The next reform waits for development points."));
                    continue;
                }
                output.Add(facts.Intent is { } intent && facts.Reform.TryGetValue(out var choice) && choice.Comparison is { } comparison
                    ? new NeedMethod(Reform, ReformMethodId(intent), ReformReason(comparison), id =>
                        new PlanSpec(id, new PlanRevision(1), [DomainAction.NewIdeoligionReformAction(new ActionId($"{id.Value}-0"), intent)]))
                    : NeedMethod.Held(Reform, ReformUnread, "The ideoligion or its catalog is unread."));
            }
            else if (need.Id.Family == FamilyId && need.Id.Kind == ConvertKind && facts.Conversion.TryGetValue(out var conversion) && conversion.IsOwed && need.Id == ConversionRules.NeedOf(conversion.Target))
            {
                output.Add(ConversionMethod(need.Id, conversion));
            }
        }
        return output;
    }

    /// <summary>The reform's MethodId: one per reform count, so the same reform continues its open Method.</summary>
    public static MethodId ReformMethodId(IdeoligionReform intent) => new(string.Create(CultureInfo.InvariantCulture, $"ideoligion-{intent.Count}"));

    /// <summary>The kind segment of a conversion Need: <c>ideology/convert/&lt;pawn&gt;</c>.</summary>
    public const string ConvertKind = "convert";

    /// <summary>The hold of a conversion whose ideoligion has no role that grants the Convert ability.</summary>
    public const string NoGuideRole = "no_moral_guide_role";

    /// <summary>The hold of a conversion whose guide's Convert ability is on cooldown or already ordered.</summary>
    public const string ConversionCharging = "convert_ability_charging";

    private static NeedMethod ConversionMethod(NeedId need, ConversionOwed owed) => owed.Block switch
    {
        ConversionBlock.None => new NeedMethod(need, new MethodId("conversion-" + owed.Target.Value), $"ideology: {owed.Guide} converts {owed.Target}", id =>
            new PlanSpec(id, new PlanRevision(1), [DomainAction.NewConversionAction(new ActionId($"{id.Value}-0"), new Conversion(owed.Guide, owed.Target))])),
        ConversionBlock.NoGuideRole => NeedMethod.Held(need, NoGuideRole, "The ideoligion has no role that grants the Convert ability."),
        ConversionBlock.Charging => NeedMethod.Held(need, ConversionCharging, "The moral guide's Convert ability is not charged."),
        _ => NeedMethod.Held(need, "moral_guide_unassigned", "No pawn holds the moral guide's role."),
    };

    private static NeedMethod RoleMethod(RoleAssignment role)
    {
        var assign = new Assign(role.Pawn, role.Role, PreviousAssignment.ClearPrevious());
        return new NeedMethod(Roles, new MethodId($"ideorole-{role.Pawn}-{role.Role}"), $"ideology: {role.Pawn} takes role {role.Def} ({role.Role})", id =>
            new PlanSpec(id, new PlanRevision(1), [DomainAction.NewAssignAction(new ActionId($"{id.Value}-0"), assign)]));
    }

    private static string ReformReason(DesignComparison c) =>
        FormattableString.Invariant($"ideoligion: restrictions {c.Current.Restrictions}→{c.Candidate.Restrictions}, mood cost {c.Current.MoodCost:G}→{c.Candidate.MoodCost:G}, obligations {c.Current.Obligations}→{c.Candidate.Obligations}; work relief {(c.WorkRelief ? "true" : "false")}, observed mood relief {c.MoodRelief:G}, new mood cost {c.NewMoodCost:G}");

    private static Need Owed(NeedId id, string fact, Fact<int> count) => new()
    {
        Id = id,
        Tier = NeedTier.Development,
        Satisfaction = !count.TryGetValue(out var n) ? Satisfaction.Unknown : n > 0 ? Satisfaction.Unmet : Satisfaction.Met,
        Deficit = count.TryGetValue(out var d) ? Known((double)d) : Unknown<double>(),
    };
}

public static class IdeologyFamilyRules
{
    /// <summary>
    /// The reform Need: unknown while the chosen design is; met once the pending reform is observed, or while the
    /// choice is no reform (no memes, or not an improvement); unmet otherwise.
    /// </summary>
    public static Need ReformNeedOf(IdeologyFacts f)
    {
        var known = f.Reform.TryGetValue(out var choice);
        var owed = known && !f.ReformObserved && choice!.Design.Memes.Count > 0 && choice.Comparison?.Decision is DesignDecision.Improves or DesignDecision.Awaiting;
        var satisfaction = !known ? Satisfaction.Unknown : owed ? Satisfaction.Unmet : Satisfaction.Met;
        return new Need
        {
            Id = IdeologyFamily.Reform,
            Tier = NeedTier.Development,
            Satisfaction = satisfaction,
            Deficit = known ? Known(owed ? 1.0 : 0.0) : Unknown<double>(),
        };
    }
}
