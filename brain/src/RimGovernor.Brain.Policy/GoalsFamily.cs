namespace RimGovernor.Brain.Policy;

/// <summary>Where one goal stands.</summary>
public enum GoalState
{
    /// <summary>The goal's predicate holds.</summary>
    Met,

    /// <summary>An owning Need is working toward it.</summary>
    Pursuing,

    /// <summary>It cannot advance now: its facts are unread or its owner is blocked; <see cref="GoalStatus.Reason"/> says which.</summary>
    Blocked,

    /// <summary>No Need pursues the goal; <see cref="GoalStatus.Reason"/> says why.</summary>
    NoMethod,
}

/// <summary>One goal's standing: its state with the reason, the Need that pursues it and how far it is from met.</summary>
public sealed record GoalStatus(GoalRecord Goal, Satisfaction Satisfaction, GoalState State, string Reason, NeedId Owner, Fact<double> Deficit);

/// <summary>A theme's progress: how many of its goals stand met.</summary>
public sealed record ThemeStatus(int Id, int Met, int Total);

/// <summary>What an owning Need's progress row says about a goal's owner: a method it works, and why it is blocked when it is.</summary>
public sealed record OwnerProgress(bool Method, string Blocked);

/// <summary>
/// What a goal asks the existing channels for (#3494): research projects for the research-wanted set, stock floors
/// for the resource Need, a colonist head count for the joiner and lance population target and a head count per race
/// for the herd plan. The goals Family proposes no plans of its own.
/// </summary>
public sealed record GoalDemand
{
    public static readonly GoalDemand None = new();

    public IReadOnlyList<string> Research { get; init; } = [];

    public IReadOnlyDictionary<Resource, long> Stock { get; init; } = new Dictionary<Resource, long>();

    public int Colonists { get; init; }

    public IReadOnlyDictionary<Resource, long> Animals { get; init; } = new Dictionary<Resource, long>();

    /// <summary>The prisoners the goals name (#3499): the prisoner step recruits them whatever their worth.</summary>
    public IReadOnlyList<PawnId> Prisoners { get; init; } = [];

    /// <summary>The quests the goals name (#3499): the quest step considers them first.</summary>
    public IReadOnlyList<QuestId> Quests { get; init; } = [];
}

/// <summary>What the goals Family reads (#3494): the player's goals and the review facts they are judged by.</summary>
public sealed record GoalsFacts
{
    public IReadOnlyList<GoalRecord> Goals { get; init; } = [];

    public IReadOnlyList<ThemeRecord> Themes { get; init; } = [];

    public Fact<ResearchFacts> Research { get; init; }

    public Fact<TechTier> Tier { get; init; }

    public Fact<EquatableArray<Amount>> Stock { get; init; }

    public Fact<long> Colonists { get; init; }

    public Fact<EquatableArray<UpkeepAnimal>> Animals { get; init; }

    public Fact<Ideoligion> Ideology { get; init; }

    public Fact<bool> IdeologyInstalled { get; init; }

    /// <summary>The review's facts: the prisoner, custody and quest censuses the named-prisoner and named-quest goals are judged by.</summary>
    public RoundsFacts Rounds { get; init; } = new();

    /// <summary>The progress row of each owning Need that has one.</summary>
    public IReadOnlyDictionary<NeedId, OwnerProgress> Owners { get; init; } = new Dictionary<NeedId, OwnerProgress>();

    /// <summary>The facts a review already holds, with the goals and the owners' rows.</summary>
    public static GoalsFacts From(RoundsFacts f, Fact<TechTier> tier, IReadOnlyList<GoalRecord> goals, IReadOnlyList<ThemeRecord> themes, IReadOnlyDictionary<NeedId, OwnerProgress> owners) => new()
    {
        Goals = goals,
        Themes = themes,
        Research = f.Research,
        Tier = tier,
        Stock = f.Resources,
        Colonists = f.Colonists,
        Animals = f.AnimalUpkeep.Animals,
        Ideology = f.Ideology,
        IdeologyInstalled = f.IdeologyInstalled,
        Rounds = f,
        Owners = owners,
    };
}

