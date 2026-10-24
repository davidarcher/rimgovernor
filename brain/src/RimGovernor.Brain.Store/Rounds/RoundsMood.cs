using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using NeedLevel = RimGovernor.Contracts.Wire.Observations.NeedLevel;
using PolicyMoodHistory = RimGovernor.Brain.Policy.MoodHistory;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Store;

/// <summary>
/// Optional persisted values distinguish unknown from observed zero/false: a <see cref="Fact{T}"/> persists as a
/// nullable value. Domain Fact deliberately has no serialization contract.
/// </summary>
internal static class PersistedFact
{
    /// <summary>The known value of a struct fact, or null while it is unknown.</summary>
    public static T? Value<T>(Fact<T> fact)
        where T : struct => fact.TryGetValue(out var value) ? value : null;

    /// <summary>The known value of a reference fact, or null while it is unknown.</summary>
    public static T? Reference<T>(Fact<T> fact)
        where T : class => fact.TryGetValue(out var value) ? value : null;

    /// <summary>The fact of a stored struct value: unknown when the value is absent.</summary>
    public static Fact<T> Of<T>(T? value)
        where T : struct => value is { } known ? Known(known) : Unknown<T>();

    /// <summary>The fact of a stored reference value: unknown when the value is absent.</summary>
    public static Fact<T> OfReference<T>(T? value)
        where T : class => value is null ? Unknown<T>() : Known(value);
}

/// <summary>One mood need's measured level in a persisted mood state.</summary>
public sealed record RoundsMoodCause(MoodNeed Need, double Level);

/// <summary>A persisted pawn's mood evidence, read whole from its pawn row (#3768); only the forecast's target can be unknown (null).</summary>
public sealed record RoundsMoodPawn
{
    public MentalState? Break { get; init; }

    public PawnId Id { get; init; }

    public NeedLevel Mood { get; init; } = new NeedLevel.Lacks();

    public double Threshold { get; init; }

    public double? Target { get; init; }

    public NeedLevel Food { get; init; } = new NeedLevel.Lacks();

    public NeedLevel Rest { get; init; } = new NeedLevel.Lacks();

    public NeedLevel Joy { get; init; } = new NeedLevel.Lacks();

    public bool Mental { get; init; }

    public bool Dead { get; init; }

    public bool Downed { get; init; }

    public bool Drafted { get; init; }

    public bool PlayerForced { get; init; }
}

/// <summary>One pawn's persisted mood state.</summary>
public sealed record RoundsMoodState
{
    public RoundsMoodPawn Pawn { get; init; } = new();

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
            Pawn = new RoundsMoodPawn
            {
                Break = p.Break,
                Id = p.Id,
                Mood = p.Mood,
                Threshold = p.Threshold,
                Target = PersistedFact.Value(p.Target),
                Food = p.Food,
                Rest = p.Rest,
                Joy = p.Joy,
                Mental = p.Mental,
                Dead = p.Dead,
                Downed = p.Downed,
                Drafted = p.Drafted,
                PlayerForced = p.PlayerForced,
            },
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
            Pawn = new MoodPawn
            {
                Break = Pawn.Break,
                Id = Pawn.Id,
                Mood = Pawn.Mood,
                Threshold = Pawn.Threshold,
                Target = PersistedFact.Of(Pawn.Target),
                Food = Pawn.Food,
                Rest = Pawn.Rest,
                Joy = Pawn.Joy,
                Mental = Pawn.Mental,
                Dead = Pawn.Dead,
                Downed = Pawn.Downed,
                Drafted = Pawn.Drafted,
                PlayerForced = Pawn.PlayerForced,
            },
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

    public double? NeedBenefit { get; init; }

    public double? MoodBenefit { get; init; }
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
                    NeedBenefit = PersistedFact.Value(p.NeedBenefit),
                    MoodBenefit = PersistedFact.Value(p.MoodBenefit),
                }
            );
        }
        return result;
    }
}
