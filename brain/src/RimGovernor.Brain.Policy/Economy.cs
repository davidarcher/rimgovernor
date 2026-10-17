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

    /// <summary>The resource Need's facts; with no target (the default) the Need is met.</summary>
    public ResourceFacts Resource { get; init; } = new();

    /// <summary>The review's trade facts the trade and caravan Needs read; unknown before the first review.</summary>
    public TradeReview Trade { get; init; } = new();

    /// <summary>The trade-mission Need's facts; unknown (the default) when the record was not read.</summary>
    public TradeMissionFacts Missions { get; init; } = new();
}

/// <summary>
/// What the economy family's resource Need reads (#3303): the review's derived stock targets (every source's
/// floor: construction, bills, clothing, fuel, animal feed, runways, evidence needs) and whether the colony's own
/// works demand any stock, the usable stock census with the wood fact, the review's runway forecasts, and the
/// research project the recorded research needs name first.
/// </summary>
public sealed record ResourceFacts
{
    public IReadOnlyDictionary<Resource, long> Targets { get; init; } = new Dictionary<Resource, long>();

    /// <summary>Whether the colony's own works (construction, open bills, clothing, fuel, feed) demand some stock: stock a chop or a mine provides.</summary>
    public bool Works { get; init; }

    public Fact<EquatableArray<Amount>> Stock { get; init; }

    /// <summary>The wood fact WoodLog is counted from; unknown counts WoodLog from the census.</summary>
    public Fact<long> Wood { get; init; }

    public EquatableArray<ResourceRunway> Runways { get; init; }

    /// <summary>The unfinished project the recorded research needs name first (the workshop ladder's rung among them); empty when none gates.</summary>
    public string ResearchGate { get; init; } = "";

    /// <summary>The resource facts of a review: its facts and the demand it derived from them.</summary>
    public static ResourceFacts From(RoundsFacts f, DerivedDemand demand) => new()
    {
        Targets = demand.Needs,
        Works = demand.Works.Count > 0,
        Stock = f.Resources,
        Wood = f.Wood,
        Runways = f.ResourceRunways,
        ResearchGate = ResearchRules.ResearchConcernTarget("", f.ResearchNeeds, f.Research),
    };
}

/// <summary>What the research Need's Method is now: a project to select, a hold, and the bench work the runtime does beside it.</summary>
/// <param name="Select">The project to select; empty when none is.</param>
/// <param name="Hold">Why nothing is selected; null when a project is, or when the Need has nothing to say.</param>
/// <param name="Bench">The project waits on the simple research bench, or one is current: the runtime raises the laboratory's bench.</param>
/// <param name="BuildAhead">The runtime builds the high-tech bench and then its analyzer ahead of need.</param>
public sealed record ResearchStep(string Select, NeedHold? Hold, bool Bench, bool BuildAhead);

/// <summary>
/// The economy family (#3263, #3303). Its Needs: <c>economy/research</c> (Development), met while no recorded
/// research need is unfinished, which replaced EnsureResearch; and <c>economy/resource</c> (Wellbeing), met while
/// every derived stock target is held and no runway is short, which replaced the resource Standard. The research Need's
/// pure Method selects the next project; its laboratory benches, and all of the resource Need's work (mines,
/// tunnels and deep drills, chops and hunts, resource fields, the production bench), are the family's runtime work
/// under the Need, admitted by claims, so they go side by side.
/// </summary>
/// <param name="research">Derive the research Need.</param>
/// <param name="resource">Derive the resource Need.</param>
/// <param name="trade">Derive the trade Need (#3316): the silver gap answered with ledger sale batches.</param>
/// <param name="caravan">Derive the caravan and trade-mission Needs (#3316): caravan sessions and settlement missions.</param>
/// <remarks>
/// The trade Needs (#3316, <c>EconomyTrade.cs</c>): <c>economy/trade</c> (Development), the silver gap Concern before it;
/// <c>economy/caravan</c> (Wellbeing), a caravan Incident before it; and <c>economy/trade-mission</c>
/// (Development), whose mission is the <c>family/economy/trade_missions</c> record rather than a Standard.
/// </remarks>
public sealed partial class EconomyFamily(bool research = true, bool resource = false, bool trade = false, bool caravan = false) : IFamily<EconomyFacts>
{
    public const string FamilyId = "economy";

    public static readonly NeedId Research = NeedId.Of(FamilyId, "research");

