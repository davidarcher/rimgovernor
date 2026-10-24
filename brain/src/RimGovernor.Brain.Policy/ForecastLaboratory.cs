using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The Lab role's credit (#3800, epic #3650 P1): the research speed a planned laboratory gives its bench. The bench's
/// StatDefOf.ResearchSpeedFactor in the closed room (<see cref="BenchSpeedModel"/> at the room's predicted role and
/// cleanliness, <see cref="PlannedRoomCleanlinessRules.Steady"/>, indoors) is compared with its factor now: a research
/// bench standing in the room is compared with its observed factor (<see cref="NeedsForecastRead.ResearchBenches"/>) and
/// gains the research done at it; with none standing, the room's planned bench (<see cref="ForecastCoupledContext.LaboratoryBench"/>)
/// is compared with the same bench placed outdoors and gains all of the schedule's research. The gain in factor scales
/// each research allocation's points (a point a tick is ResearchSpeed × the bench's factor × the game's point rate), so
/// a served day earns, per researcher, the allocation's points over the horizon's days / its bench's factor now × the gain
/// × <see cref="ForecastCoupledContext.Investment"/>'s value of a point of its project. No research (no current project,
/// or none valued) earns nothing.
/// </summary>
internal static class ForecastLaboratory
{
    /// <summary>
    /// The lab's gains per researcher (<see cref="RoomCredit"/>), in the prediction the room is served in
    /// (<see cref="PlannedRoomCredits.Prediction"/>). None without research, or for a shape that closes no room. Throws <see cref="RequiredFactException"/> (#3835)
    /// when the room's layout room or filth model, a stat part, or a research bench's row, factor or bench model is unread.
    /// </summary>
    internal static IReadOnlyList<RoomGain> LaboratoryCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        var tasks = context.Activities.Activities.Where(t => t.ProjectProgress is { } p && !p.Project.StartsWith(ForecastProjectProgress.SowPrefix, StringComparison.Ordinal))
            .GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().ProjectProgress!, StringComparer.Ordinal);
        var research = outcome.Plan.Allocations.Where(a => tasks.TryGetValue(a.Activity, out var work) && context.Investment.ContainsKey(work.Project)).ToArray();
        if (research.Length == 0) return [];
        if (PlannedRoomCredits.Prediction(context, room.Room, room.Stuff) is not { } closure) return [];
        if (!context.LayoutRooms.TryGetValue(room.Room, out var planned)) throw new RequiredFactException("planned_room_layout:" + room.Room.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // A modded role RoomRole does not name is a proper room of no vanilla role: a role part's room is not its role.
        var role = Wire.TryParse<RoomRole>(closure.Prediction.Role, out var named) ? named : RoomRole.Room;
        var closed = BenchRoom.Closed(role, PlannedRoomCleanlinessRules.Steady(closure.Prediction, planned.FilthRuns.Values, context.Filth).Dirty);
        var benches = context.Needs.ResearchBenches.GroupBy(b => b.Bench, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var standing = benches.Values.Where(b => closure.Interior.Contains(b.Position)).Select(b => b.Bench).ToHashSet(StringComparer.Ordinal);
        // With none standing in the room, its planned bench placed outdoors is what the room changes.
        double? unhoused = null;
        if (standing.Count == 0)
        {
            if (!context.ResearchBenchSpeeds.TryGetValue(context.LaboratoryBench, out var lab)) throw new RequiredFactException("research bench speed " + context.LaboratoryBench);
            unhoused = lab.At(closed) - lab.At(BenchRoom.Outside);
        }
        var perDay = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var allocation in research)
        {
            var work = tasks[allocation.Activity];
            var bench = context.Activities.Options.FirstOrDefault(o => o.Activity == allocation.Activity && o.Pawn == allocation.Pawn)?.Equipment is { Count: > 0 } equipment
                ? equipment[0] : throw new RequiredFactException("research bench of " + allocation.Activity);
            if (!benches.TryGetValue(bench, out var now) || !(now.Factor > 0)) throw new RequiredFactException("research bench factor " + bench);
            double gain;
            if (unhoused is { } outdoors) gain = outdoors;
            else if (!standing.Contains(bench)) continue;
            else if (context.ResearchBenchSpeeds.TryGetValue(now.Def, out var model)) gain = model.At(closed) - now.Factor;
            else throw new RequiredFactException("research bench speed " + now.Def);
            var points = allocation.Learning?.WorkAmount ?? allocation.WorkTicks * work.PointsPerTick;
            perDay[allocation.Pawn] = perDay.GetValueOrDefault(allocation.Pawn)
                + (points / now.Factor * gain * context.Investment[work.Project] * GameTime.TicksPerDay / horizon);
        }
        return [.. perDay.Select(p => new RoomGain(p.Key, p.Key, p.Value))];
    }
}
