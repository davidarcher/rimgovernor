using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>One hand-placed activity: who does it and the physical times. Nothing here is derived.</summary>
internal sealed record ReferenceStep(string Activity, string Worker, double Depart, double Arrive, double Start, double End);

/// <summary>Violations are stable codes ("code:detail"); Served names each hungry (pawn, event) relieved in its window.</summary>
internal sealed record ReferenceCheck(IReadOnlyList<string> Violations, IReadOnlySet<string> Served, double CompletedAt);

/// <summary>
/// Independent validator for a hand-built schedule. It reads only the fixture's facts and data records (activities,
/// options, routes, capacities, equipment, lots, needs) and re-derives each constraint with plain arithmetic: it neither
/// searches nor calls any production scheduler, so a schedule it accepts is feasible by construction rather than by the
/// incumbent's code. Feasibility says nothing about optimality.
/// </summary>
internal static class ReferenceScheduleChecker
{
    private const double Eps = 1e-9;

    public static ReferenceCheck Check(ForecastCoupledContext context, IReadOnlyList<ReferenceStep> steps)
    {
        var bad = new List<string>();
        var problem = context.Activities;
        var tasks = problem.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var options = problem.Options.ToDictionary(o => (o.Activity, o.Pawn));
        var pawns = problem.Pawns.ToDictionary(p => p.Pawn, StringComparer.Ordinal);
        var routes = problem.Routes.ToDictionary(r => (r.Pawn, r.From, r.To), r => r.Ticks);
        var equipment = problem.Equipment.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var deadline = problem.Deadline.Value;
        var byActivity = new Dictionary<string, ReferenceStep>(StringComparer.Ordinal);
        foreach (var step in steps)
            if (!byActivity.TryAdd(step.Activity, step)) bad.Add("duplicate_activity:" + step.Activity);
        var known = steps.Where(s => tasks.ContainsKey(s.Activity) && options.ContainsKey((s.Activity, s.Worker)) && pawns.ContainsKey(s.Worker)).ToArray();
        foreach (var step in steps.Except(known)) bad.Add("unknown_activity_or_worker:" + step.Activity + ":" + step.Worker);

        // Alternative realizations of one physical operation cannot both run.
        foreach (var group in known.GroupBy(s => tasks[s.Activity].ChoiceIdentity).Where(g => g.Key.Length != 0 && g.Count() > 1))
            bad.Add("choice_identity_repeated:" + group.Key);

        // Each worker is one body: travel, waiting and service occupy [Depart, End] and never overlap.
        foreach (var worker in known.GroupBy(s => s.Worker))
        {
            var ordered = worker.OrderBy(s => s.Depart).ThenBy(s => s.End).ToArray();
            var windows = pawns[worker.Key].Windows.TryGetValue(out var w) ? w : default;
            string origin = "start";
            ReferenceStep? previous = null;
            var skillRecipes = new List<ReferenceStep>();
            foreach (var step in ordered)
            {
                var task = tasks[step.Activity];
                var option = options[(step.Activity, step.Worker)];
                if (previous is not null && step.Depart < previous.End - Eps)
                    bad.Add($"worker_overlap:{step.Worker}:{previous.Activity}:{step.Activity}");
                if (!windows.Any(x => step.Depart >= x.Start - Eps && step.End <= x.End + Eps))
                    bad.Add($"worker_unavailable:{step.Worker}:{step.Activity}");
                double travel;
                if (origin == task.Location) travel = 0;
                else if (routes.TryGetValue((step.Worker, origin, task.Location), out var route) && route.TryGetValue(out var ticks)) travel = ticks;
                else { bad.Add($"unknown_route:{step.Worker}:{origin}:{task.Location}"); travel = double.NaN; }
                if (!double.IsNaN(travel) && Math.Abs(step.Arrive - (step.Depart + travel)) > Eps) bad.Add($"travel_time:{step.Activity}");
                if (step.Start < step.Arrive - Eps) bad.Add($"starts_before_arrival:{step.Activity}");
                // Observed per-worker rates (91/105/110, 1-tick carry and 10-tick ingestion) are exact; a developing cook is separate.
                if (option.SkillWork is null)
                {
                    if (!option.WorkTicks.TryGetValue(out var work) || Math.Abs(step.End - step.Start - work) > Eps) bad.Add($"work_rate:{step.Activity}:{step.Worker}");
                }
                else skillRecipes.Add(step);
                origin = task.EndLocation;
                previous = step;
            }
            CheckDevelopingCook(worker.Key, skillRecipes, options, bad);
        }

        // Dependencies gate service. Earliest times and latest bounds come from the activity records.
        foreach (var step in known)
        {
            var task = tasks[step.Activity];
            if (step.Start < task.NotBefore - Eps) bad.Add($"before_earliest:{step.Activity}");
            if (task.LatestStart is { } latestStart && step.Start > latestStart + Eps) bad.Add($"after_latest_start:{step.Activity}");
            if (task.LatestFinish is { } latestFinish && step.End > latestFinish + Eps) bad.Add($"after_latest_finish:{step.Activity}");
            if (step.End > deadline + Eps) bad.Add($"after_horizon:{step.Activity}");
            foreach (var prerequisite in task.Prerequisites)
            {
                if (!byActivity.TryGetValue(prerequisite, out var before)) bad.Add($"prerequisite_missing:{step.Activity}:{prerequisite}");
                else if (step.Start < before.End - Eps) bad.Add($"prerequisite_unfinished:{step.Activity}:{prerequisite}");
            }
        }

        // Exclusive equipment (the single stove) is held for the whole service and never double-booked.
        foreach (var piece in known.SelectMany(s => options[(s.Activity, s.Worker)].Equipment.Select(id => (Id: id, Step: s))).GroupBy(x => x.Id))
        {
            if (!equipment.TryGetValue(piece.Key, out var capacity) || !capacity.Windows.TryGetValue(out var open)) { bad.Add("unknown_equipment:" + piece.Key); continue; }
            var ordered = piece.Select(x => x.Step).OrderBy(s => s.Start).ToArray();
            foreach (var step in ordered)
            {
                var task = tasks[step.Activity];
                if ((task.EquipmentLocation.Length == 0 ? task.Location : task.EquipmentLocation) != capacity.Location) bad.Add($"equipment_location:{step.Activity}");
                if (!open.Any(x => step.Start >= x.Start - Eps && step.End <= x.End + Eps)) bad.Add($"equipment_unavailable:{piece.Key}:{step.Activity}");
            }
            for (var i = 1; i < ordered.Length; i++)
                for (var j = 0; j < i; j++)
                    if (ordered[i].Start < ordered[j].End - Eps && ordered[j].Start < ordered[i].End - Eps)
                        bad.Add($"stove_overlap:{piece.Key}:{ordered[j].Activity}:{ordered[i].Activity}");
        }

        CheckConservation(context, known, tasks, bad);
        var served = CheckIngestion(context, known, tasks, options, bad);
        return new(bad, served, known.Length == 0 ? 0 : known.Max(s => s.End));
    }

