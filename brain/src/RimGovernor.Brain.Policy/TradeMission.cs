
namespace RimGovernor.Brain.Policy;


public static partial class TradePolicyRules
{
    /// <summary>
    /// Selects one healthy negotiator, retaining the existing departure policy's last work owners, home staffing
    /// and defense headroom. Claimed pawns include urgent work and other outstanding missions.
    /// </summary>
    public static (IReadOnlyList<PawnId> Crew, QuestSkipReason Reason) TradeMissionCrew(ReviewFacts f, IReadOnlyList<PawnId> claimed)
    {
        if (f.QuestPawns is not QuestPawns.Seen { Departures: var departure } || f.QuestRoster is not QuestRoster.Planned planned)
        {
            return ([], "no_departure_census");
        }
        var rows = departure
            .OrderBy(
                x => x,
                Comparer<QuestDeparturePawn>.Create(
                    (x, y) =>
                    {
                        switch (x.Negotiation, y.Negotiation)
                        {
                            case (RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Able a, RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Able b) when a.Ability != b.Ability:
                                return a.Ability > b.Ability ? -1 : 1;
                            case (RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Able, RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Incapable):
                                return -1;
                            case (RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Incapable, RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Able):
                                return 1;
                        }
                        return x.Id.CompareTo(y.Id);
                    }
                )
            )
            .ToList();
        foreach (var row in rows)
        {
            if (row.Negotiation is not RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Able(var ability, _) || !Finite(ability) || ability <= 0 || claimed.Contains(row.Id) || !planned.Spare.Contains(row.Id))
            {
                continue;
            }
            var candidate = f with { QuestRoster = planned with { Spare = EquatableArray.From([row.Id]) } };
            // A trade caravan is no quest: no open quest's departures are its own.
            var (crew, reason) = QuestDeparture.DepartureSquadFor(default, QuestFamily.BanditCamp, 1, candidate, false, true);
            if (reason.Value.Length == 0)
            {
                return (crew, reason);
            }
        }
        return ([], "no_safe_negotiator");
    }

    /// <summary>Uses observed dietary access and rot, leaving every home consumer's reserve. Capacity excludes the observed silver mass.</summary>
    public static (IReadOnlyList<CargoItem> Cargo, QuestSkipReason Reason) TradeMissionFood(ReviewFacts f, IReadOnlyList<PawnId> crew, double roundTripDays, double capacity, PlannerSettings p)
    {
        var supply = f.AnimalUpkeep.Food;
        if (!Finite(capacity) || capacity < 0 || !Finite(roundTripDays) || roundTripDays < 0)
        {
            return ([], "invalid_trip");
        }
        return QuestExpedition.ExpeditionFood(supply, crew, Math.Max(1, roundTripDays), p.FoodMinDays, capacity);
    }

    public static (double Capacity, bool Ok) TradeMissionCarryCapacity(ReviewFacts f, IReadOnlyList<PawnId> crew)
    {
        if (f.QuestPawns is not QuestPawns.Seen { Departures: var rows })
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
                var (carry, mass) = (row.CarryCapacity, row.CarriedMass);
                if (!FoodForecastRules.FoodNumber(carry) || !FoodForecastRules.FoodNumber(mass) || carry < mass)
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
    public static bool SafeTradeMissionPack(RimGovernor.Brain.GameFacts.Observations.TradePackEstimate pack)
    {
        if (pack is not RimGovernor.Brain.GameFacts.Observations.TradePackEstimate.Packed packed || packed.MassUsage < 0 || packed.MassUsage > packed.MassCapacity)
        {
            return false;
        }
        var (days, reachable) = TradeMissionRouteDays(packed);
        return reachable && packed.FoodDays >= Math.Max(1, days) && packed.FoodRotDays >= Math.Max(1, days);
    }

    /// <summary>The round trip's days, outbound plus home; false while either leg is unreachable.</summary>
    public static (double Days, bool Reachable) TradeMissionRouteDays(RimGovernor.Brain.GameFacts.Observations.TradePackEstimate.Packed pack) =>
        (pack.Outbound, pack.Home) is (RimGovernor.Brain.GameFacts.Observations.PackRoute.Reachable outbound, RimGovernor.Brain.GameFacts.Observations.PackRoute.Reachable home)
            ? ((double)(outbound.EstimatedTicks + home.EstimatedTicks) / GameTime.TicksPerDay, true)
            : (0, false);
}