namespace RimGovernor.Brain.Policy;

// The shared precept rule. One pure function answers: given the ideoligion, does a precept in force allow,
// approve, penalise or forbid an action, for one pawn or for the colony, and what mood does it cost? Callers
// name the action by the game's HistoryEventDef (the event the action raises, which precept effects react
// to), so no def name is listed here and a modded precept answers like a vanilla one. Unknown stays
// unknown: the rule never guesses a stance it cannot read.

/// <summary>The ideoligion's answer to an action.</summary>
public enum PreceptStance
{
    /// <summary>Holds the action: the ideoligion is unread, or an effect bears on the action in a way the contract does not type.</summary>
    Unknown,

    /// <summary>No precept in force reacts to the action.</summary>
    Allowed,

    /// <summary>Precepts only give mood for the action.</summary>
    Approved,

    /// <summary>The action costs mood, or members may refuse it (RefusalChance below 1).</summary>
    Penalised,

    /// <summary>Members refuse the action outright.</summary>
    Forbidden,
}

/// <summary>The action asked about: the HistoryEventDef it raises, or Apparel for what a pawn wears (apparel precepts carry no history event).</summary>
public readonly record struct PreceptAction(string HistoryEvent, bool Apparel);

/// <summary>
/// Who acts. The default is the colony: no pawn's traits are known, so no refusal is cancelled. For a pawn,
/// its traits and hediffs cancel an unwilling effect that lists them, and a slave skips effects that apply
/// only to non-slaves.
/// </summary>
public sealed record PreceptSubject
{
    public bool Pawn { get; init; }

    public bool Slave { get; init; }

    public IReadOnlyList<string> Traits { get; init; } = [];

    public IReadOnlyList<string> Hediffs { get; init; } = [];
}

/// <summary>
/// The rule's answer. DoerMoodCost sums, over the matching precept effects the doer takes
/// (self-took-action), the worst stage's mood loss; WitnessMoodCost the same for the other penalising
/// effects. Both are non-negative. RefusalChance is the largest chance members refuse, 1 when a refusal is
/// unconditional. Effects are the matching effects, in precept order.
/// </summary>
public sealed record PreceptVerdict
{
    public PreceptStance Stance { get; init; }

    public double DoerMoodCost { get; init; }

    public double WitnessMoodCost { get; init; }

    public double RefusalChance { get; init; }

    public IReadOnlyList<EffectInForce> Effects { get; init; } = [];

    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// What the frame says about Ideology: the primary ideoligion fact, and whether the expansion is installed.
/// An unread ideoligion with the expansion installed (or unknown) is unread; with the expansion absent
/// there are no precepts, so nothing is forbidden.
/// </summary>
public readonly record struct IdeologyRead(Fact<Ideoligion> Ideology, Fact<bool> Installed)
{
    /// <summary>Reports that the Ideology expansion is known not to be installed.</summary>
    public bool Absent() => Installed.TryGetValue(out var installed) && !installed;

    /// <summary>The stance under the read: allowed without the expansion, else the ideoligion's (unknown when unread).</summary>
    public PreceptVerdict ActionStance(PreceptAction action, PreceptSubject subject)
    {
        if (Absent())
        {
            return new PreceptVerdict { Stance = PreceptStance.Allowed, Reason = "Ideology not installed" };
        }
        return PolicyFunctions.ActionStance(Ideology, action, subject);
    }
}

public static class PreceptStanceExtensions
{
    /// <summary>Reports a stance that costs mood or is refused.</summary>
    public static bool CostsMood(this PreceptStance s) => s is PreceptStance.Penalised or PreceptStance.Forbidden;
}

public sealed partial record Ideoligion
{
    /// <summary>Evaluates action by subject under the ideoligion.</summary>
    public PreceptVerdict ActionStance(PreceptAction action, PreceptSubject subject)
    {
        if (!action.Apparel && string.IsNullOrEmpty(action.HistoryEvent))
        {
            return new PreceptVerdict { Stance = PreceptStance.Unknown, Reason = "no action named" };
        }
        var effects = new List<EffectInForce>();
        double doerMoodCost = 0, witnessMoodCost = 0, refusalChance = 0;
        var approved = false;
        foreach (var inForce in EffectsInForce())
        {
            var e = inForce.Effect;
            if (action.Apparel)
            {
                if (e.Kind == PreceptEffectKind.Apparel)
                {
                    // The apparel effect carries no requirement on the wire.
                    return new PreceptVerdict { Stance = PreceptStance.Unknown, Effects = [inForce], Reason = "apparel effect of " + inForce.Precept.Name + " is not typed" };
                }
                continue;
            }
            if (e.HistoryEvent != action.HistoryEvent || e.Kind == PreceptEffectKind.Apparel || (e.OnlyForNonSlaves && subject.Slave))
            {
                continue;
            }
            if (e.Forbids())
            {
                if (subject.Pawn && PolicyFunctions.Cancels(e, subject))
                {
                    continue;
                }
                effects.Add(inForce);
                var chance = 1.0;
                if (e.Chance.TryGetValue(out var c))
                {
                    chance = Math.Min(c, 1);
                }
                refusalChance = Math.Max(refusalChance, chance);
            }
            else if (e.Penalises())
            {
                effects.Add(inForce);
                var cost = -e.StageMoods.Min();
                if (e.Kind == PreceptEffectKind.SelfTookAction)
                {
                    doerMoodCost += cost;
                }
                else
                {
                    witnessMoodCost += cost;
                }
            }
            else if (e.Approves())
            {
                effects.Add(inForce);
                approved = true;
            }
        }
        var stance = PreceptStance.Allowed;
        if (refusalChance >= 1)
        {
            stance = PreceptStance.Forbidden;
        }
        else if (refusalChance > 0 || doerMoodCost > 0 || witnessMoodCost > 0)
        {
            stance = PreceptStance.Penalised;
        }
        else if (approved)
        {
            stance = PreceptStance.Approved;
        }
        return new PreceptVerdict
        {
            Stance = stance,
            DoerMoodCost = doerMoodCost,
            WitnessMoodCost = witnessMoodCost,
            RefusalChance = refusalChance,
            Effects = effects,
        };
    }

