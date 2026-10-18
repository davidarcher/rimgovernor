using TechLevel = RimGovernor.Contracts.Wire.Defs.TechLevel;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The review-time native research state the economy/research Need is measured against. Projects lists every
/// native project the census exposes so a configured target absent from the game stays unknown rather
/// than an unbounded deficit.
/// </summary>
public sealed record ResearchFacts
{
    public ResearchProjectId Current { get; init; }

    public EquatableArray<ResearchProjectId> Finished { get; init; }

    public EquatableArray<ResearchProjectId> Projects { get; init; }

    /// <summary>The current project's only native lock is the research bench nobody has built, so it does not progress and the goal stays in deficit for the bench.</summary>
    public bool CurrentBenchMissing { get; init; }

    /// <summary>The knowledge project an empty Anomaly knowledge slot should fund now (KnowledgePick), empty when none is owed.</summary>
    public ResearchProjectId KnowledgePick { get; init; }

    /// <summary>The tech level of each listed project: what a ResearchTier goal expands to.</summary>
    public IReadOnlyDictionary<ResearchProjectId, TechLevel> Levels { get; init; } = new Dictionary<ResearchProjectId, TechLevel>();

    /// <summary>The census's facts of each listed project by defName: its cost, prerequisites and progress (the colony model's research input).</summary>
    public IReadOnlyDictionary<string, ResearchProjectFacts> Details { get; init; } = new Dictionary<string, ResearchProjectFacts>(StringComparer.Ordinal);
}

public static partial class ResearchRules
{
    /// <summary>Counts observed available workers, preserving incomplete facts.</summary>
    public static Fact<int> RoundsWorkers(IReadOnlyList<WorkPawn> pawns)
    {
        var count = 0;
        foreach (var p in pawns)
        {
            var known = p.Available.TryGetValue(out var available);
            var appliesKnown = p.Applies.TryGetValue(out var applies);
            if ((known && !available) || (appliesKnown && !applies))
            {
                continue;
            }
            if (!known || !appliesKnown)
            {
                return Unknown<int>();
            }
            count++;
        }
        return Known(count);
    }

    /// <summary>
    /// The project the research Need pursues: the configured target when one is set, else the first derived
    /// need the native research census lists and has not finished. Unknown facts keep the first derived
    /// need so the deficit stays unknown rather than recovered; no need at all is no target.
    /// </summary>
    public static string ResearchConcernTarget(string configured, IReadOnlyList<string> derived, Fact<ResearchFacts> facts)
    {
        if (configured != "" || derived.Count == 0)
        {
            return configured;
        }
        if (!facts.TryGetValue(out var f))
        {
            return derived[0];
        }
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in f.Projects)
        {
            listed.Add(name.Value);
        }
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in f.Finished)
        {
            finished.Add(name.Value);
        }
        foreach (var name in derived)
        {
            if (listed.Contains(name) && !finished.Contains(name))
            {
                return name;
            }
        }
        return "";
    }

    /// <summary>
    /// Resolves the project the research Need pursues: the first unfinished project some need recorded (a bench
    /// the workshop ladder gates, a planner waiting on research, the fishing and furniture requests). With no
    /// need nothing is researched. Derived is true for any target found.
    /// </summary>
    public static (string Target, bool Derived) ResearchConcern(IReadOnlyList<string> needs, Fact<ResearchFacts> facts)
    {
        var target = ResearchConcernTarget("", needs, facts);
        return (target, target != "");
    }

    /// <summary>
    /// Names the first project of required that the native census has not finished: the research a goal's
    /// only method waits on. Unknown facts keep the first requirement; none unfinished is no gate.
    /// </summary>
    public static string ResearchGate(IReadOnlyList<string> required, Fact<ResearchFacts> facts)
    {
        if (required.Count == 0)
        {
            return "";
        }
        if (!facts.TryGetValue(out var f))
        {
            return required[0];
        }
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in f.Finished)
        {
            finished.Add(name.Value);
        }
        foreach (var name in required)
        {
            if (!finished.Contains(name))
            {
                return name;
            }
        }
        return "";
    }

    /// <summary>
    /// Measures the research Need: no target is certain recovery; a finished target is recovered; an idle
    /// research tab with the target unfinished is a full deficit. A current native project also recovers a
    /// configured target (player-chosen research is respected, never replaced), but not a derived one:
    /// another goal's ladder waits on that project, so the deficit stands, and the research planner asks the
    /// clock for ticks while any project is current, until it is finished. Missing facts stay unknown.
    /// </summary>
    public static (Fact<bool> Recovered, Fact<double> Deficit) ResearchTargetNeed(string target, bool derived, Fact<ResearchFacts> facts)
    {
        if (target == "")
        {
            return (Known(true), Known(0.0));
        }
        if (!facts.TryGetValue(out var f))
        {
            return (Unknown<bool>(), Unknown<double>());
        }
        var listed = false;
        foreach (var name in f.Projects)
        {
            listed = listed || name.Value == target;
        }
        if (!listed)
        {
            return (Unknown<bool>(), Unknown<double>());
        }
        foreach (var name in f.Finished)
        {
            if (name.Value == target)
            {
                return (Known(true), Known(0.0));
            }
        }
        if (f.Current.Value is { Length: > 0 } && !derived && !f.CurrentBenchMissing)
        {
            return (Known(true), Known(0.0));
        }
        return (Known(false), Known(1.0));
    }

    /// <summary>
    /// Measures the economy/resource Need against the generic resource census: the deficit is the worst-covered
    /// configured target's shortfall fraction, the same proportion SelectResourceTarget dispatches on. No
    /// configured target is certain recovery; unknown stock stays unknown.
    /// </summary>
    public static (Fact<bool> Recovered, Fact<double> Deficit) ResourceTargetNeed(IReadOnlyDictionary<Resource, long>? targets, Fact<EquatableArray<Amount>> stock)
    {
        if (targets is null || targets.Count == 0)
        {
            return (Known(true), Known(0.0));
        }
        if (!stock.TryGetValue(out var rows))
        {
            return (Unknown<bool>(), Unknown<double>());
        }
        Resource resource;
        long target;
        bool ok;
        try
        {
            (resource, target, ok) = ResourceRules.SelectResourceTarget(targets, stock);
        }
        catch (DomainException)
        {
            return (Unknown<bool>(), Unknown<double>());
        }
        if (!ok)
        {
            return (Known(true), Known(0.0));
        }
        long have = 0;
        foreach (var q in rows)
        {
            if (q.Resource == resource)
            {
                have = q.Count;
            }
        }
        return (Known(false), Known(Math.Min(1.0, Math.Max(0.0, (double)(target - have) / target))));
    }
}
