using O = RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Policy;

// Go: trade_mission.go.

public static partial class PolicyFunctions
{
    /// <summary>
    /// Selects one healthy negotiator, retaining the existing departure policy's last work owners, home staffing
    /// and defense headroom. Claimed pawns include urgent work and other outstanding missions.
    /// </summary>
    public static (IReadOnlyList<PawnId> Crew, QuestSkipReason Reason) TradeMissionCrew(RoundsFacts f, IReadOnlyList<PawnId> claimed)
    {
        if (!f.QuestDeparturePawns.TryGetValue(out var departure))
        {
            return ([], "capacity_unknown");
        }
        var rows = departure
            .OrderBy(
                x => x,
                Comparer<QuestDeparturePawn>.Create(
                    (x, y) =>
                    {
                        var xk = x.NegotiationAbility.TryGetValue(out var a);
                        var yk = y.NegotiationAbility.TryGetValue(out var b);
                        if (xk != yk)
                        {
                            return xk ? -1 : 1;
                        }
                        if (a != b)
                        {
                            return a > b ? -1 : 1;
                        }
                        return x.Id.CompareTo(y.Id);
                    }
                )
            )
            .ToList();
        if (!f.QuestSparePawns.TryGetValue(out var spare))
        {
            return ([], "capacity_unknown");
        }
        foreach (var row in rows)
        {
            var ak = row.NegotiationAbility.TryGetValue(out var ability);
            if (!ak || !Finite(ability) || ability <= 0 || claimed.Contains(row.Id) || !spare.Contains(row.Id))
            {
                continue;
            }
            var candidate = f with { QuestSparePawns = Known(EquatableArray.From([row.Id])) };
            var offer = new JoinerOffer
            {
                Profile = Known(new QuestProfile { Family = QuestFamily.BanditCamp }),
                Objectives = [new QuestObjective { Kind = O.QuestObjectiveKind.LoadPawns, Count = Known(1L) }],
            };
            var (crew, reason) = DepartureSquadWithCrew(offer, candidate, false, true);
            if (reason.Value.Length == 0)
            {
                return (crew, reason);
            }
        }
        return ([], "no_safe_negotiator");
    }

    /// <summary>Uses observed dietary access and rot, leaving every home consumer's reserve. Capacity excludes the observed silver mass.</summary>
    public static (IReadOnlyList<CargoItem> Cargo, QuestSkipReason Reason) TradeMissionFood(RoundsFacts f, IReadOnlyList<PawnId> crew, double roundTripDays, double capacity, RoundsPolicy p)
    {
        if (!f.AnimalUpkeep.Food.TryGetValue(out var supply) || !Finite(capacity) || capacity < 0 || !Finite(roundTripDays) || roundTripDays < 0)
        {
            return ([], "food_unknown");
        }
        return ExpeditionFood(supply, crew, Math.Max(1, roundTripDays), p.FoodMinDays, capacity);
    }

    public static (double Capacity, bool Ok) TradeMissionCarryCapacity(RoundsFacts f, IReadOnlyList<PawnId> crew)
    {
        if (!f.QuestDeparturePawns.TryGetValue(out var rows))
        {
            return (0, false);
        }
        var capacity = 0.0;
        foreach (var id in crew)
        {
            var found = false;
            foreach (var row in rows)
            {
                if (row.Id != id)
                {
                    continue;
                }
                var ck = row.CarryCapacity.TryGetValue(out var carry);
                var mk = row.CarriedMass.TryGetValue(out var mass);
                if (!ck || !mk || !FoodNumber(carry) || !FoodNumber(mass) || carry < mass)
                {
                    return (0, false);
                }
                capacity += carry - mass;
                found = true;
            }
            if (!found)
            {
                return (0, false);
            }
        }
        return (capacity, true);
    }

    /// <summary>Admits only a fresh native calculation for the exact final crew and cargo, including both asymmetric routes.</summary>
    public static bool SafeTradeMissionPack(O.TradePackEstimate? pack, int homeTile, int settlementTile)
    {
        if (
            pack is null
            || !pack.HasCanPack
            || !pack.CanPack
            || !pack.HasMassUsage
            || !pack.HasMassCapacity
            || !FoodNumber(pack.MassUsage)
            || !FoodNumber(pack.MassCapacity)
            || pack.MassUsage > pack.MassCapacity
            || !pack.HasFoodDays
            || !pack.HasFoodRotDays
            || !FoodNumber(pack.FoodDays)
            || !FoodNumber(pack.FoodRotDays)
        )
        {
            return false;
        }
        var (days, known) = TradeMissionRouteDays(pack, homeTile, settlementTile);
        return known && pack.FoodDays >= Math.Max(1, days) && pack.FoodRotDays >= Math.Max(1, days);
    }

    public static (double Days, bool Ok) TradeMissionRouteDays(O.TradePackEstimate? pack, int homeTile, int settlementTile)
    {
        if (pack is null)
        {
            return (0, false);
        }
        O.WorldRoute?[] routes = [pack.Outbound, pack.Home];
        for (var i = 0; i < routes.Length; i++)
        {
            var tile = i == 1 ? homeTile : settlementTile;
            var route = routes[i];
            if (route is null || !route.HasDestination || route.Destination != tile || !route.HasReachable || !route.Reachable || !route.HasEstimatedTicks || route.EstimatedTicks < 0)
            {
                return (0, false);
            }
        }
        return (((double)pack.Outbound.EstimatedTicks + (double)pack.Home.EstimatedTicks) / GameTime.TicksPerDay, true);
    }
}