    /// <summary>
    /// The ideoligion's defs with exactly the named precept defs in force: the ideoligion of a pawn whose
    /// row lists its own precepts, which may differ from the colony's primary one.
    /// </summary>
    public Ideoligion HeldBy(IReadOnlyList<string> precepts)
    {
        var held = new List<HeldPrecept>(precepts.Count);
        for (var n = 0; n < precepts.Count; n++)
        {
            held.Add(new HeldPrecept(n.ToString(System.Globalization.CultureInfo.InvariantCulture), precepts[n]));
        }
        return new Ideoligion(Defs, new IdeoligionFacts { IdeoId = Facts.IdeoId, Precepts = held });
    }

    /// <summary>The worst and the best stance over the events, in the order approved, allowed, penalised, forbidden, unknown.</summary>
    internal (PreceptStance Worst, PreceptStance Best) EventStances(IReadOnlyList<string> events)
    {
        static int Rank(PreceptStance s) => s switch
        {
            PreceptStance.Approved => 0,
            PreceptStance.Allowed => 1,
            PreceptStance.Penalised => 2,
            PreceptStance.Forbidden => 3,
            _ => 4,
        };
        var worst = PreceptStance.Approved;
        var best = PreceptStance.Unknown;
        foreach (var e in events)
        {
            var s = ActionStance(new PreceptAction(e, false), new PreceptSubject()).Stance;
            if (Rank(s) > Rank(worst))
            {
                worst = s;
            }
            if (Rank(s) < Rank(best))
            {
                best = s;
            }
        }
        return (worst, best);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The Ideology reading of the facts.</summary>
    public static IdeologyRead IdeologyRead(this RoundsFacts f) => new(f.Ideology, f.IdeologyInstalled);

    /// <summary>Evaluates the action under the ideoligion fact; the verdict is Unknown when the ideoligion is unread.</summary>
    public static PreceptVerdict ActionStance(Fact<Ideoligion> ideology, PreceptAction action, PreceptSubject subject)
    {
        if (!ideology.TryGetValue(out var i))
        {
            return new PreceptVerdict { Stance = PreceptStance.Unknown, Reason = "ideoligion unknown" };
        }
        return i.ActionStance(action, subject);
    }

    internal static bool Cancels(PreceptEffect e, PreceptSubject s)
    {
        foreach (var t in e.NullifyingTraits)
        {
            if (s.Traits.Contains(t))
            {
                return true;
            }
        }
        foreach (var h in e.NullifyingHediffs)
        {
            if (s.Hediffs.Contains(h))
            {
                return true;
            }
        }
        return false;
    }
}
