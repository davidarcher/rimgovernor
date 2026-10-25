using System.Security.Cryptography;
using System.Text;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Evidence extraction only: reads the selected result and actual native observations, never forecasts again.</summary>
public static class ForecastLiveEvidence
{
    public static IEnumerable<ForecastPrediction> Predictions(string run, string load, string map, string review,
        long tick, ForecastCoupledSelection selection, ForecastCoupledContext context,
        ForecastAdmission admission, IReadOnlyList<PawnActivities> batches, string proposalMethod)
    {
        var target = checked(tick + (long)Math.Ceiling(selection.Horizon));
        ForecastPrediction Row(ForecastMetric metric, string subject, string scope, string unit, EvidenceValue value,
            string method = "", string task = "", long revision = 0, ForecastAdmission? status = null) =>
            new(new(run, load, map, review, $"{metric}/{subject}/{task}", metric, subject, scope, unit, method, task, revision),
                tick, target, value, status ?? admission);
        if (selection.Selected.Terminal is { } terminal)
        {
            var resources = terminal.Request.Facts.Food.Stocks.Select(s => s.DefName)
                .Concat(terminal.Lots.Where(l => l.FoodEligible is not FoodEligibility.Ineligible).Select(l => l.Resource)).Where(r => !string.IsNullOrEmpty(r.Value)).Distinct().OrderBy(r => r.Value, StringComparer.Ordinal).ToArray();
            if (resources.Length == 0 && terminal.FoodUnread)
                yield return Row(ForecastMetric.FoodStock, "unavailable", ForecastStockScope.Food, "items", Unavailable("food_stock_census_unknown"));
            foreach (var resource in resources)
                yield return Row(ForecastMetric.FoodStock, resource.Value, ForecastStockScope.Food, "items",
                    terminal.FoodUnread ? Unavailable("food_stock_census_or_lot_unknown") : Of(ForecastStockScope.Count(terminal.Lots, resource), "food_stock_census_or_lot_unknown"));
        }
        else yield return Row(ForecastMetric.FoodStock, "unavailable", ForecastStockScope.Food, "items", Unavailable("food_stock_prediction_unavailable"));
        foreach (var pawn in context.Needs.Pawns)
        {
            selection.Selected.Needs.TryGetValue(pawn.Pawn, out var needs);
            foreach (var (kind, metric) in new[] { ("food", ForecastMetric.FoodLevel), ("rest", ForecastMetric.RestLevel) })
                yield return Row(metric, pawn.Pawn, "pawn_need", "level", Of(needs?.Needs.FirstOrDefault(n => n.Kind == kind)?.FinalLevel ?? default, "need_prediction_unavailable"));
            yield return Row(ForecastMetric.Mood, pawn.Pawn, "pawn_mood", "level", needs?.Mood is ForecastMoodResult.Projected projected ? new EvidenceValue.Valued(projected.TerminalMood) : Unavailable("mood_prediction_unavailable"));
        }
        foreach (var shelter in selection.Selected.Shelters)
        {
            var geometry = context.Shelters.FirstOrDefault(s => s.Room == shelter.Room);
            yield return Row(ForecastMetric.ShelterTemperature, shelter.Room.ToString(),
                geometry is null ? "geometry_unavailable" : Geometry(geometry.Interior), "celsius",
                shelter is ForecastShelterOutcome.Projected projected ? new EvidenceValue.Valued(projected.TemperatureAtHorizon) : Unavailable("temperature_prediction_unavailable"));
        }
        foreach (var allocation in selection.Selected.Plan.Allocations)
        {
            var batch = batches.FirstOrDefault(b => b.Tasks.Any(t => t.Id == allocation.Activity && t.Pawn == allocation.Pawn));
            var order = batch?.Orders.FirstOrDefault(o => o.Pawn == allocation.Pawn && !o.Release);
            yield return Row(ForecastMetric.TaskCompletion, allocation.Pawn, "selected_activity", "ticks",
                new EvidenceValue.Valued(tick + allocation.End), order?.Method ?? "", allocation.Activity, order?.Revision ?? 0,
                order is null ? ForecastAdmission.Selected : order.Method != proposalMethod ? ForecastAdmission.Continued : admission);
        }
    }

