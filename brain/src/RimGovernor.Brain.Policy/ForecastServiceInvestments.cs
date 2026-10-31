using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Explanation of selected physical service work. Derived from the shared comparison;
/// neither a second demand target nor authorization to execute.</summary>
public sealed record ForecastServiceInvestment(ServiceTarget Target, EquatableArray<string> Beneficiaries,
    EquatableArray<string> Tasks, double HorizonTicks, string Metric, double AvoidedExposure, string Retirement);

public static class ForecastServiceInvestments
{
    /// <summary>The Need a coupled attempt continues under: ended at a service construction boundary, when productive food
    /// demand ended, or when every intervention purpose its orders carry has completed or been cancelled
    /// (<see cref="InterventionPurposes.AtContinuation"/>).</summary>
    public static ActivityNeed ReviewServiceActivityNeed(PawnActivities batch, ActivityNeed needed, ActivityTaskRead read)
        => ServiceConstructionBoundary(batch, read) || ProductiveFoodDemandEnded(batch, read)
            || InterventionPurposes.Ended(batch, p => InterventionPurposes.AtContinuation(p, batch, read.Needs)) != PurposeCondition.Open
            ? ActivityNeed.Ended : needed;

    /// <summary>Reconsider only at a physical work boundary. Fresh needs can retire the supported
    /// ingestion-only investment; missing observations cannot establish demand satisfaction.</summary>
    private static bool ProductiveFoodDemandEnded(PawnActivities batch, ActivityTaskRead read)
    {
        var needs = read.Needs;
        if (read.Context.Tick != needs.Tick.Value
            || batch.Tasks.Any(t => t.Step.Kind is not (PawnActivityKind.Deliver or PawnActivityKind.Eat)
                && !(t.Step.Kind == PawnActivityKind.Work && t.Step.Job == "Harvest"))
            || batch.Orders.Any(o => !o.Release && read.Pawns.FirstOrDefault(p => p.Pawn == o.Pawn)?.Execution?.State is not ("completed" or "holding" or "failed"))) return false;
        var eaters = batch.Tasks.Where(t => t.Step.Kind == PawnActivityKind.Eat).GroupBy(t => t.Pawn).ToArray();
        if (eaters.Length == 0) return false;
        foreach (var eater in eaters)
        {
            var observed = needs.Pawns.SingleOrDefault(p => p.Pawn == eater.Key);
            var horizon = eater.Max(t => t.Step.Deadline) - needs.Tick.Value;
            if (observed is null || horizon <= 0 || horizon > GameTime.TicksPerDay) return false;
            var trajectory = ForecastNeeds.ForecastNeedsTimeline(observed, new ForecastCapacityInput
            {
                Pawn = new() { Id = new(eater.Key), State = ForecastPawnCapacityState.Available },
                Location = "current", LocalHour = observed.LocalHour, HorizonTicks = horizon, ProtectTimetable = false,
                CurrentWork = new(new(eater.Key), false, WorkType.None, new ForecastWorkRemaining.Timed(0d)),
                Occupancy = new ForecastProtection.Read([]), OpportunityCostPerTick = 1d
            }, [], mood: false);
            var food = trajectory.Needs.SingleOrDefault(n => n.Kind == "food");
            if (food?.ThresholdExposureTicks is not ForecastTerm.Assessed(0d)) return false;
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
            && before.Needs.SingleOrDefault(n => n.Kind == "food")?.ThresholdExposureTicks is ForecastTerm.Assessed(var old)
            && p.Value.Needs.SingleOrDefault(n => n.Kind == "food")?.ThresholdExposureTicks is ForecastTerm.Assessed(var next)
            && next < old).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        var foodTasks = Dependencies(context.NeedTasks.Where(n => n.Kind == "food" && food.Contains(n.Pawn)
            && selected.Plan.Allocations.Any(a => a.Activity == n.Task)).Select(n => n.Task));
        if (food.Length != 0 && foodTasks.Count != 0)
            result.Add(new(ServiceTarget.ForNeed(FoodRules.FoodSupplyNeed),
                EquatableArray.From(food), foodTasks, horizon, "food_threshold_ticks",
                food.Sum(p => ((ForecastTerm.Assessed)baseline.Needs[p].Needs.Single(n => n.Kind == "food").ThresholdExposureTicks).Value
                    - ((ForecastTerm.Assessed)selected.Needs[p].Needs.Single(n => n.Kind == "food").ThresholdExposureTicks).Value),
                "fresh_food_trajectory_has_no_shortfall_or_chain_is_no_longer_feasible"));
        if (selected.CompletedConstructionSites.Count != 0 && selected.StationaryThermalRelief is ForecastTerm.Assessed { Value: > 0 } relief)
            result.Add(new(ServiceTarget.ForNeed(ShelterFamily.Temperature, "owner_observed_recovery"),
                EquatableArray.From(selected.ThermalExposure.Where(t => t.AssessedTicks > 0).Select(t => t.Pawn).Distinct().Order(StringComparer.Ordinal)),
                Dependencies(selected.Plan.Allocations.Where(a => context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b)
                    && b.Kind == PawnActivityKind.Work && b.Job is "FinishFrame" or "BuildRoof").Select(a => a.Activity)),
                horizon, "stationary_thermal_degree_ticks", relief.Value,
                "recompare_remaining_physical_work_at_observed_task_boundary"));
        result.AddRange(HostedLoadInvestments(selected, context, horizon, Dependencies, tasks.ContainsKey));
        return EquatableArray.From(result);
    }

    /// <summary>The retirement of a workshop host's investment: its owner's own predicate on fresh facts, never the bench's placement or construction.</summary>
    public const string HostedLoadRetirement = "owner_need_met_on_fresh_facts_never_by_bench_placement";

    /// <summary>
    /// The investments of the selected workshop hosts (RG-HABITAT-005, RG-HABITAT-011): a planned room whose
    /// <see cref="PlannedRoom.Production"/> hosts an unhosted ledger load (a target, no bench kind) and whose host piece's
    /// planned build site the plan starts (an activity of it allocated) serves each owner that declared the load
    /// (<see cref="LedgerView.Owners"/>). The target is that owner's own Need predicate (<c>need_satisfied</c>), so the
    /// bench being placed, accepted or built never satisfies it; the beneficiaries are the hosted recipes, the tasks the
    /// host sites' physical chain, and the metric the hosted runs a day the plan credits. No ledger view, no investment.
    /// </summary>
    private static IEnumerable<ForecastServiceInvestment> HostedLoadInvestments(ForecastCoupledOutcome selected, ForecastCoupledContext context,
        double horizon, Func<IEnumerable<string>, EquatableArray<string>> dependencies, Func<string, bool> known)
    {
        if (context.ProductionOrders is not { } ledger) yield break;
        var allocated = selected.Plan.Allocations.Select(a => a.Activity).ToHashSet(StringComparer.Ordinal);
        var served = new SortedDictionary<string, List<(int Room, PlannedProduction Load, string[] Roots)>>(StringComparer.Ordinal);
        foreach (var (id, room) in context.LayoutRooms.OrderBy(r => r.Key))
            foreach (var load in room.Production)
            {
                var prefix = PlannedRules.PlannedPieceSitePrefix(id, load.Bench);
                var roots = context.PlannedSites.Where(s => s.Room == id && s.Site.StartsWith(prefix, StringComparison.Ordinal))
                    .SelectMany(s => s.Activities.Concat(s.Finishes)).Where(a => allocated.Contains(a) && known(a)).Distinct().ToArray();
                if (roots.Length == 0) continue;
                var owners = ledger.Orders.Where(o => o.Recipe == load.Recipe && o.Target > 0 && o.BenchKind.Length == 0)
                    .SelectMany(o => ledger.Owners.GetValueOrDefault(o.Key()).Items).Distinct(StringComparer.Ordinal);
                foreach (var owner in owners)
                {
                    if (!served.TryGetValue(owner, out var loads)) served[owner] = loads = [];
                    loads.Add((id, load, roots));
                }
            }
        foreach (var (owner, loads) in served)
        {
            var chain = dependencies(loads.SelectMany(l => l.Roots));
            if (chain.Count == 0) continue;
            yield return new(new ServiceTarget(new FilingKey(owner), "need_satisfied"),
                EquatableArray.From(loads.Select(l => l.Load.Recipe).Distinct().Order(StringComparer.Ordinal)), chain, horizon,
                "hosted_runs_per_day", loads.Sum(l => l.Load.RunsPerDay), HostedLoadRetirement);
        }
    }

    /// <summary>Construction investment is conditional on the remaining physical chain. At a safe
    /// completed-work boundary, release and let ordinary review compare fresh service consequences.
    /// This is reconsideration, never an assertion that shelter is satisfied or permission to cancel it. The boundary is a
    /// finished site (its FinishFrame or BuildRoof ended): a frame's ended Supply is not one, since the material is in the frame
    /// and its finish, or the carry's retry, is the only use of it, so the binding goes on (#3537).</summary>
    private static bool ServiceConstructionBoundary(PawnActivities batch, ActivityTaskRead read)
        => batch.Orders.Any(PawnActivities.Intervention)
            && batch.Tasks.Any(t => t.SiteDefinition.Length != 0 || t.Step.Job == "BuildRoof")
            && !read.Truncated
            && batch.Orders.Where(o => !o.Release).All(o => read.Pawns.SingleOrDefault(p => p.Pawn == o.Pawn)?.Execution?.State
                is "completed" or "failed" || o.Preparing && read.Pawns.SingleOrDefault(p => p.Pawn == o.Pawn)?.Execution?.State == "holding")
            && batch.Orders.Any(o => !o.Release && !o.Preparing
                && read.Pawns.SingleOrDefault(p => p.Pawn == o.Pawn)?.Execution?.State is "completed" or "failed")
            && batch.Orders.Where(o => !o.Release && !o.Preparing && read.Pawns.SingleOrDefault(p => p.Pawn == o.Pawn)?.Execution?.State is "completed" or "failed")
                .All(o => batch.Tasks.FirstOrDefault(t => t.Id == o.Task) is not { Step.Kind: PawnActivityKind.Supply });
}
