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
    /// A served kitchen's credit (#3802, #3835): its own planned cooking, the meals a day its stations make
    /// (<see cref="PlannedRoom.Meals"/>), made at the stat's roomless chance (FoodPoisonRoomless: the stations standing with no
    /// room around them) less at the planned kitchen's, times the price of a poisoning, one row under <see cref="Poisoning"/>.
    /// None for a kitchen that cooks nothing. Throws <see cref="RequiredFactException"/> when the kitchen's layout room,
    /// prediction, the filth model or the poisoning price is unread.
    /// </summary>
    internal static IReadOnlyList<RoomGain> KitchenCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        var kitchen = Layout(context, room.Room);
        if (!(kitchen.Meals > 0)) return [];
        var planned = Chance(context, room.Room, room.Stuff, kitchen.FilthRuns.Values);
        var roomless = context.Filth.FoodPoisonRoomless;
        return [new(Poisoning, "", (roomless - planned) * kitchen.Meals * ForecastValue.PoisoningPrice(outcome, context, horizon))];
    }

    /// <summary>
    /// A served butchery's credit: per planned kitchen, the poisoning of its meals its butchering's blood would cause there,
    /// the kitchen's chance with the butchery's filth runs added to its own less its chance with its own, times the kitchen's
    /// meals a day and the price of a poisoning, a row under the kitchen's id. The kitchen's prediction is the one in the
    /// butchery's ring stuff if it has one, else its first by stuff (walls and doors carry no Cleanliness stat, so the ring
    /// stuff does not move the stat). None for a butchery without runs or a plan without a kitchen, or a kitchen that cooks
    /// nothing; throws as <see cref="KitchenCredit"/> for a layout room, prediction, filth model or the price.
    /// </summary>
    internal static IReadOnlyList<RoomGain> ButcheryCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        var butchery = Layout(context, room.Room);
        if (butchery.FilthRuns.Values.All(r => !(r.Runs > 0))) return [];
        var kitchens = context.PlannedRoomRoles.Where(r => r.Value == PlannedRole.Kitchen && r.Key != room.Room).Select(r => r.Key).Order().ToArray();
        var gains = new List<RoomGain>(kitchens.Length);
        foreach (var id in kitchens)
        {
            var kitchen = Layout(context, id);
            if (!(kitchen.Meals > 0)) continue;
            var own = kitchen.FilthRuns.Values.ToArray();
            var clean = Chance(context, id, room.Stuff, own);
            var dirty = Chance(context, id, room.Stuff, [.. own, .. butchery.FilthRuns.Values]);
            gains.Add(new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), "", (dirty - clean) * kitchen.Meals * ForecastValue.PoisoningPrice(outcome, context, horizon)));
        }
        return gains;
    }

    /// <summary>A planned room's layout room; one the layout plan lacks throws <see cref="RequiredFactException"/> (#3835).</summary>
    private static PlannedRoom Layout(ForecastCoupledContext context, int room) => context.LayoutRooms.TryGetValue(room, out var planned) ? planned
        : throw new RequiredFactException("planned_room_layout:" + room.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The poison chance of planned room <paramref name="room"/> with <paramref name="runs"/> standing in it, from its
    /// prediction in <paramref name="stuff"/> (<see cref="PlannedRoomCredits.Prediction"/>), the roomless chance for a shape that
    /// closes no room; throws when the filth model is unread.
    /// </summary>
    private static double Chance(ForecastCoupledContext context, int room, string stuff, IEnumerable<FilthRuns> runs)
    {
        if (!PlannedRoomCleanlinessRules.Read(context.Filth)) throw new RequiredFactException("filth_model");
        // A shape whose ring closes no room (no prediction row, #3834) leaves its stations roomless.
        return PlannedRoomCredits.Prediction(context, room, stuff) is { } closure
            ? PlannedRoomCleanlinessRules.FoodPoisonChance(closure.Prediction, runs, context.Filth) : context.Filth.FoodPoisonRoomless;
    }
}
