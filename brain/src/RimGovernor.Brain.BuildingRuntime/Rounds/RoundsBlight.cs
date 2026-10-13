using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsBlightSource : IColonySource
{
}

public sealed class RoundsBlightResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

/// <summary>Cuts blighted plants: one method of up to eight cut orders per admission.</summary>
public sealed class RoundsBlightPlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsBlightSource _native;

    public RoundsBlightPlanner(Rounder reviewer, IRoundsBlightSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsBlightPlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
    }

    internal RoundsBlightResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsBlightResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsBlightResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.RemoveBlight, call);
        if (!workable)
        {
            return new RoundsBlightResult { Verdict = BuildingReasonNoDeficit };
        }
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                return new RoundsBlightResult { Verdict = BuildingReasonExistingWork };
            }
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.CutPlant is { } cut && progress.View.Stage == Stage.Unsuccessful)
                {
                    claimed.Add(cut.Plant);
                }
            }
        }
        var started = _reviewer.Clock.Now();
        var identity = _native.Identity(call).Identity;
        var expected = ObservationApi.DecodeIdentity(identity);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var reading = _reviewer.ObserveColony(call, _native, expected, null);
        if (!reading.Projection.Facts.Blight.TryGetValue(out var census))
        {
            return new RoundsBlightResult { Verdict = FieldUnavailable("blighted_plants") };
        }
        var byId = new Dictionary<string, CutPlant>(StringComparer.Ordinal);
        foreach (var plant in census)
        {
            byId[plant.Id] = new CutPlant(plant.Id, plant.Definition, plant.Cell);
        }
        var targets = PolicyApi.SelectBlightCuts(census, claimed, 8);
        if (targets.Count == 0)
        {
            return new RoundsBlightResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "blight_cut_targets") };
        }
        var hash = new StringBuilder();
        foreach (var plant in targets)
        {
            hash.Append(CultureInfo.InvariantCulture, $"{plant.Id}/{plant.Definition}/{plant.Cell.X}/{plant.Cell.Z}\n");
        }
        var methodId = new MethodId("cut-" + HashHex16(hash.ToString()));
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        for (var i = 0; i < targets.Count; i++)
        {
            actions.Add(DomainAction.NewCutPlantAction(new ActionId($"{id}-{i}"), byId[targets[i].Id]));
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, methodId, spec, call);
        return new RoundsBlightResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
