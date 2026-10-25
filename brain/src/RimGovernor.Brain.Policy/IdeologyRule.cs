namespace RimGovernor.Brain.Policy;

// The shared precept rule. One pure function answers: given the ideoligion, does a precept in force allow,
// approve, penalise or forbid an action, for one pawn or for the colony, and what mood does it cost? Callers
// name the action by the game's HistoryEventDef (the event the action raises, which precept effects react
// to), so no def name is listed here and a modded precept answers like a vanilla one. The rule never guesses a
// stance it cannot read.

/// <summary>The ideoligion's answer to an action.</summary>
public enum PreceptStance
{
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
/// What the frame says about Ideology: the expansion is not installed, so there are no precepts and nothing is
/// forbidden, or it is installed and the player faction's primary ideoligion comes with the catalog's defs.
/// </summary>
public abstract record IdeologyMode
{
    private IdeologyMode()
    {
    }

    /// <summary>The Ideology expansion is not installed.</summary>
    public sealed record NotInstalled : IdeologyMode;

    /// <summary>The expansion is installed; Primary is the player faction's primary ideoligion.</summary>
    public sealed record Installed(Ideoligion Primary) : IdeologyMode;

    /// <summary>The stance under the mode: allowed without the expansion, else the primary ideoligion's.</summary>
    public PreceptVerdict ActionStance(PreceptAction action, PreceptSubject subject) =>
        this is Installed installed ? installed.Primary.ActionStance(action, subject) : new PreceptVerdict { Stance = PreceptStance.Allowed };
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

    private IReadOnlyList<string>? SelectedPrecepts { get; init; }

    /// <summary>
    /// The ideoligion's defs with exactly the named precept defs in force: the ideoligion of a pawn whose
    /// row lists its own precepts, which may differ from the colony's primary one.
    /// </summary>
    public Ideoligion HeldBy(IReadOnlyList<string> precepts) => this with { SelectedPrecepts = precepts };

    /// <summary>The worst and the best stance over the events, in the order approved, allowed, penalised, forbidden, unknown.</summary>
    internal (PreceptStance Worst, PreceptStance Best) EventStances(IReadOnlyList<string> events)
    {
        static int Rank(PreceptStance s) => s switch
        {
            PreceptStance.Approved => 0,
            PreceptStance.Allowed => 1,
            PreceptStance.Penalised => 2,
            PreceptStance.Forbidden => 3,
            PreceptStance other => throw new InvalidOperationException($"precept stance {other} is not named"),
        };
        var worst = PreceptStance.Approved;
        var best = PreceptStance.Forbidden;
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
