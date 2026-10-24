namespace RimGovernor.Brain.Policy;

/// <summary>
/// A player animal the game walks off the map (#3812, AnimalUpkeepObservation.Roamers): its census id, race def, the game's
/// RoamMtbDays, its MarketValue stat and whether it can reach the map edge now (the gate of MentalStateWorker_Roaming.CanRoamNow).
/// </summary>
public sealed record PenRoamer(string Id, string Race, double RoamMtbDays, double MarketValue, bool ReachesMapEdge);

/// <summary>
/// The Pen credit (#3812, epic #3650 P1): the roaming losses a planned pen stops. A roamer that can reach the map edge starts
/// a Roaming mental state once per RoamMtbDays on average (ThinkNode_ChancePerDay_Roam, JobGiver_StartRoaming) and walks off
/// the map, losing its MarketValue; a pen encloses it, so it can no longer reach the edge. Per such roamer of the pen's herd,
/// the pen gains its value in capability-days (the market value over <see cref="SilverGapRules.ValuePerWorkTick"/>, as
/// <see cref="ForecastValue.MaterialValueOf"/> prices a market good) over its RoamMtbDays a day. A pen takes the roamers of its
/// herd's race; a misc pen (herd "") those whose race has no pen of its own; with several pens of a herd the lowest id takes
/// them. A roamer already enclosed, and an animal that does not roam, earn nothing.
/// </summary>
internal static class ForecastPens
{
    /// <summary>The pen's credit (<see cref="RoomCredit"/>): one row per roamer it takes, under the roamer's id.</summary>
    internal static IReadOnlyList<RoomGain> PenCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        if (!context.PenHerds.TryGetValue(room.Room, out var herd)) return [];
        if (context.PenHerds.Where(p => p.Value == herd).Min(p => p.Key) != room.Room) return [];
        var roamers = context.Roamers ?? throw new RequiredFactException("animal census");
        var penned = context.PenHerds.Values.Where(h => h.Length != 0).ToHashSet(StringComparer.Ordinal);
        return [.. roamers.Where(r => r.ReachesMapEdge && (herd.Length != 0 ? r.Race == herd : !penned.Contains(r.Race)))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new RoomGain(r.Id, "", LossPerDay(r)))];
    }

    /// <summary>The capability-days a day a roamer's walking off the map costs: its value over its RoamMtbDays.</summary>
    internal static double LossPerDay(PenRoamer roamer) =>
        roamer.MarketValue / SilverGapRules.ValuePerWorkTick / GameTime.TicksPerDay / roamer.RoamMtbDays;
}
