using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public sealed record SiteReturnPlan
{
    public string Site { get; init; } = "";

    public CaravanDeparture? Departure { get; init; }

    public IReadOnlyList<PawnId> Crew { get; init; } = [];

    public int HomeTile { get; init; }

    public bool Retreat { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static partial class PolicyFunctions
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
            if (CaravanDeparture.TryCreate(trip.PawnIds, [], route.Tile, out var departure))
            {
                return departure;
            }
        }
        return null;
    }

    /// <summary>
    /// Retains the desired inventory explicitly because vanilla reform empties inventories before packing the
    /// selected transferables. A failed fight requests a walking retreat; it never reforms through hostiles.
    /// </summary>
    public static SiteReturnPlan PlanSiteReturn(WorldSite site, bool failedFight)
    {
        var result = new SiteReturnPlan { Site = site.Id };
        SiteReturnPlan Fail(QuestSkipReason reason) => result with { Reason = reason };

        if (!site.Extraction.TryGetValue(out var extraction) || extraction.Crew.Count == 0)
        {
            return Fail("extraction_unknown");
        }
        result = result with { Crew = extraction.Crew.ToList() };
        var routes = extraction.HomeRoutes.ToList();
        routes.Sort((x, y) =>
        {
            var ak = x.TravelTicks.TryGetValue(out var a);
            var bk = y.TravelTicks.TryGetValue(out var b);
            if (ak != bk)
            {
                return ak ? -1 : 1;
            }
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
            return x.Map.CompareTo(y.Map);
        });
        SiteHomeRoute? route = null;
        foreach (var candidate in routes)
        {
            var rk = candidate.Reachable.TryGetValue(out var reachable);
            var tk = candidate.TravelTicks.TryGetValue(out var ticks);
            if (rk && reachable && tk && ticks >= 0)
            {
                route = candidate;
                break;
            }
        }
        if (route is null)
        {
            return Fail("home_route_unknown");
        }
        result = result with { HomeTile = route.Tile };
        if (!extraction.CanReform.TryGetValue(out var canReform))
        {
            return Fail("reform_unknown");
        }
        if (!canReform)
        {
            if (failedFight)
            {
                return result with { Retreat = true };
            }
            return Fail("site_fight_pending");
        }
        if (!site.Security.TryGetValue(out var security))
        {
            return Fail("security_unknown");
        }
        var ck = security.Known.TryGetValue(out var complete);
        var tc = security.TrapCount.TryGetValue(out var traps);
        if (!ck || !complete || !tc)
        {
            return Fail("security_unknown");
        }
        if (traps > 0)
        {
            return Fail("site_hazards_pending");
        }
        var ak2 = security.ActiveThreat.TryGetValue(out var active);
        var dk = security.DormantThreat.TryGetValue(out var dormant);
        if (!ak2 || !dk)
        {
            return Fail("security_unknown");
        }
        if (active || dormant)
        {
            result = result with { Retreat = failedFight };
            return Fail("site_fight_pending");
        }
        route.TravelTicks.TryGetValue(out var travel);
        var days = (double)travel / GameTime.TicksPerDay + 1;
        var (cargo, reason) = SiteReturnCargo(extraction, days, !failedFight);
        if (!reason.IsNone)
        {
            return Fail(reason);
        }
        if (!CaravanDeparture.TryCreate(extraction.Crew, cargo, route.Tile, out var departure))
        {
            return Fail("cargo_invalid");
        }
        return result with { Departure = departure };
    }

    private static (IReadOnlyList<CargoItem> Cargo, QuestSkipReason Reason) SiteReturnCargo(SiteExtraction extraction, double days, bool loot)
    {
        IReadOnlyList<CargoItem> none = [];
        var ck = extraction.CarryCapacity.TryGetValue(out var capacity);
        var mk = extraction.CarriedMass.TryGetValue(out var carried);
        var fk = extraction.InventoryFoodDays.TryGetValue(out var inventoryFood);
        var rk = extraction.CrewNutritionPerDay.TryGetValue(out var rate);
        if (!ck || !mk || !Finite(capacity) || !Finite(carried) || capacity < 0 || carried < 0)
        {
            return (none, "mass_unknown");
        }
        if (!fk || !rk || !Finite(inventoryFood) || !Finite(rate) || inventoryFood < 0 || rate <= 0)
        {
            return (none, "food_unknown");
        }
        var rows = extraction.Cargo.ToList();
        rows.Sort((x, y) =>
        {
            x.Held.TryGetValue(out var a);
            y.Held.TryGetValue(out var b);
            if (a != b)
            {
                return a ? -1 : 1;
            }
            x.MarketValue.TryGetValue(out var av);
            y.MarketValue.TryGetValue(out var bv);
            x.UnitMass.TryGetValue(out var am);
            y.UnitMass.TryGetValue(out var bm);
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
            var hk = row.Held.TryGetValue(out var held);
            var nk = row.Count.TryGetValue(out var count);
            var uk = row.UnitMass.TryGetValue(out var mass);
            if (!hk || !nk || !uk || count < 0 || !Finite(mass) || mass < 0)
            {
                return (none, "cargo_unknown");
            }
            if (held)
            {
                free += count * mass;
            }
        }
        var packed = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var left = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            row.Count.TryGetValue(out var count);
            row.UnitMass.TryGetValue(out var mass);
            row.Held.TryGetValue(out var held);
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
            var nk = row.Nutrition.TryGetValue(out var nutrition);
            row.UnitMass.TryGetValue(out var mass);
            if (count == 0 || !nk || !Finite(nutrition) || nutrition <= 0)
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
        if (loot)
        {
            foreach (var row in rows)
            {
                var count = left.GetValueOrDefault(row.Id);
                var vk = row.MarketValue.TryGetValue(out var value);
                row.UnitMass.TryGetValue(out var mass);
                if (count == 0 || !vk || !Finite(value) || value <= 0)
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
        }
        var cargo = packed.Where(kv => kv.Value > 0)
            .Select(kv => new CargoItem(kv.Key, kv.Value))
            .OrderBy(c => c.Definition, StringComparer.Ordinal)
            .ToList();
        return (cargo, "");
    }
}
