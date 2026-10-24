namespace RimGovernor.Brain.Policy;

/// <summary>
/// One planned store's stock (#3798, StoreRules.PlannedStoreStock): the stacks its interior holds (one a cell) and the
/// stranded loot rows its storage settings would take.
/// </summary>
public sealed record PlannedStock(int Cells, EquatableArray<LootItem> Rows);

// The storage credit (#3798, epic #3650): a planned storage room, meal closet or yard is worth the stock it keeps from
// deteriorating. Each stranded stack it would take loses the game's expected hit points a day where it lies now and fewer
// where the room puts it (StatPart_EnvironmentalEffects rebuilt there); the difference, at the stack's value per hit
// point, is the room's gain a served day.
public static class ForecastStorage
{
    /// <summary>
    /// The credit of a planned storage room, meal closet, reserve or yard (#3798): per stack it keeps, the hit points a day
    /// it no longer loses times its value per hit point (its count × <see cref="ForecastCoupledContext.MaterialValues"/>'
    /// value per unit over its MaxHitPoints). A reserve keeps no store and earns nothing. Rows are cached per context and
    /// shared out across the rooms in id order, each stack to one room (<see cref="Gains"/>). Null (unassessed) while the
    /// room's stock is unread, a kept stack's value is unpriced, or the map's rain, area or environmental part is.
    /// </summary>
    internal static IReadOnlyList<RoomGain>? StorageCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (context.PlannedRoomRoles.GetValueOrDefault(room.Room) == PlannedRole.Reserve) return [];
        return Cache.GetValue(context, Gains).GetValueOrDefault(room.Room);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Dictionary<int, IReadOnlyList<RoomGain>?>> Cache = new();

    /// <summary>
    /// The gains of every planned store with a stock row, by room: in room id order, each room takes the stacks no earlier
    /// room took whose loss it lowers, the largest gain first, up to its cells. A food stack hunger already debits
    /// (DeterioratedAt over the needs targets) is left to it. A room whose stock is unread, or one of whose stacks is
    /// unpriced or needs an unread map fact, is null and takes nothing.
    /// </summary>
    private static Dictionary<int, IReadOnlyList<RoomGain>?> Gains(ForecastCoupledContext context)
    {
        var output = new Dictionary<int, IReadOnlyList<RoomGain>?>();
        if (context.StoreStock is not { } stock) return output;
        var debited = context.Needs.Pawns.SelectMany(p => p.Targets).Where(t => t.Kind == "food" && t.Deterioration is not null)
            .Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (room, kept) in stock.OrderBy(p => p.Key))
        {
            var role = context.PlannedRoomRoles.GetValueOrDefault(room);
            if (role is not (PlannedRole.Storage or PlannedRole.MealCloset or PlannedRole.Yard)) continue;
            var rows = new List<RoomGain>();
            var assessed = true;
            foreach (var row in kept.Rows)
            {
                if (taken.Contains(row.Supply.Thing) || debited.Contains(row.Supply.Thing)) continue;
                var gain = Gain(row, role == PlannedRole.Yard, context);
                if (gain is not { } perDay) { assessed = false; break; }
                if (perDay > 0) rows.Add(new(row.Supply.Thing, "", perDay));
            }
            if (!assessed) { output[room] = null; continue; }
            var best = rows.OrderByDescending(g => g.PerDay).ThenBy(g => g.Subject, StringComparer.Ordinal).Take(Math.Max(0, kept.Cells)).ToList();
            taken.UnionWith(best.Select(g => g.Subject));
            output[room] = best;
        }
        return output;
    }

    /// <summary>
    /// The capability-days a day one stack gains in the room: the expected hit points lost a day at its observed rate less
    /// those at the rate where the room puts it (roofed and indoors; a yard's open air), its StatPart_EnvironmentalEffects
    /// factor rebuilt there with its terrain and edifice held, times its value per hit point. Zero for a stack that cannot
    /// deteriorate, is not exposed to its environment or loses no less there; null when its value or a map fact is unread.
    /// </summary>
    private static double? Gain(LootItem row, bool yard, ForecastCoupledContext context)
    {
        if (row.Deterioration is not { Environmental: true } d || !double.IsFinite(d.RatePerDay) || d.RatePerDay <= 0 || d.MaxHitPoints <= 0) return 0;
        var read = context.Needs;
        var rain = ForecastRoofConsequences.DeteriorationRain(read);
        if (read.Deterioration is not { } part || ForecastRoofConsequences.DeteriorationVisits(read) is not { } visits) return null;
        var now = ForecastRoofConsequences.EnvironmentalFactor(part, rain, d.Roofed, d.Outdoors, d.TerrainFactor, d.Protected);
        var there = ForecastRoofConsequences.EnvironmentalFactor(part, rain, roofed: !yard, outdoors: yard, d.TerrainFactor, d.Protected);
        if (now <= there) return 0;
        var saved = ForecastRoofConsequences.DeteriorationPerDay(d.RatePerDay, visits) - ForecastRoofConsequences.DeteriorationPerDay(d.RatePerDay * there / now, visits);
        if (saved <= 0) return 0;
        if (!context.MaterialValues.TryGetValue(new Resource(row.Supply.Definition), out var value) || value.Basis == MaterialValueBasis.Unpriced) return null;
        return saved * row.Count * value.PerUnit / d.MaxHitPoints;
    }

    /// <summary>The stock defs a planned store's credit prices (<see cref="Gain"/>): those of the kept rows that deteriorate.</summary>
    internal static IEnumerable<Resource> StockMaterials(ForecastCoupledContext context) =>
        context.StoreStock is not { } stock ? []
            : stock.Values.SelectMany(s => s.Rows).Where(r => r.Deterioration is { Environmental: true }).Select(r => new Resource(r.Supply.Definition));
}
