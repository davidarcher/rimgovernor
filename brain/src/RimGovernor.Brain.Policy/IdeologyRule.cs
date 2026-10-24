namespace RimGovernor.Brain.Policy;

// The shared precept rule. One pure function answers: given the ideoligion, does a precept in force allow,
// approve, penalise or forbid an action, for one pawn or for the colony, and what mood does it cost? Callers
// name the action by the game's HistoryEventDef (the event the action raises, which precept effects react
// to), so no def name is listed here and a modded precept answers like a vanilla one. The rule never guesses a
// stance it cannot read.

/// <summary>The ideoligion's answer to an action.</summary>
public enum PreceptStance
{
    /// <summary>Holds the action: the ideoligion fact is unread (a caller still holding a Fact; #3927).</summary>
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

/// <summary>The action asked about: the HistoryEventDef it raises.</summary>
public readonly record struct PreceptAction(string HistoryEvent);

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
/// unconditional.
/// </summary>
public sealed record PreceptVerdict
{
    public PreceptStance Stance { get; init; }

    public double DoerMoodCost { get; init; }

    public double WitnessMoodCost { get; init; }

    public double RefusalChance { get; init; }
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
            return new PreceptVerdict { Stance = PreceptStance.Allowed };
        }
        return IdeologyRuleRules.ActionStance(Ideology, action, subject);
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
        if (string.IsNullOrEmpty(action.HistoryEvent))
        {
            throw new ArgumentException("a precept action names the history event it raises", nameof(action));
        }
        double doerMoodCost = 0, witnessMoodCost = 0, refusalChance = 0;
        var approved = false;
        foreach (var e in EffectsInForce())
        {
            if (e.HistoryEvent != action.HistoryEvent || (e.OnlyForNonSlaves && subject.Slave))
            {
                continue;
            }
            if (e.Forbids())
            {
                if (subject.Pawn && IdeologyRuleRules.Cancels(e, subject))
                {
                    continue;
                }
                refusalChance = Math.Max(refusalChance, Math.Min(e.Chance, 1));
            }
            else if (e.Penalises())
            {
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
        return new Ideoligion(Defs, new IdeoligionFacts { IdeoId = Facts.IdeoId, Precepts = held, Development = Facts.Development });
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
            PreceptStance.Unknown => 4,
            PreceptStance other => throw new InvalidOperationException($"precept stance {other} is not named"),
        };
        var worst = PreceptStance.Approved;
        var best = PreceptStance.Unknown;
        foreach (var e in events)
        {
            var s = ActionStance(new PreceptAction(e), new PreceptSubject()).Stance;
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

public static class IdeologyRuleRules
{
    /// <summary>The Ideology reading of the facts.</summary>
    public static IdeologyRead IdeologyRead(this RoundsFacts f) => new(f.Ideology, f.IdeologyInstalled);

    /// <summary>Evaluates the action under the ideoligion fact; the verdict is Unknown when the ideoligion is unread.</summary>
    public static PreceptVerdict ActionStance(Fact<Ideoligion> ideology, PreceptAction action, PreceptSubject subject)
    {
        if (!ideology.TryGetValue(out var i))
        {
            return new PreceptVerdict { Stance = PreceptStance.Unknown };
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
