using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using PolicyMoodHistory = RimGovernor.Brain.Policy.MoodHistory;

namespace RimGovernor.Brain.Store;

/// <summary>One mood need's measured level in a persisted mood state.</summary>
public sealed record RoundsMoodCause(MoodNeed Need, double Level);

/// <summary>One pawn's persisted mood state.</summary>
public sealed record RoundsMoodState
{
    public required MoodPawn Pawn { get; init; }

    public bool Active { get; init; }

    public bool Missing { get; init; }

    public bool MentalRisk { get; init; }

    public EquatableArray<RoundsMoodCause> Causes { get; init; }

    public EquatableArray<MoodProvision> Provision { get; init; }

    public EquatableArray<MoodThought> Unowned { get; init; }

    /// <summary>The persisted form of a policy mood state.</summary>
    internal static RoundsMoodState From(MoodState s)
    {
        var p = s.Pawn;
        return new RoundsMoodState
        {
            Pawn = p,
            Active = s.Active,
            Missing = s.Missing,
            MentalRisk = s.MentalRisk,
            Causes = EquatableArray.From(s.Causes.Select(c => new RoundsMoodCause(c.Need, c.Level))),
            Provision = EquatableArray.From(s.Provision),
            Unowned = EquatableArray.From(s.Unowned),
        };
    }

    /// <summary>The policy mood state this record persists.</summary>
    internal MoodState ToPolicy() =>
        new()
        {
            Pawn = Pawn,
            Active = Active,
            Missing = Missing,
            MentalRisk = MentalRisk,
            Causes = [.. Causes.Select(c => new MoodCause { Need = c.Need, Level = c.Level })],
            Provision = [.. Provision],
            Unowned = [.. Unowned],
        };
}

/// <summary>The persisted mood review.</summary>
public sealed record RoundsMood
{
    public EquatableArray<RoundsMoodState> States { get; init; }
}

/// <summary>One pawn's mood method proposal as the review filed it.</summary>
public sealed record RoundsMoodMethod
{
    public PawnId Pawn { get; init; }

    public MoodNeed Need { get; init; }

    public MoodMethodReason Reason { get; init; }

    /// <summary>The filing key of the Need a provisioned proposal defers to; empty otherwise.</summary>
    public ConcernId Concern { get; init; }

    public string Thought { get; init; } = "";

    public double Target { get; init; }

    /// <summary>The need level a relief proposal restores toward one half; zero for any other reason.</summary>
    public double NeedBenefit { get; init; }
}

public sealed partial class Store
{
    /// <summary>The persisted mood review of a mood history, or null when it holds no state.</summary>
    internal static RoundsMood? MoodRecord(PolicyMoodHistory h) =>
        h.States.Count == 0 ? null : new RoundsMood { States = EquatableArray.From(h.States.Select(RoundsMoodState.From)) };

    /// <summary>One proposal per mood state, selected without any used method.</summary>
    internal static List<RoundsMoodMethod> MoodProposals(PolicyMoodHistory h)
    {
        var result = new List<RoundsMoodMethod>();
        foreach (var state in h.States)
        {
            var p = MoodRules.SelectMoodMethod(state, []);
            result.Add(
                new RoundsMoodMethod
                {
                    Pawn = p.Pawn,
                    Need = p.Need,
                    Reason = p.Reason,
                    Concern = p.Provider.IsValid ? p.Provider.FilingKey : default,
                    Thought = p.Thought,
                    Target = p.Target,
                    NeedBenefit = p.NeedBenefit,
                }
            );
        }
        return result;
    }
}
