using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record SiteReturnPlan
{
    public CaravanDeparture? Departure { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static class QuestReturn
{
    /// <summary>
    /// A stopped caravan with a crew from a journaled expedition can return using the native home route. A
    /// moving outbound or returning caravan keeps its order.
    /// </summary>
    public static CaravanDeparture? PlanStoppedExpeditionReturn(ExpeditionTrip trip, IReadOnlyList<PawnId> departed)
    {
        if (trip.Forming || trip.PawnIds.Count == 0)
        {
            return null;
        }
        if (trip.Destination.TryGetValue(out _))
        {
            return null;
        }
        foreach (var id in trip.PawnIds)
        {
            if (!departed.Contains(id))
            {
                return null;
            }
        }
        if (!trip.Tile.TryGetValue(out var tile))
        {
            return null;
        }
        var routes = trip.HomeRoutes
            .OrderBy(r => r.TravelTicks.TryGetValue(out var t) ? t : 0L)
            .ThenBy(r => r.Tile)
            .ToList();
        foreach (var route in routes)
        {
            var rk = route.Reachable.TryGetValue(out var reachable);
            var tk = route.TravelTicks.TryGetValue(out var ticks);
            if (route.Tile == tile)
            {
                return null;
            }
            if (!rk || !reachable || !tk || ticks < 0)
            {
                continue;
            }
            if (CaravanDeparture.TryCreate(trip.PawnIds.Select(p => (Domain.PawnId)p), [], route.Tile, out var departure))
            {
                return departure with { Mode = CaravanDepartureMode.ExistingRoute };
            }
        }
        return null;
    }

    /// <summary>
    /// Retains the desired inventory explicitly because vanilla reform empties inventories before packing the
    /// selected transferables. It never reforms through hostiles.
    /// </summary>
    public static SiteReturnPlan PlanSiteReturn(WorldSite site)
    {
        var result = new SiteReturnPlan();
        SiteReturnPlan Fail(QuestSkipReason reason) => result with { Reason = reason };

        if (site.Place is not Pb.SitePlace.Loaded { Map: var map } || map.Extraction is not Pb.QuestSiteExtraction.Crew { Value: var extraction })
        {
            return Fail("extraction_unknown");
        }
        Pb.QuestSiteHomeRoute? route = null;
        long travel = 0;
        foreach (var candidate in extraction.HomeRoutes)
        {
            if (candidate.Travel is Pb.SiteTravel.Reachable { TravelTicks: var ticks } && ticks >= 0
                && (route is null || ticks < travel || (ticks == travel && candidate.Map.CompareTo(route.Map) < 0)))
            {
                route = candidate;
                travel = ticks;
            }
        }
        if (route is null)
        {
            return Fail("home_route_unknown");
        }
        if (!extraction.CanReform)
        {
            return Fail("site_fight_pending");
        }
        if (site.Security is not Pb.SiteThreatBudget.Bounded)
        {
            return Fail("security_unknown");
        }
        if (map.Threat is Pb.SiteThreat.Active or Pb.SiteThreat.Dormant)
        {
            return Fail("site_fight_pending");
        }
        if (map.Threat is Pb.SiteThreat.Trapped)
        {
            return Fail("site_hazards_pending");
        }
        var days = (double)travel / GameTime.TicksPerDay + 1;
        var (cargo, reason) = SiteReturnCargo(extraction, days);
        if (!reason.IsNone)
        {
            return Fail(reason);
        }
        if (!CaravanDeparture.TryCreate(extraction.Crew.Select(p => (Domain.PawnId)p), cargo, route.Tile, out var departure))
        {
            return Fail("cargo_invalid");
        }
        return result with { Departure = departure with { Mode = CaravanDepartureMode.SiteReform } };
    }

    private static (IReadOnlyList<CargoItem> Cargo, QuestSkipReason Reason) SiteReturnCargo(Pb.ExtractionCrew extraction, double days)
    {
        IReadOnlyList<CargoItem> none = [];
        var capacity = extraction.CarryCapacity;
        var carried = extraction.CarriedMass;
        var inventoryFood = extraction.InventoryFoodDays;
        var rate = extraction.CrewNutritionPerDay;
        if (!TradePolicyRules.Finite(capacity) || !TradePolicyRules.Finite(carried) || capacity < 0 || carried < 0)
        {
            return (none, "mass_unknown");
        }
        if (!TradePolicyRules.Finite(inventoryFood) || !TradePolicyRules.Finite(rate) || inventoryFood < 0 || rate <= 0)
        {
            return (none, "food_unknown");
        }
        var rows = extraction.Cargo.ToList();
        rows.Sort((x, y) =>
        {
            var a = x.Held;
            var b = y.Held;
            if (a != b)
            {
                return a ? -1 : 1;
            }
            var av = x.MarketValue;
            var bv = y.MarketValue;
            var am = x.UnitMass;
            var bm = y.UnitMass;
            if (am > 0)
            {
                av /= am;
            }
            if (bm > 0)
            {
                bv /= bm;
            }
            if (av != bv)
            {
                return av > bv ? -1 : 1;
            }
            return string.CompareOrdinal(x.Id, y.Id);
        });
        // Inventory mass is already in CarriedMass, but reform repacks it. Credit that mass once, then
        // account for every retained inventory stack below.
        var free = capacity - carried;
        foreach (var row in rows)
        {
            if (row.Count < 0 || !TradePolicyRules.Finite(row.UnitMass) || row.UnitMass < 0)
            {
                return (none, "cargo_unknown");
            }
            if (row.Held)
            {
                free += row.Count * row.UnitMass;
            }
        }
        var packed = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var left = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var count = row.Count;
            var mass = row.UnitMass;
            var held = row.Held;
            if (row.Def.Length == 0 || count > int.MaxValue)
            {
                return (none, "cargo_invalid");
            }
            left[row.Id] = count;
            if (!held)
            {
                continue;
            }
            if (count * mass > free)
            {
                return (none, "mass_capacity");
            }
            packed[row.Def] = packed.GetValueOrDefault(row.Def) + (ulong)count;
            left[row.Id] = 0;
            free -= count * mass;
        }
        var need = Math.Max(0.0, days - inventoryFood) * rate;
        foreach (var row in rows)
        {
            if (need <= 0)
            {
                break;
            }
            var count = left.GetValueOrDefault(row.Id);
            var nutrition = row.Nutrition;
            var mass = row.UnitMass;
            if (count == 0 || !TradePolicyRules.Finite(nutrition) || nutrition <= 0)
            {
                continue;
            }
            var take = Math.Min(count, (long)Math.Ceiling(need / nutrition));
            if (mass > 0)
            {
                take = Math.Min(take, Math.Max(0L, (long)Math.Floor(free / mass)));
            }
            packed[row.Def] = packed.GetValueOrDefault(row.Def) + (ulong)take;
            left[row.Id] -= take;
            free -= take * mass;
            need -= take * nutrition;
        }
        if (need > 0)
        {
            return (none, "food_capacity");
        }
        foreach (var row in rows)
        {
            var count = left.GetValueOrDefault(row.Id);
            var value = row.MarketValue;
            var mass = row.UnitMass;
            if (count == 0 || !TradePolicyRules.Finite(value) || value <= 0)
            {
                continue;
            }
            var take = count;
            if (mass > 0)
            {
                take = Math.Min(take, Math.Max(0L, (long)Math.Floor(free / mass)));
            }
            packed[row.Def] = packed.GetValueOrDefault(row.Def) + (ulong)take;
            free -= take * mass;
        }
        var cargo = packed.Where(kv => kv.Value > 0)
            .Select(kv => new CargoItem(kv.Key, kv.Value))
            .OrderBy(c => c.Definition, StringComparer.Ordinal)
            .ToList();
        return (cargo, "");
    }
}
