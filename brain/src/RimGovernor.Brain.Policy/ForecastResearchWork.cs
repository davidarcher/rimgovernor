namespace RimGovernor.Brain.Policy;

/// <summary>Shared project completion stops every worker's callbacks before their later work is priced.</summary>
internal static class ForecastResearchWork
{
    internal static ForecastActivityPlan? Complete(ForecastActivityProblem problem, ForecastActivityAllocation[] proposed, ISet<string> findings)
    {
        var tasks = problem.Activities.ToDictionary(t => t.Id);
        var options = problem.Options.ToDictionary(o => (o.Activity, o.Pawn));
        var limits = new Dictionary<string, (double CallbackCut, double Finish, double Units)>(StringComparer.Ordinal);
        foreach (var group in proposed.Where(a => tasks[a.Activity].ProjectProgress is { } p
            && !p.Project.StartsWith(ForecastProjectProgress.SowPrefix, StringComparison.Ordinal))
            .GroupBy(a => tasks[a.Activity].ProjectProgress!.Project))
        {
            var rows = group.ToArray();
            var amounts = rows.Select(a => options[(a.Activity, a.Pawn)].SkillWork?.WorkAmount).ToArray();
            if (amounts.Any(a => a is null) || rows.Any(a => a.Learning == null)) continue;
            var needed = amounts[0]!.Value;
            if (amounts.Any(a => Math.Abs(a!.Value - needed) > 1e-9)) return null;
            double Produced(int tick) => rows.Sum(a => a.Learning!.WorkSegments.Sum(s =>
                Math.Max(0, Math.Floor((Math.Min(tick, s.End) - s.Start) / (double)s.CallbackInterval)) * s.CallbackInterval * s.WorkPerTick));
            var high = (int)Math.Ceiling(rows.Max(a => a.End));
            if (Produced(high) + 1e-9 < needed) continue;
            var low = 0;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (Produced(mid) + 1e-9 >= needed) high = mid; else low = mid + 1;
            }
            double At(ForecastActivityAllocation a, int tick) => a.Learning!.WorkSegments.Sum(s =>
                Math.Max(0, Math.Floor((Math.Min(tick, s.End) - s.Start) / (double)s.CallbackInterval)) * s.CallbackInterval * s.WorkPerTick);
            var final = rows.Where(a => At(a, low) > At(a, low - 1) + 1e-9).ToArray();
            if (final.Length > 1 && final.Select(a => options[(a.Activity, a.Pawn)].SkillWork!.CallbackOrder).Distinct().Count() != final.Length)
            { findings.Add("research_callback_order_tied:" + group.Key); return null; }
            var remaining = needed - rows.Sum(a => At(a, low - 1));
            foreach (var row in rows)
                limits[row.Activity] = (low, Math.Min(row.End, Math.Max(row.Start, low)), At(row, low - 1));
            foreach (var row in final.OrderBy(a => options[(a.Activity, a.Pawn)].SkillWork!.CallbackOrder))
            {
                var before = At(row, low - 1);
                var useful = Math.Min(Math.Max(0, remaining), At(row, low) - before);
                limits[row.Activity] = (remaining > 1e-9 ? low : low - 1, Math.Min(row.End, Math.Max(row.Start, low)), before + useful);
                remaining -= useful;
            }
        }
        if (limits.Count == 0) return new(proposed, proposed.Max(a => a.End), proposed.Sum(a => a.LaborCost));
        var completed = new List<ForecastActivityAllocation>();
        foreach (var row in proposed.OrderBy(a => a.Start).ThenBy(a => a.Activity, StringComparer.Ordinal))
        {
            var option = options[(row.Activity, row.Pawn)];
            var task = tasks[row.Activity];
            var limited = limits.TryGetValue(row.Activity, out var limit);
            double? cutoff = limited ? limit.CallbackCut : null;
            var forecast = ForecastProductiveWorkPolicy.ForecastProductiveWork(option, completed, row.Start, cutoff);
            if (!forecast.WorkTicks.TryGetValue(out var work)) return null;
            var pawn = problem.Pawns.Single(p => p.Pawn == row.Pawn);
            var finish = limited ? limit.Finish : row.Start + work;
            work = finish - row.Start;
            var evidence = forecast.Evidence;
            if (limited && evidence is not null)
            {
                var events = completed.Where(a => a.Pawn == row.Pawn && a.Learning?.Skill == evidence.Skill)
                    .SelectMany(a => a.Learning!.Callbacks).Concat(evidence.Callbacks).OrderBy(e => e.Tick).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
                var after = ForecastSkillPolicy.ForecastSkill(option.SkillWork!.Learning, events, (int)Math.Ceiling(finish));
                if (after is not SkillForecast.Projected { Projection: var projection })
                { findings.UnionWith(ForecastSkillPolicy.FindingsOf(after).Select(f => "research_learning_unprojectable:" + f)); return null; }
                evidence = evidence with { WorkAmount = limit.Units, WorkSegments = LimitWork(evidence.WorkSegments, limit.Units), WorkTicks = work, After = projection };
            }
            if (finish > problem.Deadline.Value || task.LatestFinish is { } latest && finish > latest
                || !pawn.Windows.Value.Any(w => row.Depart >= w.Start && finish <= w.End)
                || completed.Any(a => a.Pawn == row.Pawn && a.End > row.Depart)
                || task.Prerequisites.Any(id => completed.All(a => a.Activity != id || a.End > row.Start))) return null;
            completed.Add(row with { End = finish, WorkTicks = work, DisplacedLaborTicks = (finish - row.Depart) * pawn.OpportunityCostPerTick.Value,
                Learning = evidence });
        }
        foreach (var group in completed.SelectMany(a => options[(a.Activity, a.Pawn)].Equipment.Select(e => (Equipment: e, Allocation: a))).GroupBy(a => a.Equipment))
        {
            var ordered = group.OrderBy(a => a.Allocation.Start).ToArray();
            if (ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.Allocation.End > pair.Second.Allocation.Start)) return null;
        }
        var result = completed.OrderBy(a => a.Activity, StringComparer.Ordinal).ToArray();
        return new(result, result.Max(a => a.End), result.Sum(a => a.LaborCost));
    }
    private static EquatableArray<ForecastWorkSegment> LimitWork(EquatableArray<ForecastWorkSegment> segments, double units)
    {
        var result = new List<ForecastWorkSegment>();
        foreach (var segment in segments)
        {
            if (units <= 1e-9) break;
            var amount = (segment.End - segment.Start) * segment.WorkPerTick;
            if (amount <= units + 1e-9) { result.Add(segment); units -= amount; continue; }
            var callbacks = (int)Math.Floor(units / (segment.CallbackInterval * segment.WorkPerTick));
            var through = segment.Start + callbacks * segment.CallbackInterval;
            if (through > segment.Start) result.Add(segment with { End = through });
            units -= callbacks * segment.CallbackInterval * segment.WorkPerTick;
            if (units > 1e-9) result.Add(new(through, through + segment.CallbackInterval, units / segment.CallbackInterval, segment.CallbackInterval));
            break;
        }
        return EquatableArray.From(result);
    }
}
