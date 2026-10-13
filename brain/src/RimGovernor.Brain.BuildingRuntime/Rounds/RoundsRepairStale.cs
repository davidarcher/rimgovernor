namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>Cancels repair actions that have not started once the standard is met.</summary>
    internal static void CancelSettledRepairMethods(StoreJournal journal, StandardState goal, CancellationToken ct)
    {
        if (goal.Standard.Finding != Finding.Met)
        {
            return;
        }

        foreach (var method in goal.Methods)
        {
            var plan = journal.LoadPlan(method.Plan, ct);
            if (!StorePackage.PlanOpen(plan))
            {
                continue;
            }

            var repairs = new HashSet<ActionId>();
            foreach (var action in plan.Spec.Actions)
            {
                if (action.Repair is not null)
                {
                    repairs.Add(action.Id);
                }
            }

            foreach (var progress in plan.Progress)
            {
                var v = progress.View;
                if (!repairs.Contains(v.Action) || (v.Stage != Stage.Pending && v.Stage != Stage.Prepared))
                {
                    continue;
                }

                journal.Cancel(method.Plan, v.Action, ct);
            }
        }
    }
}
