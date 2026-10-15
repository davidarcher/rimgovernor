namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsCustodySource : IRoundsRescueSource
{
}

public sealed partial class RoundsPopulationCustodyPlanner
{
    /// <summary>Containment upkeep: close doors, enable bioferrite harvest, advance the monolith, leave critical care to its Incident owner.</summary>
    internal async Task<RoundsPopulationCustodyResult> StepContainmentAsync(Player p, ControlState state, DateTimeOffset started, StandardState goal, RoundsFacts facts, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var containment = facts.Containment;
        var research = facts.Research;
        var upkeep = PolicyFunctions.ContainmentDoorUpkeep(containment);
        foreach (var issue in upkeep.Issues)
        {
            BuildingRuntimePackage.DefenseAction("routine-population-custody", TelemetryLevel.Warn, "refused", "containment_upkeep_issue", $"{issue.Cell.X},{issue.Cell.Z}", new Dictionary<string, object?> { ["x"] = issue.Cell.X, ["z"] = issue.Cell.Z, ["detail"] = issue.Reason }, call);
        }

        if (upkeep.CloseDoors.Count > 0)
        {
            var cell = upkeep.CloseDoors[0];
            var door = new DoorControl(cell, false);
            var prefix = $"population-door-{cell.X}-{cell.Z}-";
            var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
            if (!ok)
            {
                return new RoundsPopulationCustodyResult { Verdict = verdict };
            }

            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewDoorControlAction(new ActionId($"{id}-0"), door);
            return Commit(p, state, started, goal, method, id, action, call, epoch);
        }

        var harvest = PolicyFunctions.BioferriteHarvestOwed(containment, research);
        foreach (var reason in harvest.Issues)
        {
            BuildingRuntimePackage.DefenseAction("routine-population-custody", TelemetryLevel.Warn, "refused", "bioferrite_harvest_unread", "bioferrite", new Dictionary<string, object?> { ["detail"] = reason }, call);
        }

        if (harvest.Enable.Count > 0)
        {
            var entity = harvest.Enable[0];
            var prefix = $"population-bioferrite-{entity}-";
            var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
            if (!ok)
            {
                return new RoundsPopulationCustodyResult { Verdict = verdict };
            }

            var setting = PawnSettings.NewExtractBioferriteSetting(entity, true);
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewPawnSettingsAction(new ActionId($"{id}-0"), setting);
            return Commit(p, state, started, goal, method, id, action, call, epoch);
        }

        var (monolith, handled) = await StepMonolithAsync(p, state, started, goal, facts, arbiter, call, epoch).ConfigureAwait(false);
        if (handled)
        {
            return monolith;
        }

        return new RoundsPopulationCustodyResult { Verdict = Verdict.AwaitingPlan("dependent_care_incident_owner",
            "Critical held-entity care belongs to CriticalMedicine; noncritical dependent care requires shared forecast support.") };
    }
}