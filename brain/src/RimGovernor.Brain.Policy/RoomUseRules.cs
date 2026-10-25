using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Conditional steady use of the same best services as the colony cycle. Native facts supply legality and rates; this owner accounts for meals, shared seats and bounded joy sessions.</summary>
internal static class RoomUseRules
{
    internal sealed record Levels(IReadOnlyDictionary<string, double> Pawns, string[] Findings);
    private sealed record Uses(NeedsForecastPawn Pawn, Dictionary<int, double> Stages)
    { internal double Without; }

    internal static Levels Of(ForecastCoupledContext context, bool dining, IReadOnlyList<ForecastPlannedClosure> rooms)
    {
        var kind = dining ? "food" : "joy";
        var users = context.Needs.Pawns.Where(p => p.Mood != null && p.Needs.Any(n => n.Kind == kind && n.FallPerTick > 0))
            .DistinctBy(p => p.Pawn).OrderBy(p => p.Pawn, StringComparer.Ordinal).ToArray();
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var uses = users.ToDictionary(p => p.Pawn, p => new Uses(p, []), StringComparer.Ordinal);
        foreach (var pawn in users)
            if (pawn.RoomUses is null) throw new RequiredFactException("room service mechanics:" + pawn.Pawn);
        foreach (var room in rooms)
            if (room.Prediction.Services is null) throw new RequiredFactException("planned room services:" + room.Room);
        if (dining) Meals(context, uses, rooms, findings);
        else foreach (var pawn in users) Recreation(context, uses[pawn.Pawn], rooms, findings);
        var role = dining ? ForecastRoomThoughts.DiningRole : ForecastRoomThoughts.RecRole;
        var output = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, read) in uses)
        {
            var level = Memory(read.Pawn, role, read.Stages);
            if (dining && read.Without > 0) level += Memory(read.Pawn, ForecastRoomThoughts.AteWithoutTableRole, new Dictionary<int, double> { [0] = read.Without });
            output[id] = level;
        }
        return new(output, [.. findings]);
    }

    internal static double Memory(NeedsForecastPawn pawn, string role, IReadOnlyDictionary<int, double> uses)
    {
        var entries = new List<(NeedThoughtStage Row, double Uses)>();
        foreach (var (stage, rate) in uses)
        {
            if (!(rate > 0)) continue;
            var rows = pawn.ThoughtStages.Where(r => r.Role == role && r.Stage == stage).DistinctBy(r => r.Thought).ToArray();
            if (rows.Length == 0) throw new RequiredFactException(role + " thought stage " + stage + ":" + pawn.Pawn);
            entries.AddRange(rows.Select(r => (r, rate)));
        }
        double level = 0;
        foreach (var group in entries.GroupBy(e => e.Row.Thought, StringComparer.Ordinal))
        {
            if (group.First().Row.Lasting is not ThoughtLasting.Memory memory)
                throw new RequiredFactException(role + " thought memory:" + pawn.Pawn);
            if (memory.StagesStack)
            {
                var rate = group.Sum(e => e.Uses);
                level += ForecastRoomThoughts.Held(group.Sum(e => e.Row.Offset * e.Uses) / rate, memory, rate);
            }
            else foreach (var entry in group) level += ForecastRoomThoughts.Held(entry.Row.Offset, memory, entry.Uses);
        }
        return level;
    }

    private static void Meals(ForecastCoupledContext context, Dictionary<string, Uses> uses,
        IReadOnlyList<ForecastPlannedClosure> rooms, SortedSet<string> findings)
    {
        var days = context.InvestmentHorizonDays;
        if (!(days > 0)) return;
        var until = days * GameTime.TicksPerDay;
        var queue = new PriorityQueue<(Uses Read, NeedTarget Target, double At, double Period), (double At, string Pawn)>();
        foreach (var read in uses.Values)
        {
            var pawn = read.Pawn;
            var need = pawn.Needs.Single(n => n.Kind == "food");
            var target = NeedServiceRules.Meal(pawn);
            if (target is null) { findings.Add("dining_meal_source_unobserved:" + pawn.Pawn); continue; }
            if (target.Meal is null) throw new RequiredFactException("meal table mechanics:" + target.Id);
            var service = ((NeedRecovery.Ingest)target.Recovery).ServiceTicks;
            if (!(service > 0)) throw new RequiredFactException("meal service ticks:" + target.Id);
            var period = Math.Max(service, NeedServiceRules.MealGain(target, need) / need.FallPerTick);
            var at = Math.Max(0, (need.Level - need.Threshold) / need.FallPerTick);
            queue.Enqueue((read, target, at, period), (at, pawn.Pawn));
        }
        var busy = new Dictionary<Cell, double>();
        while (queue.TryDequeue(out var item, out _))
        {
            var (read, target, at, period) = item;
            var service = ((NeedRecovery.Ingest)target.Recovery).ServiceTicks;
            if (at + service > until) continue;
            var meal = target.Meal!;
            if (meal.ChairSearchRadius > 10)
            {
                var seats = read.Pawn.RoomUses!.Seats.Where(s => !rooms.Any(r => r.Interior.Contains(s.Position))).Select(s => (s.Position, s.Stage))
                    .Concat(rooms.SelectMany(r => r.Prediction.Services!.Seats.Select(s => (Position: s, Stage: (NeedRoomStage)new NeedRoomStage.Staged(r.Prediction.Stage)))))
                    .GroupBy(s => s.Position).Select(g => g.Last())
                    .Where(s => Distance(s.Position, target.Location) <= meal.ChairSearchRadius * meal.ChairSearchRadius
                        && busy.GetValueOrDefault(s.Position) <= at)
                    .OrderBy(s => Distance(s.Position, target.Location)).ThenBy(s => s.Position.X).ThenBy(s => s.Position.Z).ToArray();
                var stage = rooms.LastOrDefault(r => r.Interior.Contains(target.Location)) is { } room
                    ? new NeedRoomStage.Staged(room.Prediction.Stage) : target.RoomStage;
                if (seats.Length > 0)
                {
                    var seat = seats[0]; busy[seat.Position] = at + service; stage = seat.Stage;
                }
                else if (meal.TableDesired) read.Without += 1 / days;
                if (stage is NeedRoomStage.Staged(var index)) read.Stages[index] = read.Stages.GetValueOrDefault(index) + 1 / days;
            }
            var next = at + period;
            if (next < until) queue.Enqueue((read, target, next, period), (next, read.Pawn.Pawn));
        }
    }

    private static double Distance(Cell a, Cell b) => (double)(a.X - b.X) * (a.X - b.X) + (double)(a.Z - b.Z) * (a.Z - b.Z);

    private sealed record Joy(string Key, double Rate, NeedJoySession Session, NeedRoomStage Stage, double Approach);

    private static void Recreation(ForecastCoupledContext context, Uses uses, IReadOnlyList<ForecastPlannedClosure> rooms, SortedSet<string> findings)
    {
        var pawn = uses.Pawn;
        var need = pawn.Needs.Single(n => n.Kind == "joy");
        if (need.OutdoorShareByTwelfth.Count != 12) throw new RequiredFactException("outdoor recreation share of " + pawn.Pawn);
        var sources = pawn.Targets.Where(t => t.Kind == "joy" && t.Recovery is NeedRecovery.Rate && t.JoySession != null)
            .Select(t => new Joy(t.Id, ((NeedRecovery.Rate)t.Recovery).GainPerTick, t.JoySession!,
                rooms.LastOrDefault(r => r.Interior.Contains(t.Location)) is { } room
                    ? new NeedRoomStage.Staged(room.Prediction.Stage) : t.RoomStage, t.TravelTicks)).ToList();
        foreach (var room in rooms)
            foreach (var source in room.Prediction.Services!.Recreation)
            {
                if (!source.Powered) { findings.Add("rec_source_power_unobserved:" + source.Definition); continue; }
                var definition = pawn.RoomUses!.Recreation.FirstOrDefault(r => r.Definition == source.Definition);
                if (definition is null) continue; // Native eligibility refused this kind for this pawn.
                if (definition.Session is not { } session) { findings.Add("rec_session_driver_unread:" + source.Definition); continue; }
                var observed = pawn.Targets.FirstOrDefault(t => t.Kind == "joy" && t.Location == source.Position);
                if (session.IncludesApproach && observed is null) { findings.Add("rec_approach_unobserved:" + source.Definition); continue; }
                sources.Add(new("planned/" + room.Room + "/" + source.Position, definition.GainPerTick * source.GainFactor, session, new NeedRoomStage.Staged(room.Prediction.Stage), observed?.TravelTicks ?? 0));
            }
        var best = sources.Where(s => s.Rate > 0).OrderByDescending(s => s.Rate)
            .ThenByDescending(s => s.Stage is NeedRoomStage.Staged(var index) ? index : int.MinValue).ThenBy(s => s.Key, StringComparer.Ordinal).FirstOrDefault();
        if (best is null) return;
        var share = OutdoorShare(context, need);
        // The existing cycle takes the faster outdoor source in pleasant weather and the best building otherwise.
        var inside = need.UnservedGainPerTick > best.Rate ? 1 - share : 1;
        if (!best.Session.RoomThought || best.Stage is not NeedRoomStage.Staged(var stage) || inside <= 0) return;
        var duration = Math.Min(best.Session.DurationTicks - (best.Session.IncludesApproach ? best.Approach : 0), (1 - need.Threshold) / best.Rate);
        if (!(duration > 0)) throw new RequiredFactException("joy service ticks:" + best.Key);
        var refill = Math.Min(1 - need.Threshold, best.Rate * duration);
        var activities = inside * GameTime.TicksPerDay / (duration + refill / need.FallPerTick);
        uses.Stages[stage] = activities;
        findings.Add("rec_future_tolerance_changes:" + pawn.Pawn);
    }

    private static double OutdoorShare(ForecastCoupledContext context, NeedRate need)
    {
        if (need.OutdoorShareByTwelfth.All(s => s == need.OutdoorShareByTwelfth[0])) return need.OutdoorShareByTwelfth[0];
        var climate = context.ModelBasis?.Input.Climate ?? throw new RequiredFactException("room recreation calendar");
        var remaining = context.InvestmentHorizonDays;
        var twelfth = climate.CurrentTwelfth;
        var span = climate.TicksToNextTwelfth / (double)GameTime.TicksPerDay;
        double total = 0;
        while (remaining > 0)
        {
            var days = Math.Min(span, remaining); total += days * need.OutdoorShareByTwelfth[twelfth]; remaining -= days;
            twelfth = (twelfth + 1) % 12; span = GearClimateRules.GearTwelfthTicks / (double)GameTime.TicksPerDay;
        }
        return total / context.InvestmentHorizonDays;
    }
}
