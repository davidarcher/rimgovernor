namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>The incident bound to a concern kind and the situation it names, or not bound.</summary>
    internal static (IncidentState State, Situation Need, bool Bound) RoundsIncident(CancellationToken call, Store.Store journal, Rounds review, ConcernId kind)
    {
        var (binding, bound) = review.Incident(kind);
        if (!bound)
        {
            return (default!, Situation.None, false);
        }

        var state = journal.LoadIncident(call, binding.Incident);
        return (state, binding.Situation, true);
    }

    internal static (IncidentState State, bool Deficit) IncidentDeficit(CancellationToken call, Store.Store journal, Rounds review, ConcernId kind)
    {
        var (state, need, ok) = RoundsIncident(call, journal, review, kind);
        if (!ok || need != Situation.Active || review.VetoIncident(state.Incident).Length != 0)
        {
            return (default!, false);
        }

        return (state, true);
    }

    internal static bool IncidentOpenWork(CancellationToken call, Store.Store journal, IncidentState state)
    {
        foreach (var method in state.Methods)
        {
            var plan = journal.LoadPlan(call, method.Plan);
            if (StorePackage.PlanOpen(plan))
            {
                return true;
            }
        }

        return false;
    }
}