    /// <summary>A developing cook works at the stored level's rate and rises only after completed recipes pay enough experience.</summary>
    private static void CheckDevelopingCook(string worker, List<ReferenceStep> recipes,
        Dictionary<(string, string), ForecastActivityOption> options, List<string> bad)
    {
        var level = 0; var xp = 0d; var first = true;
        foreach (var step in recipes.OrderBy(s => s.Start))
        {
            var work = options[(step.Activity, worker)].SkillWork!;
            if (first)
            {
                level = work.Learning.StoredLevel.Value; xp = work.Learning.Xp.Value; first = false;
            }
            var levels = work.Learning.Levels.ToDictionary(l => l.Level, l => l.RequiredXp);
            while (levels.TryGetValue(level, out var required) && levels.ContainsKey(level + 1) && xp >= required) { xp -= required; level++; }
            var speed = work.Rates.Single(r => r.StoredLevel == level).WorkPerTick.Value;
            var expected = work.WorkAmount.Value / speed;
            if (Math.Abs(step.End - step.Start - expected) > Eps) bad.Add($"learning_rate:{step.Activity}:{worker}:expected={expected}");
            xp += work.Recipe.RawXpPerWorkTick.Value * (step.End - step.Start) * work.Learning.DirectFactor.Value;
        }
    }

