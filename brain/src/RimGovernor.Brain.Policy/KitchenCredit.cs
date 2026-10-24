using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The kitchen and butchery credits (#3802, epic #3650 P1): both are priced by the food poisoning their room changes. A meal is
/// poisoned by the room it is made in with the chance CompFoodPoisonable.Notify_RecipeProduced rolls first, the room's
/// RoomStatDefOf.FoodPoisonChance (the FoodPoisonChance curve over its Cleanliness, the stat's roomless score without a
/// proper room); the planned room's is <see cref="PlannedRoomCleanlinessRules.FoodPoisonChance"/> of its native prediction
/// and its stations' filth runs. Each poisoning is priced as the ledger prices one (<see cref="ForecastValue.PoisoningPrice"/>),
/// over the meals a day the kitchen's stations make (<see cref="PlannedRoom.Meals"/>).
/// </summary>
public static class KitchenCredits
{
    /// <summary>The kitchen row's subject: the poisoning its meals avoid.</summary>
    public const string Poisoning = "food_poisoning";

    /// <summary>
    /// A served kitchen's credit: the chance at the cook's current room (the bench demands, SupportDemand.FoodPoisonChance, of
    /// the benches the colony cooks at now, <see cref="ForecastCoupledContext.CookingBenches"/>) less the planned kitchen's,
    /// times its meals a day and the price of a poisoning, one row under <see cref="Poisoning"/>. None for a kitchen that
    /// cooks nothing. Unassessed (null) when the kitchen's layout room, prediction or the filth model is unread, when no bench
    /// cooks now or the benches that do stand in rooms of different chances (the cook's current room is no one room), or when
    /// the poisoning price is unread.
    /// </summary>
    internal static IReadOnlyList<RoomGain>? KitchenCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (!context.LayoutRooms.TryGetValue(room.Room, out var kitchen)) return null;
        if (!(kitchen.Meals > 0)) return [];
        if (Chance(context, room.Room, room.Stuff, kitchen.FilthRuns.Values) is not { } planned) return null;
        var benches = context.CookingBenches.ToHashSet(StringComparer.Ordinal);
        var current = context.Support.Demands.Where(d => d.Kind == "bench" && benches.Contains(d.Target)).Select(d => d.FoodPoisonChance).Distinct().ToArray();
        if (current is not [var now]) return null;
        if (ForecastValue.PoisoningPrice(outcome, context, horizon) is not { } price) return null;
        return [new(Poisoning, "", (now - planned) * kitchen.Meals * price)];
    }

    /// <summary>
    /// A served butchery's credit: per planned kitchen, the poisoning of its meals its butchering's blood would cause there,
    /// the kitchen's chance with the butchery's filth runs added to its own less its chance with its own, times the kitchen's
    /// meals a day and the price of a poisoning, a row under the kitchen's id. The kitchen's prediction is the one in the
    /// butchery's ring stuff if it has one, else its first by stuff (walls and doors carry no Cleanliness stat, so the ring
    /// stuff does not move the stat). None for a butchery without runs or a plan without a kitchen; unassessed (null) as
    /// <see cref="KitchenCredit"/> for a kitchen's layout room, prediction, filth model or the price.
    /// </summary>
    internal static IReadOnlyList<RoomGain>? ButcheryCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (!context.LayoutRooms.TryGetValue(room.Room, out var butchery)) return null;
        if (butchery.FilthRuns.Values.All(r => !(r.Runs > 0))) return [];
        var kitchens = context.PlannedRoomRoles.Where(r => r.Value == PlannedRole.Kitchen && r.Key != room.Room).Select(r => r.Key).Order().ToArray();
        if (kitchens.Length == 0) return [];
        double? price = null;
        var gains = new List<RoomGain>(kitchens.Length);
        foreach (var id in kitchens)
        {
            if (!context.LayoutRooms.TryGetValue(id, out var kitchen)) return null;
            if (!(kitchen.Meals > 0)) continue;
            var own = kitchen.FilthRuns.Values.ToArray();
            if (Chance(context, id, room.Stuff, own) is not { } clean || Chance(context, id, room.Stuff, [.. own, .. butchery.FilthRuns.Values]) is not { } dirty) return null;
            price ??= ForecastValue.PoisoningPrice(outcome, context, horizon);
            if (price is not { } each) return null;
            gains.Add(new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), "", (dirty - clean) * kitchen.Meals * each));
        }
        return gains;
    }

    /// <summary>
    /// The poison chance of planned room <paramref name="room"/> with <paramref name="runs"/> standing in it, from its closure
    /// prediction in <paramref name="stuff"/>, else its first by stuff; null when it has none or the filth model is unread.
    /// </summary>
    private static double? Chance(ForecastCoupledContext context, int room, string stuff, IEnumerable<FilthRuns> runs)
    {
        var predictions = context.Closures.Where(c => c.Room == room).OrderBy(c => c.Stuff, StringComparer.Ordinal).ToArray();
        var closure = predictions.FirstOrDefault(c => c.Stuff == stuff) ?? predictions.FirstOrDefault();
        if (closure is null || !PlannedRoomCleanlinessRules.Read(context.Filth)) return null;
        return PlannedRoomCleanlinessRules.FoodPoisonChance(closure.Prediction, runs, context.Filth);
    }
}
