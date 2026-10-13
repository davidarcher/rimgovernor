using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes ManagePollution: clear polluted area, allow and haul wastepacks.</summary>
public sealed class RoundsPollutionPlanner
{
    private readonly Rounder reviewer;

    private RoundsPollutionPlanner(Rounder reviewer) => this.reviewer = reviewer;

    public static RoundsPollutionPlanner NewRoundsPollutionPlanner(Rounder reviewer)
    {
        if (reviewer is null || reviewer.Native is null)
        {
            throw new ControlException("NewRoundsPollutionPlanner: reviewer or its native source is missing");
        }

        return new RoundsPollutionPlanner(reviewer);
    }

    internal RoundsPollutionResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsPollutionResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsPollutionResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.ManagePollution);
        if (!workable)
        {
            return new RoundsPollutionResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        var claimed = new HashSet<string>();
        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsPollutionResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }

            foreach (var progress in open.Progress)
            {
                if (progress.Action.TryWastepackHaul(out var haul) && progress.View.Stage == ProgressStage.Unsuccessful)
                {
                    claimed.Add(haul.Thing);
                }
            }
        }

        var started = reviewer.Clock.Now();
        var expected = BuildingRuntimePackage.StepScope(call, reviewer.Native);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var claims = p.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
        var read = reviewer.ObserveOwned(call, reviewer.Native, expected, claims);
        if (!read.Projection.Facts.Pollution.TryGetValue(out var facts))
        {
            return new RoundsPollutionResult { Verdict = BuildingRuntimePackage.FieldUnavailable("pollution") };
        }

        var work = PolicyFunctions.SelectPollutionWork(facts, claimed, PolicyFunctions.PollutedWindowCells(read.Projection.Cells));
        if (work.Empty())
        {
            return new RoundsPollutionResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "pollution_work") };
        }

        var text = new StringBuilder();
        foreach (var w in work.Allow)
        {
            text.Append($"allow/{w.Id}\n");
        }

        foreach (var w in work.Haul)
        {
            text.Append($"haul/{w.Id}\n");
        }

        foreach (var c in work.Area)
        {
            text.Append($"cell/{c.X}/{c.Z}\n");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        var method = new MethodId("pollution-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
        if (goal.Methods.Any(seen => seen.Method == method))
        {
            return new RoundsPollutionResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "pollution_method") };
        }

        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        ActionId Next() => new($"{id}-{actions.Count}");
        if (work.Area.Count > 0)
        {
            var area = PollutionClearArea.NewPollutionClearArea(AreaMode.SetCells, work.Area);
            actions.Add(DomainAction.NewAreaAction(Next(), area));
        }

        foreach (var w in work.Allow)
        {
            var supply = SupplyAllow.NewSupplyAllow(w.Id, w.Definition, w.Cell);
            actions.Add(DomainAction.NewSupplyAllowAction(Next(), supply));
        }

        foreach (var w in work.Haul)
        {
            var haul = WastepackHaul.NewWastepackHaul(w.Id, w.Definition, w.Cell);
            actions.Add(DomainAction.NewWastepackHaulAction(Next(), haul));
        }

        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return new RoundsPollutionResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }
}

public readonly record struct RoundsPollutionResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
