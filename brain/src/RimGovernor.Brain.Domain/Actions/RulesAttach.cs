namespace RimGovernor.Brain.Domain;

// RuleTrigger, RulePredicate, RuleActionKind and RuleTarget are the closed words of a native rule; the bridge
// maps each to its wire enum.

public enum RuleTrigger
{
    PreyKilled,
}

public enum RulePredicate
{
    ActorUndrafted,
    ActorHuntingWorkActive,
    TargetAvailable,
}

public enum RuleActionKind
{
    GiveJob,
}

public enum RuleTarget
{
    NearestDesignatedPrey,
}

/// <summary>
/// "On Trigger, if every Predicate holds, do Action Job on Target within Radius cells of the actor." Go authors
/// it; native only executes it. <paramref name="PredatorMarginCells"/> is how far the hunt route must stay from a
/// wild predator; Go authors it, native has no default.
/// </summary>
public sealed record Rule(
    string Id,
    RuleTrigger Trigger,
    EquatableArray<RulePredicate> Predicates,
    RuleActionKind Action,
    string Job,
    RuleTarget Target,
    uint Radius,
    float PredatorMarginCells
);

/// <summary>
/// The declarative rules a RulesAttachIntent on Actions/Apply installs: it replaces every active native rule
/// and sets the lease. It is an intent kind, so the attachment has a receipt and sits in the session journal
/// before native is written. An empty rule list clears.
/// </summary>
public sealed record RulesAttach : IActionPayload
{
    /// <summary>Go <c>NewRulesAttach</c>: validates rules (unique ids) and a positive lease; throws <see cref="DomainException"/> when invalid.</summary>
    public RulesAttach(IEnumerable<Rule> rules, long leaseTicks)
    {
        DomainException.Require(leaseTicks > 0, "invalid rules attach");
        var rows = rules.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rows)
        {
            DomainException.Require(
                Identity.IsValidText(rule.Id)
                    && !seen.Contains(rule.Id)
                    && Wire.IsDefined(rule.Trigger)
                    && Wire.IsDefined(rule.Action)
                    && Identity.IsValidText(rule.Job)
                    && Wire.IsDefined(rule.Target)
                    && rule.Radius != 0
                    && rule.PredatorMarginCells > 0
                    && !float.IsInfinity(rule.PredatorMarginCells),
                "invalid rule"
            );
            foreach (var predicate in rule.Predicates)
            {
                DomainException.Require(Wire.IsDefined(predicate), "invalid rule predicate");
            }
            seen.Add(rule.Id);
        }
        Rules = EquatableArray.From(rows);
        LeaseTicks = leaseTicks;
    }

    /// <summary>The rules in request order.</summary>
    public EquatableArray<Rule> Rules { get; }

    /// <summary>The lease length in ticks from the apply tick.</summary>
    public long LeaseTicks { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewRulesAttachAction(ActionId id, RulesAttach attach) => Create(id, ActionKind.RulesAttach, attach);

    public RulesAttach? RulesAttach => PayloadOf<RulesAttach>(ActionKind.RulesAttach);
}
