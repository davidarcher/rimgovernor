namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>The incident bound to a concern kind and the situation it names, or not bound.</summary>
    internal static (IncidentState State, Situation Need, bool Bound) RoundsIncident(StoreJournal journal, Rounds review, ConcernId kind, CancellationToken call)
    {
        var binding = review.Incident(kind);
        if (binding is null)
        {
            return (default!, Situation.Unclear, false);
        }

        var state = journal.LoadIncident(binding.Incident, call);
        return (state, binding.Situation, true);
    }

    internal static (IncidentState State, bool Deficit) IncidentDeficit(StoreJournal journal, Rounds review, ConcernId kind, CancellationToken call)
    {
        var (state, need, ok) = RoundsIncident(journal, review, kind, call);
        if (!ok || need != Situation.Active || review.VetoIncident(state.Incident).Length != 0)
        {
            return (default!, false);
        }

        return (state, true);
    }

    internal static bool IncidentOpenWork(StoreJournal journal, IncidentState state, CancellationToken call)
    {
        foreach (var method in state.Methods)
        {
            var plan = journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                return true;
            }
        }

        return false;
    }
}