/// <summary>
/// The goals Family (#3494): the player's goals as Development Needs <c>goals/&lt;kind&gt;/&lt;id&gt;</c>. It owns status
/// only. A goal becomes demand on the channels that already exist (<see cref="GoalRules.DemandOf"/>), and an unmet
/// goal's Method is a hold naming what pursues it (<c>pursuing:&lt;owner need&gt;</c>), what blocks it
/// (<c>blocked:&lt;reason&gt;</c>) or why nothing does (<c>no_method:&lt;reason&gt;</c>). A kind the brain does not pursue is
/// accepted and reported.
/// </summary>
public sealed class GoalsFamily : IFamily<GoalsFacts>
{
    public const string FamilyId = "goals";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Research, FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(GoalsFacts facts) =>
        [.. facts.Goals.Select(goal => GoalRules.Evaluate(goal, facts)).Select(s => new Need
        {
            Id = GoalRules.NeedOf(s.Goal),
            Tier = NeedTier.Development,
            Satisfaction = s.Satisfaction,
            Deficit = s.Deficit,
        })];

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, GoalsFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            var goal = facts.Goals.FirstOrDefault(g => GoalRules.NeedOf(g) == need.Id);
            if (goal is null)
            {
                continue;
            }
            var status = GoalRules.Evaluate(goal, facts);
            var (subject, detail) = status.State switch
            {
                GoalState.Pursuing => ("pursuing:" + status.Owner.Value, "Pursued by " + status.Owner.Value + "."),
                GoalState.Blocked => ("blocked:" + status.Reason, "The goal cannot advance: " + status.Reason + "."),
                _ => ("no_method:" + status.Reason, "Nothing pursues this goal: " + status.Reason + "."),
            };
            output.Add(NeedMethod.Held(need.Id, subject, detail));
        }
        return output;
    }
}

