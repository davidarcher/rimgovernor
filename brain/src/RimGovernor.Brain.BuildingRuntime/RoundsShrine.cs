namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The shrine census plus the readiness reads the breach goal judges from, and the claim target read that
/// gives each empty casket its CAS token.
/// </summary>
public interface IRoundsShrineSource : Observation.IColonySource, Observation.IShrineSource, IShrineReadinessNative
{
    (ClaimBuildingTarget Target, BridgeResult Result) ReadClaimBuildingTarget(CancellationToken ct, C.Identity identity, string entity);
}

public sealed record RoundsShrineResult
{
    public Verdict Verdict { get; init; } = new();
    public PlanId Plan { get; init; }

    /// <summary>The readiness reason the planner held on (BuildingReasonHeld).</summary>
    public string Hold { get; init; } = "";

    /// <summary>The shrine the planner judged.</summary>
    public string Shrine { get; init; } = "";

    /// <summary>Every candidate the step judged and passed over, with its own reason, beside the one Shrine names.</summary>
    public IReadOnlyList<Pol.ShrineHold> Skipped { get; init; } = [];

    public uint NativeWorkTicks { get; init; }

    /// <summary>Records one candidate the step judged and moved past: the first becomes the step's hold, every later one a skipped candidate.</summary>
    internal RoundsShrineResult Pass(string shrine, string reason) =>
        Hold == ""
            ? this with { Hold = reason, Shrine = shrine }
            : this with { Skipped = [.. Skipped, new Pol.ShrineHold { Shrine = shrine, Reason = reason }] };
}

