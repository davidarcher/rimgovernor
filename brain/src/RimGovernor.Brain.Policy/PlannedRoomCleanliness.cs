using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A planned room's steady-state Cleanliness (#3797): <see cref="Clean"/> with no station filth standing,
/// <see cref="Dirty"/> while its stations' filth stands, which it does for the <see cref="Exposure"/> share of the day.
/// </summary>
public readonly record struct PlannedRoomCleanliness(double Clean, double Dirty, double Exposure);

/// <summary>
/// The steady-state Cleanliness of a planned room (#3797): the game's RoomStatWorker_Cleanliness as the native room
/// prediction reads it, with the room's planned floor laid (<see cref="PlannedRoomPrediction.Cleanliness"/>), plus the
/// filth the room's stations leave by the layout's own filth model. That model is the one the layout prices with
/// (#2988, #3750): no unit may be cleaned before FilthModel.StandTicks, so the units of a day's runs within one stand
/// of each other stand at once (<see cref="LayoutFilthRules.Standing"/>, from the station group's runs the tiler keeps
/// on the room, <see cref="PlannedRoom.FilthRuns"/>), each adding the filth def's Cleanliness stat to the room's sum,
/// which the room's cell count divides. The room is that dirty while the filth stands (its exposure share of the
/// day) and at the predicted value otherwise.
/// </summary>
public static class PlannedRoomCleanlinessRules
{
    /// <summary>
    /// The planned room's Cleanliness with <paramref name="runs"/> (a room's <see cref="PlannedRoom.FilthRuns"/>, or more)
    /// standing in it. A None room (map edge, over 60 regions) has the stat's roomless score whatever stands in it. An
    /// unread prediction (Role "") throws a RequiredFactException naming planned_room_prediction and the room; runs with
    /// an unread filth model, filth_model.
    /// </summary>
    public static PlannedRoomCleanliness Steady(PlannedRoomPrediction prediction, IEnumerable<FilthRuns> runs, FilthModel filth)
    {
        if (prediction.Role.Length == 0)
        {
            throw new RequiredFactException("planned_room_prediction:" + prediction.Room);
        }
        var clean = prediction.Cleanliness;
        if (prediction.Role == Wire.Name(RoomRole.None) || runs.All(r => !(r.Runs > 0)))
        {
            return new(clean, clean, 0);
        }
        if (!Read(filth))
        {
            throw new RequiredFactException("filth_model");
        }
        var (units, exposure) = LayoutFilthRules.Standing(runs, filth.StandTicks);
        return new(clean, clean + (units * filth.Cleanliness / prediction.Cells), exposure);
    }

    /// <summary>
    /// The chance a meal made in the planned room is poisoned by the room (CompFoodPoisonable.Notify_RecipeProduced's first
    /// roll, RoomStatDefOf.FoodPoisonChance, #3802) with <paramref name="runs"/> standing in it: the FoodPoisonChance curve
    /// at its <see cref="Steady"/> Cleanliness, dirty for the exposure share of the day and clean otherwise; a None room's,
    /// the stat's roomless score. Throws as <see cref="Steady"/> does, and filth_model without the curve.
    /// </summary>
    public static double FoodPoisonChance(PlannedRoomPrediction prediction, IEnumerable<FilthRuns> runs, FilthModel filth)
    {
        var steady = Steady(prediction, runs, filth);
        if (prediction.Role == Wire.Name(RoomRole.None))
        {
            return filth.FoodPoisonRoomless;
        }
        if (filth.FoodPoison.Count == 0)
        {
            throw new RequiredFactException("filth_model");
        }
        return ((1 - steady.Exposure) * LayoutFilthRules.EvaluateCurve(filth.FoodPoison, steady.Clean))
            + (steady.Exposure * LayoutFilthRules.EvaluateCurve(filth.FoodPoison, steady.Dirty));
    }

    /// <summary>Whether <paramref name="filth"/> was read: its filth def, its positive stand ticks and its poisoning curve.</summary>
    public static bool Read(FilthModel filth) =>
        filth.Def.Length != 0 && QuestCapacityForecast.FinitePositive(filth.StandTicks) && filth.FoodPoison.Count != 0;
}
