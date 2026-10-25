using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

public sealed record SurveyPlan
{
    public CaravanDeparture? Departure { get; init; }

    public bool Return { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static class QuestSurvey
{
    /// <summary>The site's one survey scanner hold; null when no objective or more than one names the site.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.QuestSurveyScanner? SurveyScannerOf(QuestState offer, string site)
    {
        RimGovernor.Brain.GameFacts.Observations.QuestSurveyScanner? scanner = null;
        foreach (var objective in offer.Objectives)
        {
            if (objective.SurveyScanner is { } candidate && candidate.SiteId == site)
            {
                if (scanner is not null)
                {
                    return null;
                }
                scanner = candidate;
            }
        }
        return scanner;
    }

    private static (ExpeditionSite Site, QuestSkipReason Reason) SurveyBudget(QuestState offer, ExpeditionSite site, ReviewFacts f)
    {
        if (SurveyScannerOf(offer, site.Id) is not { } scanner)
        {
            return (site, "survey_unknown");
        }
        if (scanner.Scanner == RimGovernor.Brain.GameFacts.Observations.SurveyScannerCondition.Destroyed)
        {
            return (site, "scanner_destroyed");
        }
        var duration = scanner.DurationTicks;
        if (duration <= 0)
        {
            return (site, "survey_duration_unknown");
        }
        if (scanner.Hold is RimGovernor.Brain.GameFacts.Observations.SurveyHold.Running running)
        {
            duration = Math.Max(0L, running.EndTick.Value - f.QuestObservedTick.Value);
        }
        site = site with { HoldTicks = duration };
        var forced = offer.Threat is RimGovernor.Brain.GameFacts.Observations.QuestThreat.Points points ? points.Value : 0;
        if (site.Threat is not ExpeditionThreat.Bounded { Points: var initial } || !TradePolicyRules.Finite(initial) || initial < 0)
        {
            return (site, "threat_unknown");
        }
        site = site with { Threat = new ExpeditionThreat.Bounded(Math.Max(initial, forced)) };
        return (site, "");
    }

    public static QuestSkipReason SurveyAdmission(QuestState offer, ReviewFacts f)
    {
        if (!QuestExpedition.QuestExpeditionSite(offer, f, out var site))
        {
            return "site_unknown";
        }
        return PlanSurvey(offer, site, f, f.AnimalUpkeep.Food, Rules.DefaultPlannerSettings()).Reason;
    }

    /// <summary>
    /// The original native duration prices the first crew's entire hold. A distinct relief crew must remain
    /// affordable after that departure; none is sent merely because time passed, and the scanner performs its
    /// ordinary passive work.
    /// </summary>
    public static ExpeditionPlan PlanSurvey(QuestState offer, ExpeditionSite site, ReviewFacts f, FoodSupply food, PlannerSettings p)
    {
        var (budget, reason) = SurveyBudget(offer, site, f);
        if (!reason.IsNone)
        {
            return new ExpeditionPlan { Quest = offer.Id, Site = site.Id, Reason = reason };
        }
        var plan = QuestExpedition.PlanExpedition(offer, budget, f, food, p);
        if (!plan.Reason.IsNone || plan.Departure is null)
        {
            return plan;
        }
        if (!SurveyReliefFacts(f, plan.Departure.Crew, out var reserve))
        {
            return plan with { Departure = null, Reason = "relief_capacity_unknown" };
        }
        var reserveFood = SurveyReliefFood(food, plan.Departure);
        var relief = QuestExpedition.PlanExpedition(offer, budget, reserve, reserveFood, p);
        if (!relief.Reason.IsNone || relief.Departure is null)
        {
            return plan with { Departure = null, Reason = "relief_capacity" };
        }
        return plan;
    }

    private static FoodSupply SurveyReliefFood(FoodSupply food, CaravanDeparture primary)
    {
        var supply = food;
        var crew = primary.Crew;
        var consumers = supply.Consumers.Where(row => !crew.Items.Contains((Domain.PawnId)row.Id)).ToList();
        var stocks = supply.Stocks.ToList();
        var packed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in primary.Cargo)
        {
            packed[item.Definition] = (long)item.Count;
        }
        for (var i = 0; i < stocks.Count; i++)
        {
            var row = stocks[i] with { Eaters = stocks[i].Eaters.Where(id => !crew.Items.Contains((Domain.PawnId)id)).ToList() };
            stocks[i] = row;
            var remaining = packed.GetValueOrDefault(row.DefName);
            if (remaining == 0)
            {
                continue;
            }
            var count = row.Count;
            if (count <= 0)
            {
                continue;
            }
            var take = Math.Min(count, remaining);
            stocks[i] = row with
            {
                Count = count - take,
                Nutrition = row.Nutrition * ((double)(count - take) / count),
            };
            packed[row.DefName] -= take;
        }
        return supply with { Consumers = consumers, Stocks = stocks };
    }

    /// <summary>
    /// Accounts for the proposed primary crew in the same home staffing facts used by ordinary departure,
    /// without persisting a reservation.
    /// </summary>
    private static bool SurveyReliefFacts(ReviewFacts f, EquatableArray<Domain.PawnId> crew, out ReviewFacts reserve)
    {
        reserve = f;
        var capacity = f.DefenseCapacity;
        var covk = f.WorkRoster is WorkRosterRead.Read;
        var roster = f.WorkRoster is WorkRosterRead.Read { Coverage: var rosterRead } ? rosterRead : default;
        if (f.QuestPawns is not QuestPawns.Seen seen || f.QuestRoster is not QuestRoster.Planned planned || !covk)
        {
            return false;
        }
        var (home, rows, work) = (seen.AtHome, seen.Departures, planned.Work);
        var coverage = roster.ToList();
        foreach (var id in crew)
        {
            var found = false;
            foreach (var row in rows)
            {
                if (row.Id == (PawnId)id)
                {
                    capacity -= row.DefensePoints;
                    found = true;
                }
            }
            if (!found)
            {
                return false;
            }
            foreach (var row in work)
            {
                if (row.Pawn != (PawnId)id)
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
            QuestPawns = seen with { AtHome = home - crew.Count, Departures = EquatableArray.From(rows.Where(row => !crew.Items.Contains((Domain.PawnId)row.Id))) },
            DefenseCapacity = capacity,
            WorkRoster = new WorkRosterRead.Read(EquatableArray.From(coverage)),
            QuestRoster = planned with { Spare = EquatableArray.From(planned.Spare.Where(id => !crew.Items.Contains((Domain.PawnId)id))) },
        };
        return true;
    }

    public static SurveyPlan SurveyWork(QuestState offer, WorldSite site, ReviewFacts f, FoodSupply food, PlannerSettings p)
    {
        var result = new SurveyPlan();
        if (offer.State == QuestStatus.EndedFailed)
        {
            return result with { Return = true, Reason = "quest_failed" };
        }
        if (SurveyScannerOf(offer, site.Id) is not { } scanner)
        {
            return result with { Reason = "survey_unknown" };
        }
        if (scanner.Scanner == RimGovernor.Brain.GameFacts.Observations.SurveyScannerCondition.Destroyed)
        {
            return result with { Return = true, Reason = "scanner_destroyed" };
        }
        if (scanner.Complete)
        {
            return result with { Return = true };
        }
        if (scanner.Hold is not RimGovernor.Brain.GameFacts.Observations.SurveyHold.Running running)
        {
            return result with { Reason = "survey_hold_stopped" };
        }
        var now = f.QuestObservedTick;
        var end = running.EndTick.Value;
        // Elapsed time alone never proves native completion.
        if (end <= now.Value)
        {
            return result with { Reason = "survey_completion_pending" };
        }
        if (site.Place is not RimGovernor.Brain.GameFacts.Observations.SitePlace.Loaded loaded)
        {
            return result with { Reason = "survey_supply_unknown" };
        }
        // A loaded site with no able crew left needs relief; it has no runway or routes of its own.
        if (loaded.Map.Extraction is RimGovernor.Brain.GameFacts.Observations.QuestSiteExtraction.Crew { Value: var extraction })
        {
            long homeTicks = 0;
            var routeKnown = false;
            foreach (var route in extraction.HomeRoutes)
            {
                if (route.Travel is RimGovernor.Brain.GameFacts.Observations.SiteTravel.Reachable { TravelTicks: var ticks } && ticks >= 0)
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
            if (extraction.InventoryFoodDays >= remaining)
            {
                return result;
            }
        }
        // Relief is an ordinary affordable expedition carrying food for the whole remaining hold and return.
        // Existing journeys prevent duplicate relief.
        foreach (var trip in f.QuestExpeditionTrips)
        {
            if (trip is ExpeditionTrip.Forming || trip is ExpeditionTrip.OnWorld { Course: RimGovernor.Brain.GameFacts.Observations.CaravanCourse.Moving { Destination: var destination } } && destination == loaded.Tile)
            {
                return result;
            }
        }
        if (!QuestExpedition.QuestExpeditionSite(offer, f, out var target))
        {
            return result with { Reason = "site_unknown" };
        }
        var (budget, reason) = SurveyBudget(offer, target, f);
        if (!reason.IsNone)
        {
            return result with { Reason = reason };
        }
        var plan = QuestExpedition.PlanExpedition(offer, budget, f, food, p);
        return result with { Departure = plan.Departure, Reason = plan.Reason };
    }
}
