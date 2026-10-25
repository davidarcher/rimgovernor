using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A derived native route and target. A reachable route's crew is the one used for the conservative travel
/// estimate; a selected crew must be a subset. Layered destinations cannot use the surface caravan action.
/// </summary>
public sealed record ExpeditionSite
{
    public required string Id { get; init; }

    public required QuestId Quest { get; init; }

    public required int Tile { get; init; }

    public required int Layer { get; init; }

    public required Pb.SiteRoute Route { get; init; }

    public required ExpeditionThreat Threat { get; init; }

    public required long HoldTicks { get; init; }
}

/// <summary>
/// The hostile points an expedition must out-fight: a bounded sum of the site's generated and pending threats,
/// or unbounded when the site's threats are open-ended or not a site's at all.
/// </summary>
public abstract record ExpeditionThreat
{
    private protected ExpeditionThreat() { }

    public sealed record Bounded(double Points) : ExpeditionThreat;

    public sealed record Unbounded : ExpeditionThreat;
}

public sealed record ExpeditionPlan
{
    public QuestId Quest { get; init; }

    public string Site { get; init; } = "";

    public CaravanDeparture? Departure { get; init; }

    public QuestSkipReason Reason { get; init; }
}

/// <summary>
/// A caravan the colony has out with its crew: Forming on the home map, or OnWorld at a tile with its course and its
/// routes to the player homes.
/// </summary>
public abstract record ExpeditionTrip
{
    private protected ExpeditionTrip() { }

    public required IReadOnlyList<PawnId> PawnIds { get; init; }

    public sealed record Forming : ExpeditionTrip;

    public sealed record OnWorld : ExpeditionTrip
    {
        public required int Tile { get; init; }

        public required Pb.CaravanCourse Course { get; init; }

        public required IReadOnlyList<Pb.WorldRoute> HomeRoutes { get; init; }
    }
}

public static class QuestExpedition
{
    internal static bool QuestExpeditionSite(QuestState offer, RoundsFacts f, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpeditionSite? site)
    {
        site = null;
        WorldSite? selected = null;
        foreach (var candidate in f.QuestSites)
        {
            if (!candidate.QuestIds.Contains(offer.Id))
            {
                continue;
            }
            if (selected is not null)
            {
                return false;
            }
            selected = candidate;
        }
        if (selected is null)
        {
            return false;
        }
        if (selected.State == Pb.WorldSiteState.Destroyed)
        {
            return false;
        }
        int tile, layer;
        if (selected.Place is Pb.SitePlace.OnTile on)
        {
            (tile, layer) = (on.Tile, on.Layer);
        }
        else if (selected.Place is Pb.SitePlace.Loaded loaded)
        {
            (tile, layer) = (loaded.Tile, loaded.Layer);
        }
        else
        {
            return false;
        }
        site = new ExpeditionSite
        {
            Id = selected.Id,
            Quest = offer.Id,
            Tile = tile,
            Layer = layer,
            Route = selected.Route,
            Threat = selected.Security is Pb.SiteThreatBudget.Bounded b
                ? new ExpeditionThreat.Bounded(b.InitialPoints + b.PendingRaidPoints)
                : new ExpeditionThreat.Unbounded(),
            HoldTicks = 0,
        };
        return true;
    }

