using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using PolicyMoodHistory = RimGovernor.Brain.Policy.MoodHistory;
using PolicyPawnId = RimGovernor.Brain.Policy.PawnId;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Store;

/// <summary>
/// Optional persisted values distinguish unknown from observed zero/false: a <see cref="Fact{T}"/> persists as a
/// nullable value. Domain Fact deliberately has no serialization contract (Go <c>moodValue</c>, <c>moodFact</c>,
/// <c>runwayPointer</c> and <c>runwayFact</c>).
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

/// <summary>One mood need's measured level in a persisted mood state (Go <c>RoundsMoodCause</c>).</summary>
public sealed record RoundsMoodCause(MoodNeed Need, double? Level);

/// <summary>A persisted pawn's mood evidence; every observation is unknown or a value (Go <c>RoundsMoodPawn</c>).</summary>
public sealed record RoundsMoodPawn
{
    public MentalState? Break { get; init; }

    public PolicyPawnId Id { get; init; }

    public double? Mood { get; init; }

    public double? Threshold { get; init; }

    public double? Target { get; init; }

    public double? Food { get; init; }

    public double? Rest { get; init; }

    public double? Joy { get; init; }

    public bool? Mental { get; init; }

    public bool? Dead { get; init; }

    public bool? Downed { get; init; }

    public bool? Drafted { get; init; }

    public bool? PlayerForced { get; init; }
}

/// <summary>One pawn's persisted mood state (Go <c>RoundsMoodState</c>).</summary>
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
                Break = PersistedFact.Reference(p.Break),
                Id = p.Id,
                Mood = PersistedFact.Value(p.Mood),
                Threshold = PersistedFact.Value(p.Threshold),
                Target = PersistedFact.Value(p.Target),
                Food = PersistedFact.Value(p.Food),
                Rest = PersistedFact.Value(p.Rest),
                Joy = PersistedFact.Value(p.Joy),
                Mental = PersistedFact.Value(p.Mental),
                Dead = PersistedFact.Value(p.Dead),
                Downed = PersistedFact.Value(p.Downed),
                Drafted = PersistedFact.Value(p.Drafted),
                PlayerForced = PersistedFact.Value(p.PlayerForced),
            },
            Active = s.Active,
            Missing = s.Missing,
            MentalRisk = s.MentalRisk,
            Causes = EquatableArray.From(s.Causes.Select(c => new RoundsMoodCause(c.Need, PersistedFact.Value(c.Level)))),
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
                Break = PersistedFact.OfReference(Pawn.Break),
                Id = Pawn.Id,
                Mood = PersistedFact.Of(Pawn.Mood),
                Threshold = PersistedFact.Of(Pawn.Threshold),
                Target = PersistedFact.Of(Pawn.Target),
                Food = PersistedFact.Of(Pawn.Food),
                Rest = PersistedFact.Of(Pawn.Rest),
                Joy = PersistedFact.Of(Pawn.Joy),
                Mental = PersistedFact.Of(Pawn.Mental),
                Dead = PersistedFact.Of(Pawn.Dead),
                Downed = PersistedFact.Of(Pawn.Downed),
                Drafted = PersistedFact.Of(Pawn.Drafted),
                PlayerForced = PersistedFact.Of(Pawn.PlayerForced),
            },
            Active = Active,
            Missing = Missing,
            MentalRisk = MentalRisk,
            Causes = [.. Causes.Select(c => new MoodCause { Need = c.Need, Level = PersistedFact.Of(c.Level) })],
            Provision = [.. Provision],
            Unowned = [.. Unowned],
        };
}

/// <summary>The persisted mood review (Go <c>RoundsMood</c>).</summary>
public sealed record RoundsMood
{
    public EquatableArray<RoundsMoodState> States { get; init; }
}

/// <summary>One pawn's mood method proposal as the review filed it (Go <c>RoundsMoodMethod</c>).</summary>
public sealed record RoundsMoodMethod
{
    public PolicyPawnId Pawn { get; init; }

    public MoodNeed Need { get; init; }

    public MoodMethodReason Reason { get; init; }

    public ConcernId Concern { get; init; }

    public string Thought { get; init; } = "";

    public double Target { get; init; }

    public double? NeedBenefit { get; init; }

    public double? MoodBenefit { get; init; }
}

public sealed partial class Store
{
    /// <summary>The persisted mood review of a mood history, or null when it holds no state (Go <c>moodRecord</c>).</summary>
    internal static RoundsMood? MoodRecord(PolicyMoodHistory h) =>
        h.States.Count == 0 ? null : new RoundsMood { States = EquatableArray.From(h.States.Select(RoundsMoodState.From)) };

    /// <summary>One proposal per mood state, selected without any used method (Go <c>moodProposals</c>).</summary>
    internal static List<RoundsMoodMethod> MoodProposals(PolicyMoodHistory h)
    {
        var result = new List<RoundsMoodMethod>();
        foreach (var state in h.States)
        {
            var p = PolicyFunctions.SelectMoodMethod(state, []);
            result.Add(
                new RoundsMoodMethod
                {
                    Pawn = p.Pawn,
                    Need = p.Need,
                    Reason = p.Reason,
                    Concern = p.Concern,
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