    private static string Geometry(IEnumerable<Cell> cells) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join(";", cells.OrderBy(c => c.X).ThenBy(c => c.Z).Select(c => $"{c.X},{c.Z}")))));

    private static EvidenceValue.Unavailable Unavailable(string reason) => new(reason);

    /// <summary>A forecast value that the forecast records as a fact (needs, mood and shelter timelines, lot counts).</summary>
    private static EvidenceValue Of(Fact<double> value, string reason) =>
        value.TryGetValue(out var known) ? new EvidenceValue.Valued(known) : Unavailable(reason);

    public static ForecastObservation Observe(ForecastPrediction prediction, long tick, NeedsForecastRead? needs,
        ForecastCoupledContext? context, ForecastExecution execution, FoodSupply? food = null)
    {
        var key = prediction.Key;
        double? observed = null;
        var reason = "observation_unavailable";
        if (key.Metric is ForecastMetric.FoodLevel or ForecastMetric.RestLevel or ForecastMetric.Mood)
        {
            var pawn = needs?.Tick.Value == tick ? needs.Pawns.FirstOrDefault(p => p.Pawn == key.Subject) : null;
            observed = key.Metric == ForecastMetric.Mood ? pawn?.Mood?.Level
                : pawn?.Needs.FirstOrDefault(n => n.Kind == (key.Metric == ForecastMetric.FoodLevel ? "food" : "rest"))?.Level;
            reason = needs?.Tick.Value != tick ? "fresh_needs_unavailable" : pawn is null ? "pawn_unobserved" : "need_value_unknown";
        }
        else if (key.Metric == ForecastMetric.ShelterTemperature)
        {
            var room = context?.Needs.Tick.Value == tick ? context.Shelters.FirstOrDefault(s => Geometry(s.Interior) == key.Scope) : null;
            reason = room is null ? "shelter_geometry_unobserved" : "temperature_coverage_incomplete";
            if (room is not null && room.Interior.Count > 0 && context?.Thermal is { } thermal)
            {
                var temperatures = room.Interior.Select(c => thermal.Cells.FirstOrDefault(s => s.Cell == c)?.Temperature).ToArray();
                if (temperatures.All(t => t is not null) && temperatures.Distinct().Count() == 1)
                    observed = temperatures[0];
            }
        }
        else if (key.Metric == ForecastMetric.FoodStock)
        {
            reason = key.Scope != ForecastStockScope.Food ? "food_stock_scope_mismatch" : "fresh_food_census_unavailable";
            if (key.Scope == ForecastStockScope.Food && food is not null) observed = ForecastStockScope.Count(food, new(key.Subject));
        }
        return new(key, tick, observed is { } seen ? new EvidenceValue.Valued(seen) : Unavailable(reason), execution,
            new ForecastCompletion.NotApplicable(), "rounds_native_observation");
    }

    public static ForecastExecution ExecutionOf(ForecastPrediction prediction, ActivityTaskRead read, bool seen)
    {
        if (read.Truncated || read.Context.Tick < prediction.OriginTick)
            return ForecastExecution.Unconfirmed;
        var native = read.Pawns.FirstOrDefault(p => p.Pawn == prediction.Key.Subject)?.Execution;
        if (native?.Method == prediction.Key.Method && native.Revision >= prediction.Key.Revision
            && native.Tick >= prediction.OriginTick && native.State is "failed" or "released") return ForecastExecution.Interrupted;
        return seen && native is not null && native.Method != prediction.Key.Method
            ? ForecastExecution.Replaced : ForecastExecution.Unconfirmed;
    }
    public static ForecastObservation Gap(ForecastPrediction prediction, long tick, string reason, ForecastExecution execution) =>
        new(prediction.Key, tick, Unavailable(reason), execution,
            prediction.Key.Metric == ForecastMetric.TaskCompletion ? new ForecastCompletion.Unobserved() : new ForecastCompletion.NotApplicable(),
            "pending_evidence_retirement");

    /// <summary>A completed native order is not always the entire task (carry batches, prerequisites, observed predicates).</summary>
    public static ForecastObservation? TaskObservation(ForecastPrediction prediction, PawnActivities batch, ActivityTaskRead read,
        long? unfinishedTick, bool consistentStart, ForecastExecution prior, out bool unfinished)
    {
        unfinished = false;
        if (read.Truncated || read.Context.Tick is var tick && tick < prediction.OriginTick) return null;
        var task = batch.Tasks.FirstOrDefault(t => t.Id == prediction.Key.Task && t.Pawn == prediction.Key.Subject);
        var order = batch.Orders.FirstOrDefault(o => o.Pawn == prediction.Key.Subject);
        var native = read.Pawns.FirstOrDefault(p => p.Pawn == prediction.Key.Subject)?.Execution;
        if (task is null || order is null || order.Method != prediction.Key.Method) return null;
        var execution = prior;
        // A revision can legitimately advance through the task's preparation. We do not claim exact-revision consistency.
        var matching = native is not null && native.Method == prediction.Key.Method && native.Assignment == order.Assignment && native.Revision == order.Revision;
        var satisfied = read.SatisfiedTasks.Contains(task.Id);
        var completed = satisfied || matching && order.Task == task.Id && !order.Preparing && native!.State == "completed"
            && !task.CompletionMustBeObserved && !task.Step.IsSet && task.Step.Kind is not PawnActivityKind.Supply and not PawnActivityKind.Deliver;
        unfinished = !completed && matching && order.Task == task.Id && !order.Preparing && native!.State is "started" or "offered" or "installed";
        if (!completed && execution == ForecastExecution.Unconfirmed) return null;
        if (completed && unfinishedTick.HasValue && consistentStart && matching && !order.Preparing && order.Task == task.Id
            && native!.Revision == prediction.Key.Revision && execution == ForecastExecution.Unconfirmed) execution = ForecastExecution.Consistent;
        ForecastCompletion completion = !completed ? new ForecastCompletion.Unobserved()
            : unfinishedTick is { } lo && lo < tick ? new ForecastCompletion.Interval(lo, tick) : new ForecastCompletion.CompletedBy(tick);
        return new(prediction.Key, tick,
            Unavailable(completed ? unfinishedTick.HasValue ? "interval_completion" : "completion_lower_bound_unavailable" : "execution_diverged"),
            execution, completion, "native_task_read");
    }
}