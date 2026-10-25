using TechLevel = RimGovernor.Contracts.Wire.Defs.TechLevel;

namespace RimGovernor.Brain.Policy;

// The research Need (economy family): pure, native-shape-preserving primitives (prerequisite queue and the
// bench lock).

/// <summary>Names a native ResearchProjectDef by defName.</summary>
public readonly record struct ResearchProjectId(string Value) : IComparable<ResearchProjectId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    public bool Equals(ResearchProjectId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(ResearchProjectId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(ResearchProjectId left, ResearchProjectId right) => left.CompareTo(right) < 0;

    public static bool operator >(ResearchProjectId left, ResearchProjectId right) => left.CompareTo(right) > 0;

    public static bool operator <=(ResearchProjectId left, ResearchProjectId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ResearchProjectId left, ResearchProjectId right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// Static project definition values used by planning. Native progress, locks and visibility belong to the research snapshot.
/// </summary>
public sealed record ResearchProjectDefinition
{
    public string KnowledgeCategory { get; init; } = "";

    public EquatableArray<ResearchProjectId> Prerequisites { get; init; }

    public EquatableArray<ResearchProjectId> HiddenPrerequisites { get; init; }

    /// <summary>
    /// The defs finishing the project unlocks (ResearchProjectDef.UnlockedDefs, from the definition catalog), by defName, sorted:
    /// the products of the recipes it gates, the things and terrain that need it, the plants whose sowing needs it, the surgeries
    /// and the psychic rituals it gates.
    /// </summary>
    public EquatableArray<string> Unlocks { get; init; }

    /// <summary>The project's apparent cost (knowledge points for a knowledge-category project), from the definition catalog.</summary>
    public double Cost { get; init; }

    /// <summary>The project's tech level (ResearchProjectDef.techLevel), from the definition catalog.</summary>
    public TechLevel TechLevel { get; init; }

}

/// <summary>A project's native row, or its absence from a filtered snapshot whose finished set remains total.</summary>
public abstract record ResearchProjectObservation
{
    private ResearchProjectObservation() { }
    public sealed record Listed(RimGovernor.Contracts.Wire.Observations.ResearchProject Project) : ResearchProjectObservation;
    public sealed record NotListed(RimGovernor.Contracts.Wire.Observations.ResearchSnapshot Snapshot, string Project) : ResearchProjectObservation;
}

/// <summary>Catalog planning values joined to canonical native evidence without copying its fields.</summary>
public sealed record ResearchProjectFacts
{
    public required ResearchProjectDefinition Definition { get; init; }
    public required ResearchProjectObservation Observation { get; init; }
    public string KnowledgeCategory => Definition.KnowledgeCategory;
    public EquatableArray<ResearchProjectId> Prerequisites => Definition.Prerequisites;
    public EquatableArray<ResearchProjectId> HiddenPrerequisites => Definition.HiddenPrerequisites;
    public EquatableArray<string> Unlocks => Definition.Unlocks;
    public double Cost => Definition.Cost;
    public TechLevel TechLevel => Definition.TechLevel;
    public bool Census => Observation switch
    {
        ResearchProjectObservation.Listed => true,
        ResearchProjectObservation.NotListed => false,
        var invalid => throw new ArgumentOutOfRangeException(nameof(Observation), invalid, "Invalid research observation."),
    };
    public EquatableArray<string> LockReasons => Observation switch
    {
        ResearchProjectObservation.Listed(var row) => row.LockReasons,
        ResearchProjectObservation.NotListed => [],
        var invalid => throw new ArgumentOutOfRangeException(nameof(Observation), invalid, "Invalid research observation."),
    };
    public bool Hidden => LockReasons.Contains(ResearchRules.ResearchLockHidden);
    public double Progress => Observation switch
    {
        ResearchProjectObservation.Listed(var row) => row.ProgressFraction,
        ResearchProjectObservation.NotListed(var snapshot, var project) => snapshot.Finished.Contains(project) ? 1 : 0,
        var invalid => throw new ArgumentOutOfRangeException(nameof(Observation), invalid, "Invalid research observation."),
    };
}

public static partial class ResearchRules
{
    /// <summary>Bounds the returned prerequisite queue -- the native research UI cannot usefully preview more.</summary>
    public const int ResearchQueueMax = 8;

    /// <summary>Bounds prerequisite-graph inspection, to reject pathological/cyclic native data instead of hanging on it.</summary>
    internal const int ResearchVisitMax = 128;

    /// <summary>
    /// The native lock reason for a project whose required research bench (or a facility on it) the colony
    /// does not have; native SelectResearch refuses the project while it holds.
    /// </summary>
    public const string ResearchLockBench = "research_building_or_facilities";

    /// <summary>The native lock reason for a project the entity codex still hides (an Anomaly project before its entity is discovered).</summary>
    public const string ResearchLockHidden = "hidden";

    /// <summary>
    /// Whether the bench lock is the only thing keeping the project from starting: its prerequisites are done
    /// and no other native requirement holds, so building the bench is what unlocks the selection. A project
    /// locked for any other reason is not a bench need; the queue owes it a prerequisite first.
    /// </summary>
    public static bool ResearchBenchNeeded(ResearchProjectFacts project)
    {
        var needed = false;
        foreach (var reason in project.LockReasons)
        {
            if (reason != ResearchLockBench)
            {
                return false;
            }
            needed = true;
        }
        return needed;
    }

    /// <summary>
    /// Topologically orders the native prerequisites of targets, rejecting incomplete or cyclic project
    /// graphs (throws <see cref="DomainException"/>) rather than guessing at a native project it cannot fully
    /// inspect. finished projects are treated as already satisfied. The result is capped at ResearchQueueMax
    /// entries, matching the native research queue's own practical bound.
    /// </summary>
    public static IReadOnlyList<ResearchProjectId> ResearchPrerequisiteQueue(
        IReadOnlyDictionary<ResearchProjectId, ResearchProjectFacts> projects,
        IReadOnlyList<ResearchProjectId> finished,
        IReadOnlyList<ResearchProjectId> targets)
    {
        var done = new HashSet<ResearchProjectId>(finished);
        var visiting = new HashSet<ResearchProjectId>();
        var result = new List<ResearchProjectId>();
        var visits = 0;

        void Visit(ResearchProjectId name)
        {
            if (done.Contains(name))
            {
                return;
            }
            visits++;
            if (visits > ResearchVisitMax)
            {
                throw new DomainException("research prerequisite graph exceeds the inspection bound");
            }
            if (visiting.Contains(name))
            {
                throw new DomainException($"cyclic research prerequisites: {name}");
            }
            if (!projects.TryGetValue(name, out var row))
            {
                throw new DomainException($"unavailable ordinary research prerequisite: {name}");
            }
            if (row.Hidden)
            {
                throw new DomainException($"unavailable ordinary research prerequisite: {name}");
            }
            var dependencies = new HashSet<ResearchProjectId>(row.Prerequisites);
            dependencies.UnionWith(row.HiddenPrerequisites);
            var ordered = dependencies.OrderBy(d => d).ToList();
            visiting.Add(name);
            foreach (var dependency in ordered)
            {
                Visit(dependency);
            }
            visiting.Remove(name);
            done.Add(name);
            result.Add(name);
        }

        foreach (var target in targets)
        {
            Visit(target);
        }
        if (result.Count > ResearchQueueMax)
        {
            result.RemoveRange(ResearchQueueMax, result.Count - ResearchQueueMax);
        }
        return result;
    }
}