    public static readonly NeedId Resource = NeedId.Of(FamilyId, "resource");

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = trade || caravan
        ? [FactFamily.Research, FactFamily.Colony, FactFamily.Rooms, FactFamily.Definitions, FactFamily.World, FactFamily.Pawns]
        : [FactFamily.Research, FactFamily.Colony, FactFamily.Rooms, FactFamily.Definitions];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(EconomyFacts facts) =>
        [
            .. research ? [ResearchNeed(facts)] : Array.Empty<Need>(),
            .. resource ? [EconomyRules.ResourceNeedOf(facts.Resource)] : Array.Empty<Need>(),
            .. trade ? [TradeNeed(facts.Trade)] : Array.Empty<Need>(),
            .. caravan ? [CaravanNeed(facts.Trade), TradeMissionNeed(facts.Missions)] : Array.Empty<Need>(),
        ];

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, EconomyFacts facts)
    {
        var methods = new List<NeedMethod>();
        if (ready.Any(n => n.Id == Resource) && EconomyRules.ResourceHold(facts.Resource) is { } held)
        {
            methods.Add(NeedMethod.Held(Resource, held.Subject, held.Detail));
        }
        if (!ready.Any(n => n.Id == Research))
        {
            return methods;
        }
        var step = EconomyRules.ResearchStepOf(facts);
        if (step.Select.Length != 0)
        {
            var project = step.Select;
            methods.Add(new NeedMethod(Research, new MethodId("research-" + project), "", id =>
                new PlanSpec(id, new PlanRevision(1), [DomainAction.NewResearchSelectAction(new ActionId(id.Value + "-0"), new ResearchSelect(project))])));
        }
        else if (step.Hold is { } hold)
        {
            methods.Add(NeedMethod.Held(Research, hold.Subject, hold.Detail));
        }
        return methods;
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
        var census = f.Research.TryGetValue(out var read) ? Known(EconomyRules.ResearchSummary(read!)) : Unknown<ResearchFacts>();
        var (target, derived) = ResearchRules.ResearchConcern(needs, census);
        var (recovered, deficit) = ResearchRules.ResearchTargetNeed(target, derived, census);
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

public static class EconomyRules
{
    /// <summary>
    /// Why the resource Need has no Method of its own: while a recorded research need is unfinished (the workshop
    /// ladder's rung among them) and the colony's own works demand nothing a chop or a mine provides, the
    /// economy/research Need works the project. Null when the runtime works the Need.
    /// </summary>
    public static NeedHold? ResourceHold(ResourceFacts f) =>
        f.ResearchGate.Length != 0 && !f.Works ? new NeedHold("research", f.ResearchGate) : null;

    /// <summary>
    /// The resource Need, a plain threshold with no latch: met with no target short and no runway short; unmet while
    /// the worst-covered target is short or a runway is known short; unknown while the stock census is unread, or a
    /// runway over a day's window is unread with nothing short. Its deficit is the worst target's shortfall fraction,
    /// raised to a short runway's missing share of the maintenance horizon.
    /// </summary>
    public static Need ResourceNeedOf(ResourceFacts f)
    {
        var census = new StockReader(f.Stock, f.Wood).Census(f.Targets);
        var (recovered, deficit) = ResearchRules.ResourceTargetNeed(f.Targets, census);
        var shortRunways = new List<string>();
        foreach (var runway in f.Runways)
        {
            var deficitKnown = runway.Deficit.TryGetValue(out var runwayDeficit);
            if (!deficitKnown && runway.WindowDays >= 1 && RoundsRules.Positive(recovered))
            {
                recovered = Unknown<bool>();
                deficit = Unknown<double>();
            }
            if (deficitKnown && runwayDeficit)
            {
                shortRunways.Add(runway.Resource.Value);
                recovered = Known(false);
                if (runway.DaysLeft.TryGetValue(out var days))
                {
                    deficit.TryGetValue(out var old);
                    deficit = Known(Math.Max(old, 1 - days / ColonyDiagnosticsRules.MaintenanceHorizonDays));
                }
            }
        }
        var satisfaction = !recovered.TryGetValue(out var met) ? Satisfaction.Unknown : met ? Satisfaction.Met : Satisfaction.Unmet;
        var worst = Unknown<string>();
        if (census.IsKnown)
        {
            try
            {
                var (resource, _, found) = ResourceRules.SelectResourceTarget(f.Targets, census);
                worst = Known(found ? resource.Value : "");
            }
            catch (DomainException)
            {
                // A census the selector cannot rank names no worst target.
                worst = Unknown<string>();
            }
        }
        return new Need
        {
            Id = EconomyFamily.Resource,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = satisfaction == Satisfaction.Unknown ? Unknown<double>() : deficit,
            Evidence =
            [
                new("resource_targets", Known(f.Targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                new("worst_target", worst),
                new("short_runways", Known(string.Join("+", shortRunways))),
                new("research_gate", Known(f.ResearchGate)),
            ],
        };
    }

    /// <summary>The review-shaped summary of a research census: current project and its bench lock, listed and finished projects, and the knowledge pick.</summary>
    public static ResearchFacts ResearchSummary(ResearchCensus read)
    {
        var projects = read.Projects.Keys.Select(name => new ResearchProjectId(name)).ToList();
        projects.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return new ResearchFacts
        {
            Current = new ResearchProjectId(read.Current),
            CurrentBenchMissing = read.Current.Length != 0 && ResearchRules.ResearchBenchNeeded(read.Projects.GetValueOrDefault(read.Current) ?? new ResearchProjectFacts()),
            Projects = EquatableArray.From(projects),
            Finished = [.. read.Finished.Select(name => new ResearchProjectId(name))],
            KnowledgePick = new ResearchProjectId(ResearchRules.KnowledgePick(read.Projects, read.Finished, read.Knowledge)),
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
        return ResearchRules.ResearchBenchNeeded(read.Projects[next])
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
        return (ResearchRules.KnowledgePick(read.Projects, read.Finished, read.Knowledge), null);
    }

    /// <summary>
    /// The next project toward the target: the head of its prerequisite queue. No target is no project; a target
    /// the census does not list, or a prerequisite graph it cannot fully inspect, holds as unavailable.
    /// </summary>
    internal static (string Project, NeedHold? Hold) ResearchNext(IReadOnlyList<string> needs, ResearchCensus read)
    {
        var (target, _) = ResearchRules.ResearchConcern(needs, Known(ResearchSummary(read)));
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
            queue = ResearchRules.ResearchPrerequisiteQueue(projects, [.. read.Finished.Select(name => new ResearchProjectId(name))], [new ResearchProjectId(target)]);
        }
        catch (DomainException)
        {
            return ("", new NeedHold("field_unavailable", "research_prerequisites"));
        }
        return queue.Count == 0 ? ("", new NeedHold("field_unavailable", "research_prerequisites")) : (queue[0].Value, null);
    }
}
