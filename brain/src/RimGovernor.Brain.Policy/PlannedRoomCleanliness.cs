using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The steady-state Cleanliness of a planned room (#3797): the game's RoomStatWorker_Cleanliness as the native room
/// prediction reads it, with the room's planned floor laid (<see cref="PlannedRoomPrediction.Cleanliness"/>), plus the
/// filth the room's stations leave by the layout's own filth model. That model is the one the layout prices with
/// (#2988, #3750): no unit may be cleaned before FilthModel.StandTicks, so the units of a day's runs within one stand
/// of each other stand at once (<see cref="LayoutFilthRules.Standing"/>, from the station group's runs the tiler keeps
/// on the room, <see cref="PlannedRoom.FilthRuns"/>), each adding the filth def's Cleanliness stat to the room's sum,
/// which the room's cell count divides. The room is that dirty while the filth stands (its exposure share of the
/// day) and at the predicted value otherwise; this answers the dirty value.
/// </summary>
public static class PlannedRoomCleanlinessRules
{
    /// <summary>
    /// The planned room's Cleanliness while its stations' filth stands. A None room (map edge, over 60 regions) has the
    /// stat's roomless score whatever stands in it. An unread prediction (Role "") throws a RequiredFactException
    /// naming planned_room_prediction and the room; a room with filth runs and an unread filth model, filth_model.
    /// </summary>
    public static double Steady(PlannedRoomPrediction prediction, PlannedRoom room, FilthModel filth)
    {
        if (prediction.Room != room.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new ArgumentException($"prediction of room {prediction.Room} read for room {room.Id}", nameof(prediction));
        }
        if (prediction.Role.Length == 0)
        {
            throw new RequiredFactException("planned_room_prediction:" + prediction.Room);
        }
        if (prediction.Role == Wire.Name(RoomRole.None) || room.FilthRuns.Values.All(r => !(r.Runs > 0)))
        {
            return prediction.Cleanliness;
        }
        if (filth.Def.Length == 0 || !QuestCapacityForecast.FinitePositive(filth.StandTicks))
        {
            throw new RequiredFactException("filth_model");
        }
        var (units, _) = LayoutFilthRules.Standing(room.FilthRuns.Values, filth.StandTicks);
        return prediction.Cleanliness + (units * filth.Cleanliness / prediction.Cells);
    }
}
