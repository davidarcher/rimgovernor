using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;


public static partial class TradePolicyRules
{
    /// <summary>
    /// Selects one healthy negotiator, retaining the existing departure policy's last work owners, home staffing
    /// and defense headroom. Claimed pawns include urgent work and other outstanding missions.
    /// </summary>
    public static (IReadOnlyList<PawnId> Crew, QuestSkipReason Reason) TradeMissionCrew(RoundsFacts f, IReadOnlyList<PawnId> claimed)
    {
        if (!f.QuestDeparturePawns.TryGetValue(out var departure))
        {
            return ([], "no_departure_census");
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
            return ([], "no_departure_census");
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
            var (crew, reason) = QuestDeparture.DepartureSquadWithCrew(offer, candidate, false, true);
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
        if (!f.AnimalUpkeep.Food.TryGetValue(out var supply))
        {
            return ([], "no_food_census");
        }
        if (!Finite(capacity) || capacity < 0 || !Finite(roundTripDays) || roundTripDays < 0)
        {
            return ([], "invalid_trip");
        }
        return QuestExpedition.ExpeditionFood(supply, crew, Math.Max(1, roundTripDays), p.FoodMinDays, capacity);
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
                if (!ck || !mk || !FoodForecastRules.FoodNumber(carry) || !FoodForecastRules.FoodNumber(mass) || carry < mass)
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
    public static bool SafeTradeMissionPack(O.TradePackEstimate pack)
    {
        if (pack is not O.TradePackEstimate.Packed packed || packed.MassUsage < 0 || packed.MassUsage > packed.MassCapacity)
        {
            return false;
        }
        var (days, reachable) = TradeMissionRouteDays(packed);
        return reachable && packed.FoodDays >= Math.Max(1, days) && packed.FoodRotDays >= Math.Max(1, days);
    }

    /// <summary>The round trip's days, outbound plus home; false while either leg is unreachable.</summary>
    public static (double Days, bool Reachable) TradeMissionRouteDays(O.TradePackEstimate.Packed pack) =>
        (pack.Outbound, pack.Home) is (O.PackRoute.Reachable outbound, O.PackRoute.Reachable home)
            ? ((double)(outbound.EstimatedTicks + home.EstimatedTicks) / GameTime.TicksPerDay, true)
            : (0, false);
}