using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the ideology family reads (#3260): the owed role assignments, the rituals that can begin now, and the
/// reform the colony's ideoligion owes. The reform's intent is what a Method commits: the pending record's intent
/// when it still matches the colony and the chosen design, otherwise a fresh one; null while nothing is owed. Without
/// Ideology the family derives no Needs, and an unread ideoligion, catalog or census fails the read with
/// <see cref="RequiredFactException"/>.
/// </summary>
public sealed record IdeologyFacts
{
    /// <summary>Whether the Ideology expansion is active (<see cref="IdeologyFamilyRules.Installed"/>).</summary>
    public bool Installed { get; init; }

    /// <summary>The role assignments owed now; the reader fills it when the roles Need is derived.</summary>
    public EquatableArray<RoleAssignment> Roles { get; init; }

    /// <summary>The rituals that can begin now; the reader fills it when the rituals Need is derived.</summary>
    public EquatableArray<RitualPlan> Rituals { get; init; }

    /// <summary>The design the policy chooses (<c>ChooseIdeoligionReform</c>); the reader fills it when the reform Need is derived.</summary>
    public DesignChoice? Reform { get; init; }

    /// <summary>The pending reform record was observed complete: the ideoligion now holds its design.</summary>
    public bool ReformObserved { get; init; }

    public IdeoligionReform? Intent { get; init; }

    /// <summary>The conversion the player's believer goals owe (<see cref="ConversionRules.Owed"/>); the reader fills it when the conversion Need is derived.</summary>
    public ConversionOwed Conversion { get; init; } = ConversionOwed.None;

    /// <summary>The ideoligion's development state: what a reform waiting for points reports. The reader fills it with the reform.</summary>
    public RimGovernor.Contracts.Wire.Observations.IdeoDevelopment? Development { get; init; }
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

    /// <summary>The hold of an owed reform that has no intent to commit.</summary>
    public const string NoReformIntent = "no_reform_intent";

    /// <summary>The stable hold subject of a goal's reform waiting for points; the counts follow as <c>:&lt;points&gt;/&lt;cost&gt;</c>.</summary>
    public const string DevelopmentPoints = "development_points";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns, FactFamily.Colony, FactFamily.Definitions, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(IdeologyFacts facts)
    {
        if (!facts.Installed)
        {
            return [];
        }
        var needs = new List<Need>(3);
        if (roles)
        {
            needs.Add(Owed(Roles, facts.Roles.Count));
        }
        if (rituals)
        {
            needs.Add(Owed(Rituals, facts.Rituals.Count));
        }
        if (reform)
        {
            needs.Add(IdeologyFamilyRules.ReformNeedOf(facts));
        }
        if (convert && facts.Conversion.IsOwed)
        {
            needs.Add(new Need
            {
                Id = ConversionRules.NeedOf(facts.Conversion.Target),
                Tier = NeedTier.Development,
                Satisfaction = Satisfaction.Unmet,
                Deficit = 1.0,
                Preconditions = roles ? [Roles] : [],
            });
        }
        return needs;
    }

    public IReadOnlyList<Need> DeriveSeriousHarm(IdeologyFacts facts) => [];

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, IdeologyFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Roles)
            {
                output.AddRange(facts.Roles.Select(RoleMethod));
            }
            else if (need.Id == Rituals)
            {
                output.Add(NeedMethod.Held(Rituals, UnsupportedRitual, "Ritual participants, duration and outcome require shared forecast admission."));
            }
            else if (need.Id == Reform)
            {
                if (facts.Reform is { Comparison.Decision: DesignDecision.Awaiting })
                {
                    var fluid = facts.Development as RimGovernor.Contracts.Wire.Observations.IdeoDevelopment.Fluid ?? throw new RequiredFactException("fluid ideoligion development");
                    output.Add(NeedMethod.Held(Reform, FormattableString.Invariant($"{DevelopmentPoints}:{fluid.Points}/{fluid.NextReformPoints}"), "The next reform waits for development points."));
                    continue;
                }
                output.Add(facts.Intent is { } intent && facts.Reform is { Comparison: { } comparison }
                    ? new NeedMethod(Reform, ReformMethodId(intent), ReformReason(comparison), id =>
                        new PlanSpec(id, new PlanRevision(1), [DomainAction.NewIdeoligionReformAction(new ActionId($"{id.Value}-0"), intent)]))
                    : NeedMethod.Held(Reform, NoReformIntent, "No reform intent was built."));
            }
            else if (need.Id.Family == FamilyId && need.Id.Kind == ConvertKind && facts.Conversion.IsOwed && need.Id == ConversionRules.NeedOf(facts.Conversion.Target))
            {
                output.Add(ConversionMethod(need.Id, facts.Conversion));
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
        ConversionBlock.Unassigned => NeedMethod.Held(need, "moral_guide_unassigned", "No pawn holds the moral guide's role."),
        ConversionBlock block => throw new InvalidOperationException($"conversion block {block} is not named"),
    };

    private static NeedMethod RoleMethod(RoleAssignment role)
    {
        var assign = new Assign(role.Pawn, role.Role, PreviousAssignment.ClearPrevious());
        return new NeedMethod(Roles, new MethodId($"ideorole-{role.Pawn}-{role.Role}"), $"ideology: {role.Pawn} takes role {role.Def} ({role.Role})", id =>
            new PlanSpec(id, new PlanRevision(1), [DomainAction.NewAssignAction(new ActionId($"{id.Value}-0"), assign)]));
    }

    private static string ReformReason(DesignComparison c) =>
        FormattableString.Invariant($"ideoligion: restrictions {c.Current.Restrictions}→{c.Candidate.Restrictions}, mood cost {c.Current.MoodCost:G}→{c.Candidate.MoodCost:G}, obligations {c.Current.Obligations}→{c.Candidate.Obligations}; work relief {(c.WorkRelief ? "true" : "false")}, observed mood relief {c.MoodRelief:G}, new mood cost {c.NewMoodCost:G}");

    private static Need Owed(NeedId id, int count) => new()
    {
        Id = id,
        Tier = NeedTier.Development,
        Satisfaction = count > 0 ? Satisfaction.Unmet : Satisfaction.Met,
        Deficit = (double)count,
    };
}

public static class IdeologyFamilyRules
{
    /// <summary>Whether the Ideology expansion is active.</summary>
    public static bool Installed(RoundsFacts f) => f.Ideology is IdeologyMode.Installed;

    /// <summary>
    /// The reform Need: met once the pending reform is observed, or while the choice is no reform (no memes, or not an
    /// improvement); unmet otherwise. An unread chosen design raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static Need ReformNeedOf(IdeologyFacts f)
    {
        var choice = f.Reform ?? throw new RequiredFactException("ideoligion reform design");
        var owed = !f.ReformObserved && choice.Design.Memes.Count > 0 && choice.Comparison?.Decision is DesignDecision.Improves or DesignDecision.Awaiting;
        return FoodRules.WholeNeed(IdeologyFamily.Reform, NeedTier.Development, !owed);
    }
}
