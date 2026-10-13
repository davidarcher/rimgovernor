namespace RimGovernor.Brain.Policy;

// EnsureResearch opening slice: pure, native-shape-preserving primitives (prerequisite queue, eligible
// researchers, usable laboratories). No domain/store/executor/bridge/buildingruntime wiring exists yet for
// this goal; these functions only make the deterministic method-selection logic available ahead of that wiring.

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
/// Mirrors the native research snapshot fields prerequisite_queue inspects for one project. Hidden projects
/// and anything with unknown prerequisite lists can never be treated as a selectable prerequisite. A
/// knowledge-category project (Anomaly) is selectable like any other: it fills its category's knowledge slot
/// instead of the ordinary one and progresses from study knowledge, not research work.
/// </summary>
public sealed record ResearchProjectFacts
{
    public ResearchProjectId Name { get; init; }

    public Fact<bool> Hidden { get; init; }

    public string KnowledgeCategory { get; init; } = "";

    public Fact<EquatableArray<ResearchProjectId>> Prerequisites { get; init; }

    public Fact<EquatableArray<ResearchProjectId>> HiddenPrerequisites { get; init; }

    /// <summary>
    /// The native requiredResearchBuilding defName, empty when the project names none.
    /// </summary>
    public string RequiredBuilding { get; init; } = "";

    /// <summary>
    /// The native census's CanStartNow predicates the project fails (prerequisite:name, techprints,
    /// ResearchLockBench, ...), empty when it can start now.
    /// </summary>
    public EquatableArray<string> LockReasons { get; init; }

    /// <summary>
    /// Says the native census listed the project with its lock reasons: without it an empty LockReasons is
    /// unread, not startable.
    /// </summary>
    public bool Census { get; init; }

    /// <summary>The project's apparent cost (knowledge points for a knowledge-category project), from the definition catalog.</summary>
    public double Cost { get; init; }
}

/// <summary>
/// Mirrors the current or next project's native laboratory requirements
/// (requiredResearchBuilding/requiredResearchFacilities). RequiredBuilding Unknown means the native project
/// imposes no specific bench requirement; RequiredFacilities Unknown means the requirement itself has not
/// been observed and no bench can be proven usable yet.
/// </summary>
public sealed record ResearchLabRequirement
{
    public Fact<string> RequiredBuilding { get; init; }

    public Fact<EquatableArray<string>> RequiredFacilities { get; init; }
}

/// <summary>
/// Mirrors one native colonist's research-eligibility facts: incapacitation state, whether native work
/// assignment applies to them at all, and their current Research work-type priority/disabled state.
/// </summary>
public sealed record ResearchPawn
{
    public PawnId Pawn { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Drafted { get; init; }

    public Fact<bool> MentalState { get; init; }

    public Fact<bool> Applies { get; init; }

    public Fact<EquatableArray<WorkPriority>> Work { get; init; }
}

public static partial class PolicyFunctions
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
            if (!row.Hidden.TryGetValue(out var hidden) || hidden)
            {
                throw new DomainException($"unavailable ordinary research prerequisite: {name}");
            }
            var preKnown = row.Prerequisites.TryGetValue(out var prerequisites);
            var hiddenPreKnown = row.HiddenPrerequisites.TryGetValue(out var hiddenPrerequisites);
            if (!preKnown || !hiddenPreKnown)
            {
                throw new DomainException($"incomplete native prerequisites: {name}");
            }
            var dependencies = new HashSet<ResearchProjectId>(prerequisites);
            dependencies.UnionWith(hiddenPrerequisites);
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
