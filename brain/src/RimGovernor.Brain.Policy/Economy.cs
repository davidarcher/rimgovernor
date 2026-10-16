namespace RimGovernor.Brain.Policy;

/// <summary>
/// The native research census the economy family reads (#3263): the current project, every project the census
/// lists with its facts and lock reasons, the finished projects and the Anomaly knowledge slots.
/// </summary>
public sealed record ResearchCensus
{
    public string Current { get; init; } = "";

    public IReadOnlyDictionary<string, ResearchProjectFacts> Projects { get; init; } = new Dictionary<string, ResearchProjectFacts>(StringComparer.Ordinal);

    public EquatableArray<string> Finished { get; init; }

    /// <summary>Every knowledge category's slot; empty when Anomaly is inactive.</summary>
    public EquatableArray<KnowledgeSlot> Knowledge { get; init; }
}

/// <summary>
/// What the economy family reads (#3263): the research the colony's recorded evidence waits on (the workshop
/// ladder's rung, planners waiting on research, the fishing and furniture requests, deep drilling), and the
/// native research census, unknown when it was not read.
/// </summary>
public sealed record EconomyFacts
{
    /// <summary>The projects owed, in order; the first unfinished one the census lists is the target.</summary>
    public EquatableArray<string> ResearchNeeds { get; init; }

    public Fact<ResearchCensus> Research { get; init; }
}

/// <summary>What the research Need's Method is now: a project to select, a hold, and the bench work the runtime does beside it.</summary>
/// <param name="Select">The project to select; empty when none is.</param>
/// <param name="Hold">Why nothing is selected; null when a project is, or when the Need has nothing to say.</param>
/// <param name="Bench">The project waits on the simple research bench, or one is current: the runtime raises the laboratory's bench.</param>
/// <param name="BuildAhead">The runtime builds the high-tech bench and then its analyzer ahead of need.</param>
public sealed record ResearchStep(string Select, NeedHold? Hold, bool Bench, bool BuildAhead);

/// <summary>
/// The economy family (#3263), research only so far: one colony-wide Need, <c>economy/research</c>
/// (Development), met while no recorded research need is unfinished. It replaced the EnsureResearch Concern, its
/// inspection and its planner. Its pure Method selects the next project (or an empty knowledge slot's); the
/// laboratory benches are the family's building work, run by the runtime under the same Need and admitted by
/// claims, so selecting a project and raising a bench go side by side.
/// </summary>
public sealed class EconomyFamily : IFamily<EconomyFacts>
{
    public const string FamilyId = "economy";