/// <summary>
/// Composes the ClearAncientShrine goal's methods: the breach, when readiness reads Ready for a sealed shrine
/// touching Home it drafts the squad to standing cells behind the trap line and designates the chosen wall for
/// an in-place deconstruction (the wall falling is the method's end: the plan has no open work, the worker
/// releases the owned drafts and ActiveCombat answers the guards); and the claim, once the shrine is open and
/// guard-free every empty casket the player does not own is claimed in one method; and the opening, once the
/// review's opening gate holds, the melee lock: one violence-capable melee colonist drafted at each filled
/// casket and one OpenCasket order, held lock_understaffed while the squad cannot cover every casket. While the
/// gate holds back, filled caskets stay sealed.
/// </summary>
public sealed class RoundsShrinePlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsShrineSource _native;

    private RoundsShrinePlanner(Rounder reviewer, IRoundsShrineSource native)
    {
        _reviewer = reviewer;
        _native = native;
    }

    public static RoundsShrinePlanner Create(Rounder? reviewer, IRoundsShrineSource? native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsShrinePlanner: reviewer == nil || native == nil");
        }
        return new RoundsShrinePlanner(reviewer, native);
    }

    internal RoundsShrineResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsShrineResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid())
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsShrineResult { Verdict = BuildingReasonNoReview };
        }
        var (recordedCall, recordedStep) = RecordPlannerStep(call, ClearAncientShrine, state.Snapshot, review.Tick);
        call = recordedCall;
        using var recorded = recordedStep;
        // The step's own answer goes on the review it planned under, so the journal names the shrine it held
        // on rather than leaving the advisory ShrineHolds, in identity order, to read as the cause. A review
        // filed since is the newer answer; the record yields to it. held collects the candidates the step
        // passed over; a step that went on to act on a later shrine names every one of them skipped.
        var held = new System.Runtime.CompilerServices.StrongBox<RoundsShrineResult>(new RoundsShrineResult());
        var result = Plan(call, epoch, arbiter, p, state, review, held);
        if (result.Verdict.IsZero)
        {
            return result;
        }
        if (result.Shrine != held.Value.Shrine || result.Hold != held.Value.Hold)
        {
            var skipped = new List<Pol.ShrineHold>(held.Value.Skipped);
            skipped.AddRange(result.Skipped);
            if (held.Value.Hold != "" && held.Value.Shrine != result.Shrine)
            {
                skipped.Add(new Pol.ShrineHold { Shrine = held.Value.Shrine, Reason = held.Value.Hold });
            }
            result = result with { Skipped = skipped };
        }
        var step = new Sto.RoundsShrineStep { Tick = review.Tick, Reason = result.Verdict.ToString(), Shrine = result.Shrine, Hold = result.Hold, Plan = result.Plan, Skipped = result.Skipped };
        try
        {
            p.Journal.RecordShrineStep(call, review.Revision, step);
        }
        catch (Sto.ConflictException)
        {
        }
        return result;
    }

    private RoundsShrineResult Plan(CancellationToken call, CancellationToken epoch, StepArbiter arbiter, Player p, ControlState state, Sto.Rounds review, System.Runtime.CompilerServices.StrongBox<RoundsShrineResult> held)
    {
        // A pause or authority change between the draft and the move releases the draft; the moves riding on
        // it and the open waiting on them can then never dispatch, and while they stay open the goal never
        // re-plans and the clock holds on work that cannot run. Settle every shrine plan so orphaned,
        // including those of a goal a reload invalidated, before the active goal is read.
        SettleOrphanedPlans(call);
        var (goal, workable) = p.Journal.Workable(call, review, ClearAncientShrine);
        if (!workable)
        {
            return new RoundsShrineResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(call, method.Plan);
            if (Sto.PlanState.PlanOpen(plan))
            {
                return new RoundsShrineResult { Verdict = BuildingReasonExistingWork };
            }
        }
        var started = _reviewer.Clock.Now();
        var (identity, _) = _native.Identity(call);
        var expected = ObservationPackage.DecodeIdentity(identity);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var colony = _reviewer.ObserveColony(call, _native, expected, null);
        var read = ObservationPackage.ObserveShrines(call, _native, expected);
        if (!read.TryGetValue(out var shrines))
        {
            return new RoundsShrineResult { Verdict = FieldUnavailable("shrines") };
        }
        // The opening gate was judged at the review; a casket it decided open there is owed an opening now.
        // The lock is re-staffed below.
        var opening = ShrineOpeningFromHolds(review.ShrineHolds);
        var targets = new HashSet<string>(ShrineClearanceTargets(shrines, opening), StringComparer.Ordinal);
        var candidates = shrines.Where(s => targets.Contains(s.Id)).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0)
        {
            return new RoundsShrineResult { Verdict = BuildingReasonNoDeficit };
        }
        var claims = ShrineClaimTargets(candidates);
        foreach (var shrine in candidates)
        {
            if (claims.TryGetValue(shrine.Id, out var caskets) && caskets.Count > 0)
            {
                return Claim(call, epoch, state, goal, shrine, caskets, started);
            }
        }
        held.Value = new RoundsShrineResult { Verdict = BuildingReasonHeld };
        var opens = ShrineOpenTargets(candidates, opening);
        if (opens.Count > 0)
        {
            var needed = PlannedDrafts(call, p.Journal);
            var squad = ShrineSquad(call, _native, Boundary.Identity(state.Snapshot), null, needed);
            SnapshotPackage.NoteShrineSquad(squad);
            foreach (var shrine in candidates)
            {
                if (!opens.TryGetValue(shrine.Id, out var caskets) || caskets.Count == 0)
                {
                    continue;
                }
                var lockRow = ShrineMeleeLock(caskets, squad);
                if (lockRow.Reason != "")
                {
                    held.Value = held.Value.Pass(shrine.Id, lockRow.Reason);
                    continue;
                }
                return Open(call, epoch, state, goal, shrine, caskets, lockRow, started, arbiter);
            }
        }
        if (!colony.Projection.Center().TryGetValue(out var center))
        {
            return new RoundsShrineResult { Verdict = BuildingNoLayoutPlan };
        }
        var reports = ShrineReadiness(call, _native, Boundary.Identity(state.Snapshot), candidates, null, colony.Projection.Threat.RaidPoints, center, colony.Projection.Bounds);
        for (var i = 0; i < reports.Count; i++)
        {
            var shrine = candidates[i];
            var reason = ShrineHoldReason(shrine, reports[i].Readiness);
            if (reason != ShrineReady)
            {
                held.Value = held.Value.Pass(shrine.Id, reason);
                continue;
            }
            return Breach(call, epoch, state, goal, shrine, reports[i], colony.Projection, started, arbiter);
        }
        return held.Value;
    }

    /// <summary>
    /// Commits one open, guard-free shrine's casket method: a ClaimBuilding for every empty casket the player
    /// does not own, each under the CAS token a fresh target read gives it. A casket the read already shows as
    /// the player's (a census a tick behind) is skipped; a claim refused natively surfaces as the action's own
    /// unsuccessful outcome.
    /// </summary>
    private RoundsShrineResult Claim(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, Pol.AncientShrine shrine, IReadOnlyList<Pol.ShrineCasket> caskets, DateTimeOffset started)
    {
        var p = _reviewer.Player;
        var prefix = $"claim-{shrine.Id}-";
        var (method, verdict, ok) = AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsShrineResult { Verdict = verdict, Shrine = shrine.Id };
        }
        var id = PlanIds.MintPlanId();
        var identity = Boundary.Identity(state.Snapshot);
        var actions = new List<DomainAction>();
        foreach (var casket in caskets)
        {
            var (target, _) = _native.ReadClaimBuildingTarget(call, identity, casket.EntityId);
            if (target.PlayerOwned)
            {
                continue;
            }
            actions.Add(DomainAction.NewClaimBuildingAction(new ActionId($"{id}-claim-{casket.EntityId}"), new ClaimBuilding(casket.EntityId)));
        }
        if (actions.Count == 0)
        {
            return new RoundsShrineResult { Verdict = BuildingReasonNoDeficit, Shrine = shrine.Id };
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("claim: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, "", plan);
        return new RoundsShrineResult { Verdict = BuildingReasonAdmitted, Plan = id, Shrine = shrine.Id };
    }

    /// <summary>
    /// Commits one sealed shrine's method: an owned draft and a move to a standing cell behind the trap line
    /// for each drafted defender, and the breach deconstruction of the chosen wall. One colonist is always left
    /// undrafted for the deconstruct job. The shared refusal budget decides whether the wall may be tried again.
    /// </summary>
    private RoundsShrineResult Breach(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, Pol.AncientShrine shrine, ShrineReadinessReport report, Observation.ColonyProjection projection, DateTimeOffset started, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var wall = report.Readiness.Wall;
        if (!projection.Facts.Colonists.TryGetValue(out var colonists))
        {
            return new RoundsShrineResult { Verdict = FieldUnavailable("colonists") };
        }
        var drafted = ShrineBreachDrafts(report.Readiness.Squad, (int)colonists);
        if (!arbiter.TryClaim(drafted))
        {
            return new RoundsShrineResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "breach_defenders") };
        }
        var positions = ShrineBreachPositions(wall, drafted, report.Standing, report.Traps);
        var prefix = $"breach-{shrine.Id}-{wall.EntityId}-";
        var (method, verdict, ok) = AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsShrineResult { Verdict = verdict, Shrine = shrine.Id };
        }
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        foreach (var defender in drafted)
        {
            var draftId = new ActionId($"{id}-draft-{defender}");
            actions.Add(DomainAction.NewOwnedDraftAction(draftId, new OwnedDraft(defender)));
            if (!positions.TryGetValue(defender, out var cell))
            {
                continue;
            }
            var movement = new Movement(defender, cell, draftId);
            actions.Add(DomainAction.NewMovementAction(new ActionId($"{id}-move-{defender}"), movement));
        }
        var definition = wall.DefName == "" ? "Wall" : wall.DefName;
        var breachId = new ActionId($"{id}-breach");
        actions.Add(DomainAction.NewDeconstructionAction(breachId, new Deconstruction(wall.EntityId, definition, wall.Cell)));
        // The wall goes only once every drafted defender stands: the breach waits on each draft (and each move when a cell was found).
        var dependencies = actions.Take(actions.Count - 1).Select(a => new ActionDependency(breachId, a.Id)).ToList();
        var plan = new PlanSpec(id, new PlanRevision(1), actions, dependencies);
        p.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("breach: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, "", plan);
        return new RoundsShrineResult { Verdict = BuildingReasonAdmitted, Plan = id, Shrine = shrine.Id };
    }

    /// <summary>
    /// Commits one open, guard-free shrine's melee lock: an owned draft and a move to the casket's interaction
    /// cell for each locker, then one OpenCasket by the opener on the lowest casket, which ejects every casket
    /// of the group. The lockers stand where the ancients drop and their drafted auto-attack answers a waking
    /// hostile; the plan has no further work, so the worker keeps the drafts until the opening resolves and
    /// ActiveCombat and the custody planner take the occupants from there.
    /// </summary>
    private RoundsShrineResult Open(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, Pol.AncientShrine shrine, IReadOnlyList<Pol.ShrineCasket> caskets, Pol.ShrineLock lockRow, DateTimeOffset started, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var lockers = caskets.Select(c => lockRow.Lockers[c.EntityId]).ToList();
        if (!arbiter.TryClaim(lockers))
        {
            return new RoundsShrineResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "casket_lockers") };
        }
        var prefix = $"open-{shrine.Id}-";
        var (method, verdict, ok) = AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsShrineResult { Verdict = verdict, Shrine = shrine.Id };
        }
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        Pol.ShrineCasket? target = null;
        foreach (var casket in caskets)
        {
            if (casket.EntityId == lockRow.Casket)
            {
                target = casket;
            }
            var locker = lockRow.Lockers[casket.EntityId];
            var draftId = new ActionId($"{id}-draft-{locker}");
            actions.Add(DomainAction.NewOwnedDraftAction(draftId, new OwnedDraft(locker)));
            var movement = new Movement(locker, casket.InteractionCell, draftId);
            actions.Add(DomainAction.NewMovementAction(new ActionId($"{id}-move-{locker}"), movement));
        }
        if (target is null || target.EntityId == "")
        {
            throw new ControlException("open: target.EntityID == \"\"");
        }
        var openId = new ActionId($"{id}-open");
        actions.Add(DomainAction.NewOpenCasketAction(openId, new OpenCasket(lockRow.Opener, target.EntityId, target.Cell)));
        // The casket opens only once every locker stands at a casket.
        var dependencies = actions.Take(actions.Count - 1).Select(a => new ActionDependency(openId, a.Id)).ToList();
        var plan = new PlanSpec(id, new PlanRevision(1), actions, dependencies);
        p.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("open: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, "", plan);
        return new RoundsShrineResult { Verdict = BuildingReasonAdmitted, Plan = id, Shrine = shrine.Id };
    }

    /// <summary>Whether method is one the shrine planner admits (claim, breach or open), read from the plan's stored method.</summary>
    internal static bool ShrineMethod(MethodId method)
    {
        foreach (var prefix in new[] { "claim-", "breach-", "open-" })
        {
            if (method.Value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Cancels the unissued work of every shrine plan whose draft was lost (OrphanedDraftDependents): the open
    /// and retreat wait on the move, so the whole plan settles, not only the orders on the draft.
    /// </summary>
    private void SettleOrphanedPlans(CancellationToken call)
    {
        var p = _reviewer.Player;
        var plans = p.Journal.LoadPlans(call);
        foreach (var plan in plans)
        {
            if (!ShrineMethod(plan.Method) || OrphanedDraftDependents(plan.Spec, plan.Progress).Count == 0)
            {
                continue;
            }
            foreach (var progress in plan.Progress)
            {
                var v = progress.View;
                if (v.Stage == Stage.Pending || v.Stage == Stage.Prepared)
                {
                    p.Journal.Cancel(call, plan.Spec.Id, v.Action);
                }
            }
        }
    }
}
