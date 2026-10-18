using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the goals family (#3494): the full read (the player's goals from the <c>family/goals</c> record,
/// the review facts they are judged by and the progress rows of the Needs that pursue them) and the review's
/// <c>goal_review</c> rows. The family proposes no plans: the demand the goals place on the existing channels is
/// injected by the Rounder's review (<see cref="GoalRules.DemandOf"/>).
/// </summary>
public sealed class GoalsRounds
{
    private static readonly NeedId[] Owners = [EconomyFamily.Research, EconomyFamily.Resource, AnimalsFamily.Herd, JoinersFamily.Offers, JoinersFamily.Quests, PrisonersFamily.Recruit];

    private readonly Rounder _reviewer;

    public GoalsRounds(Rounder reviewer)
    {
        _reviewer = reviewer ?? throw new ControlException("GoalsRounds: reviewer is missing");
    }

    /// <summary>The family's scheduler entry; it wakes on its reads and runs no action kind.</summary>
    public FamilyRunner Runner() => new FamilyRunner<GoalsFacts>(new GoalsFamily(), ReadAsync, Schedule);

    /// <summary>An optional entry with no action kinds: its Needs are status only.</summary>
    public static FamilySchedule Schedule { get; } = new() { Class = PlannerClass.Optional };

    /// <summary>Reads the goals and their facts in <paramref name="scope"/>'s world, and files one review row per goal; the immediate pass reads nothing.</summary>
    internal async Task<GoalsFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        if (scope.Immediate)
        {
            return new GoalsFacts();
        }
        var journal = _reviewer.Player.Journal;
        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("goals read: the scope is another world or older than the review");
        }
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), ct).ConfigureAwait(false);
        var goals = await journal.LoadGoalsAsync(Player.PlayerWorld(scope.World), ct).ConfigureAwait(false);
        var owners = new Dictionary<NeedId, OwnerProgress>();
        foreach (var owner in Owners)
        {
            if (review.ConcernProgress(owner.FilingKey) is { } row)
            {
                owners[owner] = new OwnerProgress(row.Method != "", row.Blocked.IsEmpty || row.Blocked.Waiting() ? "" : row.Blocked.Text);
            }
        }
        var facts = GoalsFacts.From(reading.Projection.Facts, reading.Projection.TechTier, goals.Goals, goals.Themes, owners);
        Record(facts);
        return facts;
    }

    /// <summary>One <c>goal_review</c> decision per goal: the state as verdict, the reason, the goal id as target and the owner and deficit as attrs.</summary>
    internal static IReadOnlyList<Decision> ReviewDecisions(GoalsFacts facts)
    {
        var themes = GoalRules.Themes(facts).ToDictionary(t => t.Id);
        return [.. facts.Goals.Select(goal => ReviewDecision(GoalRules.Evaluate(goal, facts), themes))];
    }

    private static void Record(GoalsFacts facts)
    {
        foreach (var decision in ReviewDecisions(facts))
        {
            Tel.Decide(decision);
        }
    }

    internal static Decision ReviewDecision(GoalStatus status, IReadOnlyDictionary<int, ThemeStatus> themes)
    {
        var attrs = new Dictionary<string, object?>
        {
            ["kind"] = GoalRules.KindSlug(status.Goal.Kind),
            ["need"] = GoalRules.NeedOf(status.Goal).Value,
            ["owner"] = status.Owner.Value,
        };
        if (status.Deficit.TryGetValue(out var deficit))
        {
            attrs["deficit"] = deficit;
        }
        if (status.Goal.Theme is { } theme && themes.TryGetValue(theme, out var progress))
        {
            attrs["theme"] = progress.Id;
            attrs["theme_met"] = progress.Met;
            attrs["theme_total"] = progress.Total;
        }
        return new Decision
        {
            Kind = TelemetryKinds.GoalReviewKind,
            Component = GoalsFamily.FamilyId,
            Verdict = status.State switch
            {
                GoalState.Met => "met",
                GoalState.Pursuing => "pursuing",
                GoalState.Blocked => "blocked",
                _ => "no_method",
            },
            Reason = status.Reason,
            Target = status.Goal.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Attrs = attrs,
        };
    }
}
