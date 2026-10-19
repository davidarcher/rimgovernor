using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Explanation of selected physical service work. Derived from the shared comparison;
/// neither a second demand target nor authorization to execute.</summary>
public sealed record ForecastServiceInvestment(ServiceTarget Target, EquatableArray<string> Beneficiaries,
    EquatableArray<string> Tasks, double HorizonTicks, string Metric, Fact<double> AvoidedExposure, string Retirement);

public static class ForecastServiceInvestments
{
    public static Fact<bool> ReviewServiceActivityNeed(PawnActivities batch, Fact<bool> needed, ActivityTaskRead read)
        => ForecastMaintenancePolicy.PracticeMandateEnded(batch, read) || read.Known && read.Context is not null && (ServiceConstructionBoundary(batch, read) || ProductiveFoodDemandEnded(batch, read)) ? Known(false) : needed;

    /// <summary>Reconsider only at a physical work boundary. Fresh needs can retire the supported
    /// ingestion-only investment; missing observations cannot establish demand satisfaction.</summary>
    private static bool ProductiveFoodDemandEnded(PawnActivities batch, ActivityTaskRead read)
    {
        if (read.Needs is not { } needs
            || read.Context?.Tick != needs.Tick
            || batch.Tasks.Any(t => t.Step.Kind is not (PawnActivityKind.Recipe or PawnActivityKind.Deliver or PawnActivityKind.Eat)
                && !(t.Step.Kind == PawnActivityKind.Work && t.Step.Job == "Harvest"))
            || batch.Orders.Any(o => !o.Release && read.Pawns.FirstOrDefault(p => p.Pawn == o.Pawn)?.Execution?.State is not ("completed" or "holding" or "failed"))) return false;
        var eaters = batch.Tasks.Where(t => t.Step.Kind == PawnActivityKind.Eat).GroupBy(t => t.Pawn).ToArray();
        if (eaters.Length == 0) return false;
        foreach (var eater in eaters)
        {
            var observed = needs.Pawns.SingleOrDefault(p => p.Pawn == eater.Key);
            var horizon = eater.Max(t => t.Step.Deadline) - needs.Tick;
            if (observed is null || horizon <= 0 || horizon > GameTime.TicksPerDay) return false;
            var trajectory = ForecastNeeds.ForecastNeedsTimeline(observed, new ForecastCapacityInput
            {
                Pawn = new() { Id = new(eater.Key), Available = Known(true), Applies = Known(true) },
                Location = "current", LocalHour = observed.LocalHour, HorizonTicks = horizon, ProtectTimetable = false,
                CurrentWork = new(new(eater.Key), Known(false), default, Known(0d)),
                Occupancy = Known<EquatableArray<ForecastOccupancy>>([]), OpportunityCostPerTick = Known(1d)
            }, [], mood: false);
            var food = trajectory.Needs.SingleOrDefault(n => n.Kind == "food");
            if (food?.ThresholdExposureTicks != Known(0d)) return false;
        }
        return true;
    }

    internal static EquatableArray<ForecastServiceInvestment> ServiceInvestmentsOf(ForecastCoupledOutcome baseline,
        ForecastCoupledOutcome selected, ForecastCoupledContext context, double horizon)
    {
        var result = new List<ForecastServiceInvestment>();
        var physical = selected.Plan.Allocations.Where(a => context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b)
            && b.Kind is PawnActivityKind.Work or PawnActivityKind.Recipe or PawnActivityKind.Deliver or PawnActivityKind.Supply)
            .Select(a => a.Activity).ToArray();
        if (physical.Length == 0) return [];
        var tasks = context.Activities.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        EquatableArray<string> Dependencies(IEnumerable<string> roots)
        {
            var included = new HashSet<string>(StringComparer.Ordinal);
            void Include(string id)
            {
                if (!included.Add(id)) return;
                foreach (var prerequisite in tasks[id].Prerequisites) Include(prerequisite);
            }
            foreach (var root in roots) Include(root);
            return EquatableArray.From(physical.Where(included.Contains));
        }
        var food = selected.Needs.Where(p => baseline.Needs.TryGetValue(p.Key, out var before)
            && before.Needs.SingleOrDefault(n => n.Kind == "food")?.ThresholdExposureTicks is { IsKnown: true } old
            && p.Value.Needs.SingleOrDefault(n => n.Kind == "food")?.ThresholdExposureTicks is { IsKnown: true } next
            && next.Value < old.Value).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        var foodTasks = Dependencies(context.NeedTasks.Where(n => n.Kind == "food" && food.Contains(n.Pawn)
            && selected.Plan.Allocations.Any(a => a.Activity == n.Task)).Select(n => n.Task));
        if (food.Length != 0 && foodTasks.Count != 0)
            result.Add(new(ServiceTarget.ForNeed(FoodRules.FoodSupplyNeed, horizonDays: Known(horizon / GameTime.TicksPerDay)),
                EquatableArray.From(food), foodTasks, horizon, "food_threshold_ticks",
                Known(food.Sum(p => baseline.Needs[p].Needs.Single(n => n.Kind == "food").ThresholdExposureTicks.Value
                    - selected.Needs[p].Needs.Single(n => n.Kind == "food").ThresholdExposureTicks.Value)),
                "fresh_food_trajectory_has_no_shortfall_or_chain_is_no_longer_feasible"));
        if (selected.CompletedDevelopmentSites.Count != 0 && selected.StationaryThermalRelief.TryGetValue(out var relief) && relief > 0)
            result.Add(new(ServiceTarget.ForNeed(ShelterFamily.Temperature, "", "owner_observed_recovery", Known(horizon / GameTime.TicksPerDay)),
                EquatableArray.From(selected.ThermalExposure.Where(t => t.AssessedTicks > 0).Select(t => t.Pawn).Distinct().Order(StringComparer.Ordinal)),
                Dependencies(selected.Plan.Allocations.Where(a => context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b)
                    && b.Kind == PawnActivityKind.Work && b.Job is "FinishFrame" or "BuildRoof").Select(a => a.Activity)),
                horizon, "stationary_thermal_degree_ticks", Known(relief),
                "recompare_remaining_physical_work_at_observed_task_boundary"));
        return EquatableArray.From(result);
    }

    /// <summary>Construction investment is conditional on the remaining physical chain. At a safe
    /// completed-work boundary, release and let ordinary review compare fresh service consequences.
    /// This is reconsideration, never an assertion that shelter is satisfied or permission to cancel it.</summary>
    private static bool ServiceConstructionBoundary(PawnActivities batch, ActivityTaskRead read)
        => batch.Orders.Any(o => o.Method.StartsWith("coupled-", StringComparison.Ordinal))
            && batch.Tasks.Any(t => t.SiteDefinition.Length != 0 || t.Step.Job == "BuildRoof")
            && !read.Truncated
            && batch.Orders.Where(o => !o.Release).All(o => read.Pawns.SingleOrDefault(p => p.Pawn == o.Pawn)?.Execution?.State
                is "completed" or "failed" || o.Preparing && read.Pawns.SingleOrDefault(p => p.Pawn == o.Pawn)?.Execution?.State == "holding")
            && batch.Orders.Any(o => !o.Release && !o.Preparing
                && read.Pawns.SingleOrDefault(p => p.Pawn == o.Pawn)?.Execution?.State is "completed" or "failed");
}