    public static bool ExpeditionDeficit(RoundsFacts f)
    {
        foreach (var offer in f.QuestOffers)
        {
            if (f.RootOf(offer) is QuestRoot.Classified { Profile: var profile } && !profile.NeverAct && profile.Disposition != QuestDisposition.Refuse)
            {
                foreach (var site in f.QuestSites)
                {
                    if (!site.QuestIds.Contains(offer.Id))
                    {
                        continue;
                    }
                    if (site.Place is Pb.SitePlace.Loaded { Map.Extraction: Pb.QuestSiteExtraction.Crew })
                    {
                        return true;
                    }
                }
                if (offer.State != QuestStatus.Ongoing)
                {
                    continue;
                }
                if (QuestExpeditionSite(offer, f, out _))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static ExpeditionPlan SelectExpedition(RoundsFacts f, RoundsPolicy p)
    {
        foreach (var offer in f.QuestOffers.OrderBy(o => o.Id))
        {
            if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile } || profile.NeverAct || profile.Disposition == QuestDisposition.Refuse || offer.State != QuestStatus.Ongoing)
            {
                continue;
            }
            if (!QuestExpeditionSite(offer, f, out var site))
            {
                continue;
            }
            foreach (var trip in f.QuestExpeditionTrips)
            {
                if (trip is ExpeditionTrip.Forming
                    || trip is ExpeditionTrip.OnWorld { Tile: var tile } && tile == site.Tile
                    || trip is ExpeditionTrip.OnWorld { Course: Pb.CaravanCourse.Moving { Destination: var destination } } && destination == site.Tile)
                {
                    return new ExpeditionPlan { Quest = offer.Id, Site = site.Id };
                }
            }
            // A live site map proves arrival; its work belongs to the site driver.
            foreach (var row in f.QuestSites)
            {
                if (row.Id == site.Id && row.Place is Pb.SitePlace.Loaded)
                {
                    if (offer.ScriptDef == "SurveySite")
                    {
                        var work = QuestSurvey.SurveyWork(offer, row, f, f.AnimalUpkeep.Food, p);
                        return new ExpeditionPlan { Quest = offer.Id, Site = site.Id, Departure = work.Departure, Reason = work.Reason };
                    }
                    return new ExpeditionPlan { Quest = offer.Id, Site = site.Id };
                }
            }
            var terminal = QuestHackPolicy.WorshippedTerminalAdmission(offer);
            if (!terminal.IsNone)
            {
                return new ExpeditionPlan { Quest = offer.Id, Site = site.Id, Reason = terminal };
            }
            if (offer.ScriptDef == "SurveySite")
            {
                return QuestSurvey.PlanSurvey(offer, site, f, f.AnimalUpkeep.Food, p);
            }
            if (offer.ScriptDef == "OpportunitySite_PeaceTalks")
            {
                return QuestPeaceTalksPolicy.PlanPeaceTalks(offer, site, f, f.AnimalUpkeep.Food, p);
            }
            return PlanExpedition(offer, site, f, f.AnimalUpkeep.Food, p);
        }
        return new ExpeditionPlan();
    }

    public static QuestSkipReason ExpeditionAdmission(QuestState offer, RoundsFacts f)
    {
        if (offer.ScriptDef == "SurveySite")
        {
            return QuestSurvey.SurveyAdmission(offer, f);
        }
        if (offer.ScriptDef == "OpportunitySite_PeaceTalks")
        {
            return QuestPeaceTalksPolicy.PeaceTalksAdmission(offer, f);
        }
        if (!QuestExpeditionSite(offer, f, out var site))
        {
            return "site_unknown";
        }
        return PlanExpedition(offer, site, f, f.AnimalUpkeep.Food, RoundsRules.DefaultRoundsPolicy()).Reason;
    }

    /// <summary>
    /// Shares departure staffing and native defense accounting with shuttle missions. It packs only observed
    /// food that every selected pawn eats, survives the round trip, and leaves the colony's diet-aware
    /// reserve intact.
    /// </summary>
    public static ExpeditionPlan PlanExpedition(QuestState offer, ExpeditionSite site, RoundsFacts f, FoodSupply food, RoundsPolicy p) =>
        PlanExpeditionOf(offer, site, f, food, p, null);

    /// <summary>
    /// Validates a specialized crew through the common staffing, route, mass and food gates. Diplomacy may
    /// select noncombatant colonists.
    /// </summary>
    public static ExpeditionPlan PlanExpeditionCrew(QuestState offer, ExpeditionSite site, RoundsFacts f, FoodSupply food, RoundsPolicy p, IReadOnlyList<PawnId> crew)
    {
        if (crew.Count == 0)
        {
            return new ExpeditionPlan { Quest = offer.Id, Site = site.Id, Reason = "crew_unknown" };
        }
        return PlanExpeditionOf(offer, site, f, food, p, crew);
    }

    private static ExpeditionPlan PlanExpeditionOf(QuestState offer, ExpeditionSite site, RoundsFacts f, FoodSupply food, RoundsPolicy p, IReadOnlyList<PawnId>? explicitCrew)
    {
        var result = new ExpeditionPlan { Quest = offer.Id, Site = site.Id };
        ExpeditionPlan Fail(QuestSkipReason reason) => result with { Reason = reason };

        if (site.Quest != offer.Id || site.Id.Length == 0 || site.Tile < 0)
        {
            return Fail("site_unknown");
        }
        if (site.Layer != 0 || site.Route is Pb.SiteRoute.OffSurface)
        {
            return Fail("ship_only");
        }
        if (site.Route is Pb.SiteRoute.Unreachable)
        {
            return Fail("route_unreachable");
        }
        var hold = site.HoldTicks;
        if (site.Route is not Pb.SiteRoute.Reachable route || route.TravelTicks < 0 || hold < 0)
        {
            return Fail("route_unknown");
        }
        var ticks = route.TravelTicks;
        if (site.Threat is not ExpeditionThreat.Bounded { Points: var threat } || !TradePolicyRules.Finite(threat) || threat < 0)
        {
            return Fail("threat_unknown");
        }
        var tripTicks = (double)ticks * 2 + hold + GameTime.TicksPerDay;
        // Offer expiry only limits acceptance. Site objective deadlines, when present, limit travel after
        // acceptance.
        foreach (var objective in offer.Objectives)
        {
            if (objective.Timing is Pb.QuestObjectiveTiming.Deadline due && (double)due.Tick - f.QuestObservedTick.Value <= (double)ticks + hold)
            {
                return Fail("deadline_capacity");
            }
        }
        if (f.QuestRoster is not QuestRoster.Planned planned || f.QuestPawns is not QuestPawns.Seen { Departures: var rows })
        {
            return Fail("capacity_unknown");
        }
        var spare = planned.Spare;
        var strength = new Dictionary<PawnId, double>();
        foreach (var pawn in rows)
        {
            strength[pawn.Id] = pawn.DefensePoints;
        }
        IReadOnlyList<PawnId> crew = [];
        if (explicitCrew is { Count: > 0 })
        {
            var filtered = new List<PawnId>();
            foreach (var id in explicitCrew)
            {
                if (!spare.Contains(id) || filtered.Contains(id))
                {
                    return Fail("no_spare_pawn");
                }
                filtered.Add(id);
            }
            var (selected, reason) = QuestDeparture.DepartureSquadFor(offer.Id, QuestFamily.BanditCamp, explicitCrew.Count, f with { QuestRoster = planned with { Spare = EquatableArray.From(filtered) } }, false, true);
            if (!reason.IsNone)
            {
                return Fail(reason);
            }
            crew = selected;
        }
        for (var count = 1; crew.Count == 0 && count <= spare.Count; count++)
        {
            // The common departure selector takes an explicit required headcount.
            var (candidate, reason) = QuestDeparture.DepartureSquadFor(offer.Id, QuestFamily.BanditCamp, count, f, true, false);
            if (!reason.IsNone)
            {
                return Fail(reason);
            }
            var total = 0.0;
            foreach (var id in candidate)
            {
                total += strength.GetValueOrDefault(id);
            }
            if (total >= threat)
            {
                crew = candidate;
                break;
            }
        }
        if (crew.Count == 0)
        {
            return Fail("expedition_strength");
        }
        var points = 0.0;
        foreach (var id in crew)
        {
            points += strength.GetValueOrDefault(id);
        }
        if (points < threat)
        {
            return Fail("expedition_strength");
        }
        foreach (var id in crew)
        {
            if (!route.Crew.Contains(id))
            {
                return Fail("route_crew_unknown");
            }
        }
        var supply = food;
        var capacity = 0.0;
        foreach (var id in crew)
        {
            foreach (var pawn in rows)
            {
                if (pawn.Id != id)
                {
                    continue;
                }
                var (carry, mass) = (pawn.CarryCapacity, pawn.CarriedMass);
                if (!TradePolicyRules.Finite(carry) || !TradePolicyRules.Finite(mass) || carry < 0 || mass < 0)
                {
                    return Fail("mass_unknown");
                }
                capacity += carry - mass;
            }
        }
        var (cargo, foodReason) = ExpeditionFood(supply, crew, tripTicks / GameTime.TicksPerDay, p.FoodMinDays, capacity);
        if (!foodReason.IsNone)
        {
            return Fail(foodReason);
        }
        if (!CaravanDeparture.TryCreate(crew.Select(p => (Domain.PawnId)p), cargo, site.Tile, out var departure))
        {
            return Fail("cargo_invalid");
        }
        return result with { Departure = departure };
    }

    internal static (IReadOnlyList<CargoItem> Cargo, QuestSkipReason Reason) ExpeditionFood(FoodSupply supply, IReadOnlyList<PawnId> crew, double days, double homeDays, double capacity)
    {
        IReadOnlyList<CargoItem> none = [];
        if (!TradePolicyRules.Finite(days) || days < 1)
        {
            return (none, "food_unknown");
        }
        var need = 0.0;
        var crewSeen = new HashSet<PawnId>();
        var home = new List<PawnId>();
        foreach (var consumer in supply.Consumers)
        {
            if (crew.Contains(consumer.Id))
            {
                crewSeen.Add(consumer.Id);
                var rate = consumer.NutritionPerDay;
                if (!TradePolicyRules.Finite(rate) || rate <= 0)
                {
                    return (none, "food_unknown");
                }
                need += rate * days;
            }
            else
            {
                home.Add(consumer.Id);
            }
        }
        if (need <= 0 || home.Count == 0 || crewSeen.Count != crew.Count)
        {
            return (none, "food_unknown");
        }
        var stocks = supply.Stocks.ToList();
        var remaining = supply with { Stocks = stocks };
        var indices = Enumerable.Range(0, stocks.Count).ToList();
        indices.Sort((a, b) => string.CompareOrdinal(stocks[a].Id, stocks[b].Id));
        var packed = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            var stock = stocks[index];
            var count = stock.Count;
            var nutrition = stock.Nutrition;
            var mass = stock.UnitMass;
            if (!stock.Holder.IsEmpty || count <= 0 || !TradePolicyRules.Finite(nutrition) || nutrition <= 0 || stock.Corpse || stock.IsHumanMeat || stock.RawMeat
                || stock.Forbidden || stock.Reserve || stock.DefName.Value.Length == 0 || !TradePolicyRules.Finite(mass) || mass < 0)
            {
                continue;
            }
            if (stock.Rot is FoodRot.Rots { Ticks: var rot } && rot <= days * GameTime.TicksPerDay)
            {
                continue;
            }
            var eligible = true;
            foreach (var id in crew)
            {
                if (!stock.Eaters.Contains(id))
                {
                    eligible = false;
                    break;
                }
            }
            if (!eligible)
            {
                continue;
            }
            var unit = nutrition / count;
            var take = Math.Min(count, (long)Math.Ceiling(need / unit));
            if (mass > 0)
            {
                take = Math.Min(take, Math.Max(0L, (long)Math.Floor(capacity / mass)));
            }
            // Find the largest pack from this stack that leaves every home consumer at the minimum runway.
            // ForecastFood accounts for competing diets.
            long lo = 0, hi = take;
            while (lo < hi)
            {
                var mid = lo + (hi - lo + 1) / 2;
                stocks[index] = stock with { Count = count - mid, Nutrition = nutrition - mid * unit };
                var fits = false;
                try
                {
                    fits = FoodForecastRules.ForecastFood(remaining, home).RunwayDays is FoodRunway.Days { Value: var runway } && runway >= homeDays;
                }
                catch (DomainException)
                {
                    fits = false;
                }
                if (fits)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            stocks[index] = stock with { Count = count - lo, Nutrition = nutrition - lo * unit };
            packed[stock.DefName.Value] = packed.GetValueOrDefault(stock.DefName.Value) + (ulong)lo;
            need -= lo * unit;
            capacity -= lo * mass;
            if (need <= 0)
            {
                break;
            }
        }
        if (need > 0)
        {
            return (none, "food_capacity");
        }
        var cargo = packed.Where(kv => kv.Value > 0)
            .Select(kv => new CargoItem(kv.Key, kv.Value))
            .OrderBy(c => c.Definition, StringComparer.Ordinal)
            .ToList();
        return (cargo, "");
    }
}