public static class GoalRules
{
    /// <summary>The NeedId of <paramref name="goal"/>: <c>goals/&lt;kind&gt;/&lt;id&gt;</c>.</summary>
    public static NeedId NeedOf(GoalRecord goal) => NeedId.Of(GoalsFamily.FamilyId, KindSlug(goal.Kind), goal.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The lowercase hyphenated name of a kind.</summary>
    public static string KindSlug(GoalKind kind)
    {
        var name = kind.ToString();
        var text = new System.Text.StringBuilder(name.Length + 2);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                text.Append('-');
            }
            text.Append(char.ToLowerInvariant(name[i]));
        }
        return text.ToString();
    }

    /// <summary>Evaluates one goal against the facts: the predicate, then who pursues it.</summary>
    public static GoalStatus Evaluate(GoalRecord goal, GoalsFacts f)
    {
        Fact<bool> met;
        Fact<double> deficit;
        NeedId owner;
        switch (goal.Kind)
        {
            case GoalKind.ResearchProject:
                (met, deficit) = ProjectMet(goal.Target, f.Research);
                owner = EconomyFamily.Research;
                break;
            case GoalKind.ResearchTier:
                (met, deficit) = TierMet(goal.Target, f.Tier);
                owner = EconomyFamily.Research;
                break;
            case GoalKind.StockCount:
                (met, deficit) = Shortfall(goal.Count, f.Stock.TryGetValue(out var stock) ? Fact.Known(stock.Where(a => a.Resource == new Resource(goal.Target)).Sum(a => a.Count)) : Fact.Unknown<long>());
                owner = EconomyFamily.Resource;
                break;
            case GoalKind.Colonists:
                (met, deficit) = Shortfall(goal.Count, f.Colonists);
                owner = JoinersFamily.Offers;
                break;
            case GoalKind.Animals:
                (met, deficit) = Shortfall(goal.Count, f.Animals.TryGetValue(out var animals) ? Fact.Known((long)animals.Count(a => a.Definition == new Resource(goal.Target))) : Fact.Unknown<long>());
                owner = AnimalsFamily.Herd;
                break;
            case GoalKind.Precept:
            case GoalKind.Meme:
                return Adoption(goal, f);
            case GoalKind.NamedPrisoner:
                return NamedPrisoner(goal, f);
            case GoalKind.NamedQuest:
                return NamedQuest(goal, f);
            default:
                return new GoalStatus(goal, Satisfaction.Unknown, GoalState.NoMethod, NoPursuit, default, default);
        }
        if (!met.TryGetValue(out var isMet))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, deficit);
        }
        return isMet ? new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", owner, deficit) : Pursued(goal, f, owner, deficit);
    }

    /// <summary>An unmet goal an owning Need can work: blocked when that Need's progress row says why it has no method, else pursuing.</summary>
    private static GoalStatus Pursued(GoalRecord goal, GoalsFacts f, NeedId owner, Fact<double> deficit) =>
        f.Owners.TryGetValue(owner, out var row) && !row.Method && row.Blocked != ""
            ? new GoalStatus(goal, Satisfaction.Unmet, GoalState.Blocked, row.Blocked, owner, deficit)
            : new GoalStatus(goal, Satisfaction.Unmet, GoalState.Pursuing, "", owner, deficit);

    /// <summary>A named prisoner: met once a colonist, pursued by the prisoners/recruit Need while held and recruitable.</summary>
    private static GoalStatus NamedPrisoner(GoalRecord goal, GoalsFacts f)
    {
        var owner = PrisonersFamily.Recruit;
        var one = Fact.Known(1.0);
        var pawn = new PawnId(goal.Target);
        if (!f.Rounds.Prisoners.TryGetValue(out var prisoners))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
        }
        var row = prisoners.FirstOrDefault(p => p.Pawn == pawn);
        if (row is null)
        {
            if (!f.Rounds.Custody.TryGetValue(out var custody))
            {
                return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
            }
            var colonist = custody.Any(c => c.Pawn == pawn && c.Admitted.TryGetValue(out var admitted) && admitted && c.Dead.TryGetValue(out var dead) && !dead);
            return colonist
                ? new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", owner, Fact.Known(0.0))
                : new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, NotAPrisoner, default, one);
        }
        if (!row.Dead.TryGetValue(out var gone) || !row.Recruitable.TryGetValue(out var recruitable))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
        }
        if (gone)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, NotAPrisoner, default, one);
        }
        return recruitable ? Pursued(goal, f, owner, one) : new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "not_recruitable", default, one);
    }

    /// <summary>A named quest: met when it completes, pursued by the quest step (the offers step for a joiner) while an admission check passes, blocked on the check that fails, no method once it expires or fails.</summary>
    private static GoalStatus NamedQuest(GoalRecord goal, GoalsFacts f)
    {
        var one = Fact.Known(1.0);
        if (!f.Rounds.QuestOffers.TryGetValue(out var offers))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", JoinersFamily.Quests, default);
        }
        var quest = new QuestId(goal.Target);
        var offer = offers.FirstOrDefault(o => o.Quest == quest);
        if (offer is null)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "unknown_quest", default, one);
        }
        var owner = PopulationRules.IsJoinerOffer(offer.ScriptDef) ? JoinersFamily.Offers : JoinersFamily.Quests;
        switch (offer.State)
        {
            case "EndedSuccess":
                return new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", owner, Fact.Known(0.0));
            case "EndedOfferExpired":
                return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, Expired, default, one);
            case "Ongoing":
                return Pursued(goal, f, owner, one);
            case "NotYetAccepted":
                var reason = owner == JoinersFamily.Offers ? QuestSelection.JoinerThreatReason(offer, f.Rounds) : QuestSelection.NamedQuestReason(offer, f.Rounds);
                if (reason == Expired)
                {
                    return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, Expired, default, one);
                }
                return reason.IsNone ? Pursued(goal, f, owner, one) : new GoalStatus(goal, Satisfaction.Unmet, GoalState.Blocked, reason.ToString(), owner, one);
            default:
                return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "ended", default, one);
        }
    }

    /// <summary>Every theme's progress: its member goals that stand met.</summary>
    public static IReadOnlyList<ThemeStatus> Themes(GoalsFacts f)
    {
        var met = f.Goals.Where(g => Evaluate(g, f).State == GoalState.Met).Select(g => g.Id).ToHashSet();
        return [.. f.Themes.Select(t => new ThemeStatus(t.Id, t.Members.Count(met.Contains), t.Members.Count))];
    }

    /// <summary>What the goals ask the existing channels for.</summary>
    public static GoalDemand DemandOf(GoalsFacts f)
    {
        var research = new SortedSet<string>(StringComparer.Ordinal);
        var stock = new Dictionary<Resource, long>();
        var animals = new Dictionary<Resource, long>();
        var colonists = 0;
        var prisoners = new List<PawnId>();
        var quests = new List<QuestId>();
        foreach (var goal in f.Goals)
        {
            switch (goal.Kind)
            {
                case GoalKind.NamedPrisoner when goal.Target != "":
                    prisoners.Add(new PawnId(goal.Target));
                    break;
                case GoalKind.NamedQuest when goal.Target != "":
                    quests.Add(new QuestId(goal.Target));
                    break;
                case GoalKind.ResearchProject:
                    if (goal.Target != "" && Evaluate(goal, f).State != GoalState.Met && f.Research.TryGetValue(out _))
                    {
                        research.Add(goal.Target);
                    }
                    break;
                case GoalKind.ResearchTier:
                    if (Enum.TryParse<TechTier>(goal.Target, true, out var target) && f.Tier.TryGetValue(out var tier))
                    {
                        for (var t = tier + 1; t <= target; t++)
                        {
                            research.Add(TechTierRules.Unlocks[t][0]);
                        }
                    }
                    break;
                case GoalKind.StockCount when goal.Target != "" && goal.Count > 0:
                    stock[new Resource(goal.Target)] = Math.Max(stock.GetValueOrDefault(new Resource(goal.Target)), goal.Count);
                    break;
                case GoalKind.Animals when goal.Target != "" && goal.Count > 0:
                    animals[new Resource(goal.Target)] = Math.Max(animals.GetValueOrDefault(new Resource(goal.Target)), goal.Count);
                    break;
                case GoalKind.Colonists:
                    colonists = Math.Max(colonists, goal.Count);
                    break;
            }
        }
        return new GoalDemand { Research = [.. research], Stock = stock, Colonists = colonists, Animals = animals, Prisoners = prisoners, Quests = quests };
    }

    /// <summary>The review facts with the goals' stock floors in them, and everything <see cref="Stamp"/> carries.</summary>
    public static RoundsFacts Apply(RoundsFacts f, GoalDemand d) => Stamp(f with { ResourceNeeds = ResourceRules.ResourceConcernTargets(f.ResourceNeeds, d.Stock) }, d);

    /// <summary>The facts with the goals' colonist head count, herd targets, named quests and named prisoners: what every read of the world carries.</summary>
    public static RoundsFacts Stamp(RoundsFacts f, GoalDemand d)
    {
        f = f with { ColonistGoal = d.Colonists, AnimalGoals = d.Animals, NamedQuests = d.Quests };
        return f.PrisonerColony.TryGetValue(out var colony) ? f with { PrisonerColony = Fact.Known(colony with { NamedPrisoners = d.Prisoners }) } : f;
    }

    private const string NoPursuit = "no_pursuit";

    private const string NotAPrisoner = "not a prisoner";

    private const string Expired = "expired";

    private static GoalStatus Adoption(GoalRecord goal, GoalsFacts f)
    {
        if (f.IdeologyInstalled.TryGetValue(out var installed) && !installed)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "missing_dlc", default, Fact.Known(1.0));
        }
        if (!f.Ideology.TryGetValue(out var ideo))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", default, default);
        }
        var held = goal.Kind == GoalKind.Precept ? ideo.Facts.Precepts.Any(p => p.Def == goal.Target) : ideo.Facts.Memes.Contains(goal.Target);
        if (held)
        {
            return new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", default, Fact.Known(0.0));
        }
        if (!ideo.Facts.Development.TryGetValue(out var development))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", default, default);
        }
        return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, development.Fluid ? NoPursuit : "not fluid", default, Fact.Known(1.0));
    }

    private static (Fact<bool> Met, Fact<double> Deficit) ProjectMet(string project, Fact<ResearchFacts> research)
    {
        if (!research.TryGetValue(out var facts) || project == "" || !facts.Projects.Any(p => p.Value == project))
        {
            return (default, default);
        }
        var finished = facts.Finished.Any(p => p.Value == project);
        return (Fact.Known(finished), Fact.Known(finished ? 0.0 : 1.0));
    }

    private static (Fact<bool> Met, Fact<double> Deficit) TierMet(string target, Fact<TechTier> tier)
    {
        if (!tier.TryGetValue(out var reached) || !Enum.TryParse<TechTier>(target, true, out var wanted))
        {
            return (default, default);
        }
        return (Fact.Known(reached >= wanted), Fact.Known((double)Math.Max(0, wanted - reached)));
    }

    private static (Fact<bool> Met, Fact<double> Deficit) Shortfall(long wanted, Fact<long> have)
    {
        if (!have.TryGetValue(out var count))
        {
            return (default, default);
        }
        return (Fact.Known(count >= wanted), Fact.Known((double)Math.Max(0, wanted - count)));
    }
}
