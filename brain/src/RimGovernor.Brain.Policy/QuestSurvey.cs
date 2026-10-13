using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public sealed record SurveyPlan
{
    public QuestId Quest { get; init; }

    public string Site { get; init; } = "";

    public CaravanDeparture? Departure { get; init; }

    public bool Waiting { get; init; }

    public bool Return { get; init; }

    public bool Failed { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static partial class PolicyFunctions
{
    private static bool SurveyScannerOf(JoinerOffer offer, string site, out QuestSurveyScanner scanner)
    {
        scanner = new QuestSurveyScanner();
        var found = false;
        foreach (var objective in offer.Objectives)
        {
            if (objective.SurveyScanner.TryGetValue(out var candidate) && candidate.SiteId == site)
            {
                if (found)
                {
                    scanner = new QuestSurveyScanner();
                    return false;
                }
                scanner = candidate;
                found = true;
            }
        }
        return found;
    }

    private static (ExpeditionSite Site, QuestSkipReason Reason) SurveyBudget(JoinerOffer offer, ExpeditionSite site, RoundsFacts f)
    {
        if (!SurveyScannerOf(offer, site.Id, out var scanner))
        {
            return (site, "survey_unknown");
        }
        if (scanner.Alive.TryGetValue(out var alive) && !alive)
        {
            return (site, "scanner_destroyed");
        }
        if (!scanner.DurationTicks.TryGetValue(out var duration) || duration <= 0)
        {
            return (site, "survey_duration_unknown");
        }
        if (scanner.EndTick.TryGetValue(out var end))
        {
            if (!f.QuestObservedTick.TryGetValue(out var now))
            {
                return (site, "survey_duration_unknown");
            }
            duration = Math.Max(0L, end - now.Value);
        }
        site = site with { HoldTicks = Known(duration) };
        var fk = offer.ThreatPoints.TryGetValue(out var forced);
        var ik = site.ThreatPoints.TryGetValue(out var initial);
        if (!fk || !ik || !Finite(forced) || !Finite(initial) || forced < 0 || initial < 0)
        {
            return (site, "threat_unknown");
        }
        site = site with { ThreatPoints = Known(Math.Max(initial, forced)) };
        return (site, "");
    }

    public static QuestSkipReason SurveyAdmission(JoinerOffer offer, RoundsFacts f)
    {
        if (!QuestExpeditionSite(offer, f, out var site))
        {
            return "site_unknown";
        }
        return PlanSurvey(offer, site, f, f.AnimalUpkeep.Food, DefaultRoundsPolicy()).Reason;
    }

    /// <summary>
    /// The original native duration prices the first crew's entire hold. A distinct relief crew must remain
    /// affordable after that departure; none is sent merely because time passed, and the scanner performs its
    /// ordinary passive work.
    /// </summary>
    public static ExpeditionPlan PlanSurvey(JoinerOffer offer, ExpeditionSite site, RoundsFacts f, Fact<FoodSupply> food, RoundsPolicy p)
    {
        var (budget, reason) = SurveyBudget(offer, site, f);
        if (!reason.IsNone)
        {
            return new ExpeditionPlan { Quest = offer.Quest, Site = site.Id, Reason = reason };
        }
        var plan = PlanExpedition(offer, budget, f, food, p);
        if (!plan.Reason.IsNone || plan.Departure is null)
        {
            return plan;
        }
        if (!SurveyReliefFacts(f, plan.Departure.Crew, out var reserve))
        {
            return plan with { Departure = null, Reason = "relief_capacity_unknown" };
        }
        var reserveFood = SurveyReliefFood(food, plan.Departure);
        var relief = PlanExpedition(offer, budget, reserve, reserveFood, p);
        if (!relief.Reason.IsNone || relief.Departure is null)
        {
            return plan with { Departure = null, Reason = "relief_capacity" };
        }
        return plan;
    }

    private static Fact<FoodSupply> SurveyReliefFood(Fact<FoodSupply> food, CaravanDeparture primary)
    {
        if (!food.TryGetValue(out var supply))
        {
            return food;
        }
        var crew = primary.Crew;
        var consumers = supply.Consumers.Where(row => !crew.Contains(row.Id)).ToList();
        var stocks = supply.Stocks.ToList();
        var packed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in primary.Cargo)
        {
            packed[item.Definition] = (long)item.Count;
        }
        for (var i = 0; i < stocks.Count; i++)
        {
            var row = stocks[i] with { Eaters = stocks[i].Eaters.Where(id => !crew.Contains(id)).ToList() };
            stocks[i] = row;
            var remaining = packed.GetValueOrDefault(row.DefName.Value);
            if (remaining == 0)
            {
                continue;
            }
            var ck = row.Count.TryGetValue(out var count);
            var nk = row.Nutrition.TryGetValue(out var nutrition);
            if (!ck || !nk || count <= 0)
            {
                continue;
            }
            var take = Math.Min(count, remaining);
            stocks[i] = row with
            {
                Count = Known(count - take),
                Nutrition = Known(nutrition * ((double)(count - take) / count)),
            };
            packed[row.DefName.Value] -= take;
        }
        return Known(supply with { Consumers = consumers, Stocks = stocks });
    }

    /// <summary>
    /// Accounts for the proposed primary crew in the same home staffing facts used by ordinary departure,
    /// without persisting a reservation.
    /// </summary>
    private static bool SurveyReliefFacts(RoundsFacts f, IReadOnlyList<PawnId> crew, out RoundsFacts reserve)
    {
        reserve = f;
        var hk = f.QuestColonistsAtHome.TryGetValue(out var home);
        var ck = f.DefenseCapacity.TryGetValue(out var capacity);
        var rk = f.QuestDeparturePawns.TryGetValue(out var rows);
        var wk = f.QuestDepartureWork.TryGetValue(out var work);
        var covk = f.WorkRoster.TryGetValue(out var roster);
        var sk = f.QuestSparePawns.TryGetValue(out var spare);
        if (!hk || !ck || !rk || !wk || !covk || !sk)
        {
            return false;
        }
        var coverage = roster!.ToList();
        foreach (var id in crew)
        {
            var found = false;
            foreach (var row in rows!)
            {
                if (row.Id == id)
                {
                    if (!row.DefensePoints.TryGetValue(out var strength))
                    {
                        return false;
                    }
                    capacity -= strength;
                    found = true;
                }
            }
            if (!found)
            {
                return false;
            }
            foreach (var row in work!)
            {
                if (row.Pawn != id)
                {
                    continue;
                }
                foreach (var priority in row.Priorities)
                {
                    if (priority.Priority == 1 && !priority.Disabled)
                    {
                        for (var i = 0; i < coverage.Count; i++)
                        {
                            if (coverage[i].Work == priority.Work)
                            {
                                coverage[i] = coverage[i] with { Owners = coverage[i].Owners - 1 };
                            }
                        }
                    }
                }
            }
        }
        reserve = f with
        {
            QuestColonistsAtHome = Known(home - crew.Count),
            DefenseCapacity = Known(capacity),
            WorkRoster = Known(EquatableArray.From(coverage)),
            QuestSparePawns = Known(EquatableArray.From(spare!.Where(id => !crew.Contains(id)))),
            QuestDeparturePawns = Known(EquatableArray.From(rows!.Where(row => !crew.Contains(row.Id)))),
        };
        return true;
    }

    public static SurveyPlan SurveyWork(JoinerOffer offer, WorldSite site, RoundsFacts f, Fact<FoodSupply> food, RoundsPolicy p)
    {
        var result = new SurveyPlan { Quest = offer.Quest, Site = site.Id, Waiting = true };
        if (offer.State == "EndedFailed")
        {
            return result with { Waiting = false, Return = true, Failed = true, Reason = "quest_failed" };
        }
        if (!SurveyScannerOf(offer, site.Id, out var scanner))
        {
            return result with { Reason = "survey_unknown" };
        }
        if (scanner.Alive.TryGetValue(out var alive) && !alive)
        {
            return result with { Waiting = false, Return = true, Failed = true, Reason = "scanner_destroyed" };
        }
        if (scanner.Complete.TryGetValue(out var complete) && complete)
        {
            return result with { Waiting = false, Return = true };
        }
        var ek = scanner.EndTick.TryGetValue(out var end);
        var nk = f.QuestObservedTick.TryGetValue(out var now);
        if (!ek || !nk)
        {
            return result with { Reason = "survey_duration_unknown" };
        }
        // Elapsed time alone never proves native completion.
        if (end <= now.Value)
        {
            return result with { Reason = "survey_completion_pending" };
        }
        var xk = site.Extraction.TryGetValue(out var extraction);
        var runway = 0.0;
        var rk = xk && extraction!.InventoryFoodDays.TryGetValue(out runway);
        if (!xk || !rk || !Finite(runway) || runway < 0)
        {
            return result with { Reason = "survey_supply_unknown" };
        }
        long homeTicks = 0;
        var routeKnown = false;
        foreach (var route in extraction!.HomeRoutes)
        {
            var reachableKnown = route.Reachable.TryGetValue(out var reachable);
            if (route.TravelTicks.TryGetValue(out var ticks) && reachableKnown && reachable && ticks >= 0)
            {
                homeTicks = Math.Max(homeTicks, ticks);
                routeKnown = true;
            }
        }
        if (!routeKnown)
        {
            return result with { Reason = "home_route_unknown" };
        }
        var remaining = (double)(end - now.Value + homeTicks) / GameTime.TicksPerDay + 1;
        if (extraction.Crew.Count > 0 && runway >= remaining)
        {
            return result;
        }
        // Relief is an ordinary affordable expedition carrying food for the whole remaining hold and return.
        // Existing journeys prevent duplicate relief.
        if (!f.QuestExpeditionTrips.TryGetValue(out var trips))
        {
            return result with { Reason = "journey_unknown" };
        }
        var tileKnown = site.Tile.TryGetValue(out var tile);
        foreach (var trip in trips)
        {
            var dk = trip.Destination.TryGetValue(out var destination);
            if (trip.Forming || (tileKnown && dk && destination == tile))
            {
                return result;
            }
        }
        if (!QuestExpeditionSite(offer, f, out var target))
        {
            return result with { Reason = "site_unknown" };
        }
        var (budget, reason) = SurveyBudget(offer, target, f);
        if (!reason.IsNone)
        {
            return result with { Reason = reason };
        }
        var plan = PlanExpedition(offer, budget, f, food, p);
        return result with { Departure = plan.Departure, Reason = plan.Reason };
    }
}
