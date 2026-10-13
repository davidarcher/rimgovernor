namespace RimGovernor.Brain.Policy;

/// <summary>
/// The declarative native rules a plan needs right now, in the order native receives them. Go authors it; the
/// transport carries it without knowing what any rule is for. An empty set clears every active rule.
/// </summary>
public sealed record RuleSet
{
    public EquatableArray<Rule> Rules { get; init; }
}

/// <summary>
/// A completed native attachment's dispatch evidence. Dispatch is journaled before native apply, so its tick
/// is a conservative lease anchor. Native starts the full lease at apply; no queued intent supplies this value.
/// </summary>
public sealed record RuleLease
{
    public Tick Dispatched { get; init; }

    public long Ticks { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>How long native keeps an attached rule set without a renewal (an hour): Go re-attaches the set every Round, and a lapse deactivates every rule natively (docs/developers/contracts/native-rules.md).</summary>
    public const long RuleLeaseTicks = 2500;

    /// <summary>The cells around the hunter the chain rule searches for its next prey; native's maximum is 100.</summary>
    internal const int HuntChainRadius = 80;

    /// <summary>The id of the hunt-chain rule.</summary>
    public const string HuntChainRuleId = "hunt-chain";

    /// <summary>
    /// Derives the rule set from the hunt plan: with a hunter who can take Hunting and designated hunt prey
    /// standing, a hunter who kills moves on to the nearest designated prey instead of hauling its own kill;
    /// otherwise the set is empty. Known is false while either census is unread, so a Round with no evidence
    /// changes nothing.
    /// </summary>
    public static (RuleSet Set, bool Known) HuntChainRules(
        Fact<EquatableArray<AcquisitionSource>> sources, Fact<EquatableArray<PawnProfile>> hunters, IReadOnlyList<HuntHold> holds)
    {
        if (!sources.TryGetValue(out var rows) || !hunters.TryGetValue(out var profiles))
        {
            return (new RuleSet(), false);
        }
        var (_, found) = HunterFor(profiles);
        if (!found)
        {
            return (new RuleSet(), true);
        }
        var designated = 0;
        foreach (var row in rows)
        {
            if (row.Hunt && row.Designated)
            {
                designated++;
            }
        }
        // Admission holds govern new designations, not orders already standing. Native rechecks the next
        // target's route and work-giver legality at firing.
        foreach (var hold in holds)
        {
            if (hold.Source.Hunt && hold.Source.Designated)
            {
                designated++;
            }
        }
        // Keep the rule while the final target stands; native fires nothing when the killed prey leaves no next target.
        if (designated == 0)
        {
            return (new RuleSet(), true);
        }
        var rule = new Rule(
            HuntChainRuleId,
            RuleTrigger.PreyKilled,
            EquatableArray.From(new[] { RulePredicate.ActorUndrafted, RulePredicate.ActorHuntingWorkActive, RulePredicate.TargetAvailable }),
            RuleActionKind.GiveJob,
            "Hunt",
            RuleTarget.NearestDesignatedPrey,
            HuntChainRadius,
            HuntPredatorMarginCells);
        return (new RuleSet { Rules = EquatableArray.From(new[] { rule }) }, true);
    }

    /// <summary>
    /// Stops native at the renewal boundary without relying on wall-clock polling. Once that boundary passes,
    /// an unknown or failed renewal must allow game time to advance to native expiry instead of parking forever.
    /// </summary>
    public static uint RulesWindowTicks(Tick tick, uint maxTicks, Fact<RuleLease> attached)
    {
        if (!attached.TryGetValue(out var lease) || lease.Ticks <= 1 || lease.Dispatched.Value < 0 || tick < lease.Dispatched)
        {
            return maxTicks;
        }
        var elapsed = tick.Value - lease.Dispatched.Value;
        var remaining = (lease.Ticks / 2) - elapsed;
        if (remaining <= 0)
        {
            return maxTicks;
        }
        if (remaining < maxTicks)
        {
            return (uint)remaining;
        }
        return maxTicks;
    }
}
