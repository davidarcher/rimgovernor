namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsRulesResult
{
    public Verdict Verdict { get; init; } = new();
    public PlanId Plan { get; init; }
}

/// <summary>
/// Attaches the native declarative rules the food plan needs: each Round it derives the rule set from the hunt
/// plan (Pol.HuntChainRules) and commits one rules_attach action under EnsureFoodSupply, so the attachment
/// has a receipt and sits in the session journal before native is written. The lease (Pol.RuleLeaseTicks) is
/// renewed every Round; an empty set clears rules an earlier Round attached, and a goal that is no longer
/// workable lets the lease lapse.
/// </summary>
public sealed class RoundsRulesPlanner
{
    /// <summary>Names EnsureFoodSupply's native rule attachments: one method per Round, its id the observed tick so the goal's history sorts by time.</summary>
    private const string RulesMethodPrefix = "rules-";

    private readonly Rounder _reviewer;

    private RoundsRulesPlanner(Rounder reviewer) => _reviewer = reviewer;

    public static RoundsRulesPlanner Create(Rounder? reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsRulesPlanner: reviewer == nil");
        }
        return new RoundsRulesPlanner(reviewer);
    }

    internal RoundsRulesResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsRulesResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid())
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsRulesResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(call, review, EnsureFoodSupply);
        if (!workable)
        {
            return new RoundsRulesResult { Verdict = BuildingReasonNoDeficit };
        }
        var expected = StepScope(call, _reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        // Hunters come from the pawn frame, not colony facts alone. After a census invalidation the fresh read
        // must populate both inputs too.
        var read = _reviewer.ObserveOwned(call, _reviewer.Native, expected, Unknown<IReadOnlyList<Pol.ConstructionClaim>>());
        var hunters = Unknown<IReadOnlyList<Pol.PawnProfile>>();
        if (read.Projection.WorkPawns.TryGetValue(out var pawns))
        {
            hunters = Known<IReadOnlyList<Pol.PawnProfile>>(Profiles(pawns));
        }
        var (set, known) = HuntChainRules(read.Projection.Acquisition, hunters, read.Projection.HuntHolds);
        if (!known)
        {
            return new RoundsRulesResult { Verdict = FieldUnavailable("hunt_census") };
        }
        return Attach(call, epoch, state, goal, expected.Tick, set);
    }

    /// <summary>Commits one rules_attach method for the set at tick: a renewal when the set stands, a clear when it is empty and an earlier Round attached rules.</summary>
    private RoundsRulesResult Attach(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, Tick tick, Pol.RuleSet set)
    {
        var p = _reviewer.Player;
        if (set.Rules.Count == 0)
        {
            var attached = Attached(call, goal);
            if (!attached)
            {
                return new RoundsRulesResult { Verdict = BuildingReasonNoDeficit };
            }
        }
        var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"{RulesMethodPrefix}{tick.Value:D12}"));
        try
        {
            p.Journal.LoadMethod(call, goal.Standard.Id, goal.Standard.Episode, method);
            return new RoundsRulesResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "rules_method") };
        }
        catch (Sto.NotFoundException)
        {
        }
        var attach = new RulesAttach(set.Rules, RuleLeaseTicks);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRulesAttachAction(new ActionId($"{id}-0"), attach);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("attach: p.session.State() != state");
        }
        var reason = set.Rules.Count == 0 ? "clear native rules" : "attach native rules";
        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, reason, plan);
        return new RoundsRulesResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }

    /// <summary>Whether the goal's latest rules method attached any rule, so an empty set is committed only to clear what an earlier Round left.</summary>
    private bool Attached(CancellationToken call, Sto.StandardState goal)
    {
        Method? latest = null;
        foreach (var method in goal.History)
        {
            if (method.MethodId.Value.StartsWith(RulesMethodPrefix, StringComparison.Ordinal) && (latest is null || string.CompareOrdinal(method.MethodId.Value, latest.MethodId.Value) > 0))
            {
                latest = method;
            }
        }
        if (latest is null)
        {
            return false;
        }
        var plan = _reviewer.Player.Journal.LoadPlan(call, latest.Plan);
        foreach (var progress in plan.Progress)
        {
            if (progress.Action.RulesAttach is { } attach && attach.Rules.Count > 0)
            {
                return true;
            }
        }
        return false;
    }
}
