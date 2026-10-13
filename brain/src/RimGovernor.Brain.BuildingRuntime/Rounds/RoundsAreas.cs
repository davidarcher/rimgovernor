using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    internal static string AreaMethodPrefix(AllowedAreaChange c)
    {
        var text = $"{c.Pawn}/{(c.Animal ? "true" : "false")}/{c.Area}";
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return "area-" + Convert.ToHexStringLower(digest[..12]) + "-";
    }

    internal static (AllowedAreaChange Change, MethodId Method) NextAreaChange(IReadOnlyList<AllowedAreaChange> changes, int admitted)
    {
        var change = changes[admitted % changes.Count];
        return (change, new MethodId(AreaMethodPrefix(change) + admitted.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Cancels the pending area actions of a plan whose change the policy no longer wants.</summary>
    internal static void CancelStaleAreaActions(StoreJournal journal, PlanState plan, IReadOnlyList<AllowedAreaChange> changes, IReadOnlyList<WorkPawn> workers, CancellationToken ct)
    {
        var actions = plan.Spec.Actions;
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var stage = plan.Progress[i].View.Stage;
            if (stage != Stage.Pending && stage != Stage.Prepared)
            {
                continue;
            }
            AllowedAreaChange candidate;
            if (action.WorkAssignment is { HasArea: true } work)
            {
                candidate = new AllowedAreaChange { Pawn = new PawnId(work.Pawn.Value), Area = work.Area };
                var current = workers.Any(worker => worker.Id == candidate.Pawn);
                if (!current)
                {
                    candidate = candidate with { Pawn = default };
                }
            }
            else if (action.Husbandry is { Method: HusbandryMethod.AllowedArea } husbandry)
            {
                candidate = new AllowedAreaChange { Pawn = new PawnId(husbandry.Animal.Value), Animal = true, Area = husbandry.Argument };
            }
            else
            {
                continue;
            }
            if (!changes.Contains(candidate))
            {
                journal.Cancel(plan.Spec.Id, action.Id, ct);
            }
        }
    }
}

public sealed partial class RoundsRecoveryPlanner
{
    internal RoundsRecoveryResult CommitAreaChange(StepArbiter arbiter, GenerationSnapshot snapshot, IncidentState incident, IReadOnlyList<AllowedAreaChange> changes, IReadOnlyList<WorkPawn> workers, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var (change, method) = NextAreaChange(changes, incident.Methods.Count);
        if (!arbiter.TryClaim([change.Pawn]))
        {
            return new RoundsRecoveryResult { Verdict = WaitFor(Cause.MethodUsed, "area_change_pawn_claim") };
        }
        var id = PlanIds.MintPlanId();
        var actionId = new ActionId($"{id}-0");
        DomainAction action;
        if (change.Animal)
        {
            action = DomainAction.NewHusbandryAction(actionId, new Husbandry(new Domain.PawnId(change.Pawn.Value), HusbandryMethod.AllowedArea, change.Area));
        }
        else
        {
            if (!workers.Any(worker => worker.Id == change.Pawn))
            {
                return new RoundsRecoveryResult { Verdict = NoWorker("area_pawn") };
            }
            action = DomainAction.NewWorkAssignmentAction(actionId, WorkAssignment.NewAreaAssignment(new Domain.PawnId(change.Pawn.Value), change.Area == "", change.Area));
        }
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        var p = reviewer.Player;
        p.Current(call, epoch);
        var state = p.Session.State();
        var elapsed = reviewer.Clock.Now() - started;
        if (!state.Enabled || state.Snapshot != snapshot || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("commitAreaChange: !state.Enabled || state.Snapshot != snapshot || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitIncidentMethod(incident.Incident.Id, method, "", plan, call);
        return new RoundsRecoveryResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
