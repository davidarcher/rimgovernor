namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>Cancels repair actions that have not started once the standard is met.</summary>
    internal static void CancelSettledRepairMethods(CancellationToken ct, StoreJournal journal, StandardState goal)
    {
        if (goal.Standard.Finding != Finding.Met)
        {
            return;
        }

        foreach (var method in goal.Methods)
        {
            var plan = journal.LoadPlan(ct, method.Plan);
            if (!StorePackage.PlanOpen(plan))
            {
                continue;
            }

            var repairs = new HashSet<ActionId>();
            foreach (var action in plan.Spec.Actions)
            {
                if (action.TryRepair(out _))
                {
                    repairs.Add(action.Id);
                }
            }

            foreach (var progress in plan.Progress)
            {
                var v = progress.View();
                if (!repairs.Contains(v.Action) || (v.Stage != ProgressStage.Pending && v.Stage != ProgressStage.Prepared))
                {
                    continue;
                }

                journal.Cancel(ct, method.Plan, v.Action);
            }
        }
    }
}