    public static readonly NeedId Research = NeedId.Of(FamilyId, "research");

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Research, FactFamily.Colony, FactFamily.Rooms];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(EconomyFacts facts) => [ResearchNeed(facts)];

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, EconomyFacts facts)
    {
        if (!ready.Any(n => n.Id == Research))
        {
            return [];
        }
        var step = PolicyFunctions.ResearchStepOf(facts);
        if (step.Select.Length != 0)
        {
            var project = step.Select;
            return [new NeedMethod(Research, new MethodId("research-" + project), "", id =>
                new PlanSpec(id, new PlanRevision(1), [DomainAction.NewResearchSelectAction(new ActionId(id.Value + "-0"), new ResearchSelect(project))]))];
        }
        return step.Hold is { } hold ? [NeedMethod.Held(Research, hold.Subject, hold.Detail)] : [];
    }

    /// <summary>
    /// The research Need, a plain predicate with no latch: met with no unfinished need; unmet while the target is
    /// unfinished and nothing current recovers it (a derived target stays owed while its project is current, and a
    /// current project missing its bench is owed), or while an empty knowledge slot has a project to fund; unknown
    /// when the census was not read. A need the census does not list is no target. Its deficit is 1 while owed.
    /// </summary>
    public static Need ResearchNeed(EconomyFacts f)
    {
        var needs = f.ResearchNeeds;
        var census = f.Research.TryGetValue(out var read) ? Known(PolicyFunctions.ResearchSummary(read!)) : Unknown<ResearchFacts>();
        var (target, derived) = PolicyFunctions.ResearchConcern(needs, census);
        var (recovered, deficit) = PolicyFunctions.ResearchTargetNeed(target, derived, census);
        var pick = census.TryGetValue(out var summary) ? summary!.KnowledgePick.Value : "";
        if (pick.Length != 0 && needs.Count != 0)
        {
            // An empty Anomaly knowledge slot with a project to fund is a spending need of its own.
            recovered = Known(false);
            deficit = Known(Math.Max(deficit.TryGetValue(out var d) ? d : 0.0, 1.0));
        }
        var satisfaction = !recovered.TryGetValue(out var met) ? Satisfaction.Unknown : met ? Satisfaction.Met : Satisfaction.Unmet;
        return new Need
        {
            Id = Research,
            Tier = NeedTier.Development,
            Satisfaction = satisfaction,
            Deficit = satisfaction == Satisfaction.Unknown ? Unknown<double>() : deficit,
            Evidence =
            [
                new("research_needs", Known(string.Join("+", needs))),
                new("research_target", target.Length != 0 ? Known(target) : Unknown<string>()),
                new("current_project", census.TryGetValue(out var c) ? Known(c!.Current.Value) : Unknown<string>()),
                new("knowledge_pick", census.IsKnown ? Known(pick) : Unknown<string>()),
            ],
        };
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The review-shaped summary of a research census: current project and its bench lock, listed and finished projects, and the knowledge pick.</summary>
    public static ResearchFacts ResearchSummary(ResearchCensus read)
    {
        var projects = read.Projects.Keys.Select(name => new ResearchProjectId(name)).ToList();
        projects.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return new ResearchFacts
        {
            Current = new ResearchProjectId(read.Current),
            CurrentBenchMissing = read.Current.Length != 0 && ResearchBenchNeeded(read.Projects.GetValueOrDefault(read.Current) ?? new ResearchProjectFacts()),
            Projects = EquatableArray.From(projects),
            Finished = [.. read.Finished.Select(name => new ResearchProjectId(name))],
            KnowledgePick = new ResearchProjectId(KnowledgePick(read.Projects, read.Finished, read.Knowledge)),
        };
    }

    /// <summary>
    /// What the research Need does now, in the old planner's order: an empty knowledge slot's project first (a
    /// slot already funding, or an unreadable slot or project, holds); else, with a project current, wait on it
    /// while the benches are raised; else the next project in prerequisite order, held for its bench when only the
    /// bench locks it. An unread census holds as unavailable.
    /// </summary>
    public static ResearchStep ResearchStepOf(EconomyFacts f)
    {
        if (!f.Research.TryGetValue(out var read))
        {
            return new ResearchStep("", new NeedHold("field_unavailable", "research"), false, false);
        }
        var (knowledge, knowledgeHold) = ResearchKnowledgeNext(f.ResearchNeeds, read!);
        if (knowledgeHold is not null)
        {
            return new ResearchStep("", knowledgeHold, false, false);
        }
        if (knowledge.Length != 0)
        {
            return new ResearchStep(knowledge, null, false, false);
        }
        if (read!.Current.Length != 0)
        {
            return new ResearchStep("", new NeedHold("current_research_project", read.Current), true, true);
        }
        var (next, hold) = ResearchNext(f.ResearchNeeds, read);
        if (hold is not null)
        {
            return new ResearchStep("", hold, false, false);
        }
        if (next.Length == 0)
        {
            return new ResearchStep("", null, false, false);
        }
        return ResearchBenchNeeded(read.Projects[next])
            ? new ResearchStep("", new NeedHold("research_bench", next), true, true)
            : new ResearchStep(next, null, false, true);
    }

    /// <summary>
    /// The knowledge project to select: the next project when it is a knowledge project with an empty slot of its
    /// category, else the cheapest startable project an empty slot can fund. A next project whose slot is already
    /// funding, whose slot was not read or which is not startable holds the Need.
    /// </summary>
    internal static (string Project, NeedHold? Hold) ResearchKnowledgeNext(IReadOnlyList<string> needs, ResearchCensus read)
    {
        var (head, hold) = ResearchNext(needs, read);
        if (hold is null && head.Length != 0)
        {
            var row = read.Projects[head];
            if (row.KnowledgeCategory != "")
            {
                foreach (var slot in read.Knowledge)
                {
                    if (slot.Category != row.KnowledgeCategory)
                    {
                        continue;
                    }
                    if (!slot.Current.IsEmpty)
                    {
                        return ("", new NeedHold("knowledge_slot", slot.Category));
                    }
                    if (!row.Census || row.LockReasons.Count != 0)
                    {
                        return ("", new NeedHold("field_unavailable", "research_knowledge_project"));
                    }
                    return (head, null);
                }
                return ("", new NeedHold("field_unavailable", "research_knowledge_slot"));
            }
        }
        return (KnowledgePick(read.Projects, read.Finished, read.Knowledge), null);
    }

    /// <summary>
    /// The next project toward the target: the head of its prerequisite queue. No target is no project; a target
    /// the census does not list, or a prerequisite graph it cannot fully inspect, holds as unavailable.
    /// </summary>
    internal static (string Project, NeedHold? Hold) ResearchNext(IReadOnlyList<string> needs, ResearchCensus read)
    {
        var (target, _) = ResearchConcern(needs, Known(ResearchSummary(read)));
        if (target.Length == 0)
        {
            return ("", null);
        }
        if (!read.Projects.ContainsKey(target))
        {
            return ("", new NeedHold("field_unavailable", "research_project"));
        }
        var projects = read.Projects.ToDictionary(kv => new ResearchProjectId(kv.Key), kv => kv.Value);
        IReadOnlyList<ResearchProjectId> queue;
        try
        {
            queue = ResearchPrerequisiteQueue(projects, [.. read.Finished.Select(name => new ResearchProjectId(name))], [new ResearchProjectId(target)]);
        }
        catch (DomainException)
        {
            return ("", new NeedHold("field_unavailable", "research_prerequisites"));
        }
        return queue.Count == 0 ? ("", new NeedHold("field_unavailable", "research_prerequisites")) : (queue[0].Value, null);
    }
}