    /// <summary>Lot balances move at service boundaries: inputs leave at start, outputs arrive at end; none may go negative.</summary>
    private static void CheckConservation(ForecastCoupledContext context, ReferenceStep[] steps,
        Dictionary<string, ForecastActivity> tasks, List<string> bad)
    {
        var balance = new Dictionary<(Resource, string), double>();
        foreach (var lot in context.InitialLots)
            if (lot.Count.TryGetValue(out var count)) balance[(lot.Resource, lot.Lot)] = balance.GetValueOrDefault((lot.Resource, lot.Lot)) + count;
        var events = new List<(double At, int Order, Resource Resource, string Lot, double Units, string Activity)>();
        foreach (var step in steps)
        {
            var task = tasks[step.Activity];
            foreach (var input in task.Inputs)
                if (input.Units.TryGetValue(out var u)) events.Add((step.Start, 1, input.Resource, input.Lot, -u, step.Activity));
                else bad.Add("input_unknown:" + step.Activity);
            foreach (var output in task.Outputs)
                if (output.Units.TryGetValue(out var u)) events.Add((step.End, 0, output.Resource, output.Lot, u, step.Activity));
        }
        // Outputs landing at an instant are available to inputs starting at that instant.
        foreach (var e in events.OrderBy(e => e.At).ThenBy(e => e.Order))
        {
            var key = (e.Resource, e.Lot);
            balance[key] = balance.GetValueOrDefault(key) + e.Units;
            if (balance[key] < -Eps) bad.Add($"conservation:{e.Lot}:{e.Activity}@{e.At}");
        }
    }

    /// <summary>
    /// A hungry pawn's food crosses its threshold at (level - threshold) / fall; each ingestion restores the meal's
    /// nutrition, so later events recur every nutrition / fall. An event is relieved when its ingestion ends by that
    /// tick and starts no earlier than the activity's own earliest time (the threshold tick less the eating duration).
    /// </summary>
    private static HashSet<string> CheckIngestion(ForecastCoupledContext context, ReferenceStep[] steps,
        Dictionary<string, ForecastActivity> tasks, Dictionary<(string, string), ForecastActivityOption> options, List<string> bad)
    {
        var served = new HashSet<string>(StringComparer.Ordinal);
        var horizon = context.Activities.Deadline.Value;
        foreach (var pawn in context.Needs.Pawns)
        {
            var food = pawn.Needs.FirstOrDefault(n => n.Kind == "food");
            if (food is null || !food.Level.TryGetValue(out var level) || !food.FallPerTick.TryGetValue(out var fall)
                || !food.Threshold.TryGetValue(out var threshold) || fall <= 0) continue;
            var meals = context.NeedTasks.Where(n => n.Pawn == pawn.Pawn && n.Kind == "food").ToArray();
            if (meals.Length == 0 || !meals[0].Gain.TryGetValue(out var nutrition)) continue;
            var due = new List<double>();
            for (var t = (level - threshold) / fall; t <= horizon + Eps; t += nutrition / fall) due.Add(t);
            foreach (var step in steps.Where(s => s.Worker == pawn.Pawn && meals.Any(m => m.Task == s.Activity)))
            {
                var task = tasks[step.Activity];
                var work = options[(step.Activity, step.Worker)].WorkTicks.Value;
                var index = due.FindIndex(t => Math.Abs(task.NotBefore + work - t) < Eps);
                if (index < 0) { bad.Add("ingestion_not_a_demand_event:" + step.Activity); continue; }
                if (step.End > due[index] + Eps) { bad.Add($"ingestion_late:{step.Activity}"); continue; }
                if (!served.Add(pawn.Pawn + "#" + index)) bad.Add($"ingestion_event_repeated:{pawn.Pawn}#{index}");
            }
        }
        return served;
    }

    /// <summary>The hungry consumers' demand events, computed exactly as the ingestion check does, for the doc and the count.</summary>
    public static IReadOnlyList<(string Pawn, int Index, double DueTick)> DemandEvents(ForecastCoupledContext context)
    {
        var events = new List<(string, int, double)>();
        var horizon = context.Activities.Deadline.Value;
        foreach (var pawn in context.Needs.Pawns)
        {
            var food = pawn.Needs.FirstOrDefault(n => n.Kind == "food");
            var meal = context.NeedTasks.FirstOrDefault(n => n.Pawn == pawn.Pawn && n.Kind == "food");
            if (food is null || meal is null || !food.Level.TryGetValue(out var level) || !food.FallPerTick.TryGetValue(out var fall)
                || !food.Threshold.TryGetValue(out var threshold) || fall <= 0 || !meal.Gain.TryGetValue(out var nutrition)) continue;
            var index = 0;
            for (var t = (level - threshold) / fall; t <= horizon + Eps; t += nutrition / fall) events.Add((pawn.Pawn, index++, t));
        }
        return events;
    }
}
