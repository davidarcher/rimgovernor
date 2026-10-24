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
    internal static bool QuestExpeditionSite(JoinerOffer offer, RoundsFacts f, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpeditionSite? site)
    {
        site = null;
        if (!f.QuestSites.TryGetValue(out var sites))
        {
            return false;
        }
        WorldSite? selected = null;
        foreach (var candidate in sites)
        {
            if (!candidate.QuestIds.Contains(offer.Quest))
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
            Quest = offer.Quest,
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
        if (!f.QuestOffers.TryGetValue(out var offers))
        {
            return false;
        }
        foreach (var offer in offers)
        {
            if (offer.Profile.TryGetValue(out var profile) && !profile.NeverAct && profile.Disposition != QuestDisposition.Refuse)
            {
                if (f.QuestSites.TryGetValue(out var sites))
                {
                    foreach (var site in sites)
                    {
                        if (!site.QuestIds.Contains(offer.Quest))
                        {
                            continue;
                        }
                        if (site.Place is Pb.SitePlace.Loaded { Map.Extraction: Pb.QuestSiteExtraction.Crew })
                        {
                            return true;
                        }
                    }
                }
                if (offer.State != "Ongoing")
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
        if (!f.QuestOffers.TryGetValue(out var offers))
        {
            return new ExpeditionPlan();
        }
        foreach (var offer in offers.OrderBy(o => o.Quest))
        {
            if (!offer.Profile.TryGetValue(out var profile) || profile.NeverAct || profile.Disposition == QuestDisposition.Refuse || offer.State != "Ongoing")
            {
                continue;
            }
            if (!QuestExpeditionSite(offer, f, out var site))
            {
                continue;
            }
            if (!f.QuestExpeditionTrips.TryGetValue(out var trips))
            {
                return new ExpeditionPlan { Quest = offer.Quest, Site = site.Id, Reason = "journey_unknown" };
            }
            foreach (var trip in trips)
            {
                if (trip is ExpeditionTrip.Forming
                    || trip is ExpeditionTrip.OnWorld { Tile: var tile } && tile == site.Tile
                    || trip is ExpeditionTrip.OnWorld { Course: Pb.CaravanCourse.Moving { Destination: var destination } } && destination == site.Tile)
                {
                    return new ExpeditionPlan { Quest = offer.Quest, Site = site.Id };
                }
            }
            // A live site map proves arrival; its work belongs to the site driver.
            if (f.QuestSites.TryGetValue(out var sites))
            {
                foreach (var row in sites)
                {
                    if (row.Id == site.Id && row.Place is Pb.SitePlace.Loaded)
                    {
                        if (offer.ScriptDef == "SurveySite")
                        {
                            var work = QuestSurvey.SurveyWork(offer, row, f, f.AnimalUpkeep.Food, p);
                            return new ExpeditionPlan { Quest = offer.Quest, Site = site.Id, Departure = work.Departure, Reason = work.Reason };
                        }
                        return new ExpeditionPlan { Quest = offer.Quest, Site = site.Id };
                    }
                }
            }
            var terminal = QuestHackPolicy.WorshippedTerminalAdmission(offer);
            if (!terminal.IsNone)
            {
                return new ExpeditionPlan { Quest = offer.Quest, Site = site.Id, Reason = terminal };
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

    public static QuestSkipReason ExpeditionAdmission(JoinerOffer offer, RoundsFacts f)
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
    public static ExpeditionPlan PlanExpedition(JoinerOffer offer, ExpeditionSite site, RoundsFacts f, Fact<FoodSupply> food, RoundsPolicy p) =>
        PlanExpeditionOf(offer, site, f, food, p, null);

    /// <summary>
    /// Validates a specialized crew through the common staffing, route, mass and food gates. Diplomacy may
    /// select noncombatant colonists.
    /// </summary>
    public static ExpeditionPlan PlanExpeditionCrew(JoinerOffer offer, ExpeditionSite site, RoundsFacts f, Fact<FoodSupply> food, RoundsPolicy p, IReadOnlyList<PawnId> crew)
    {
        if (crew.Count == 0)
        {
            return new ExpeditionPlan { Quest = offer.Quest, Site = site.Id, Reason = "crew_unknown" };
        }
        return PlanExpeditionOf(offer, site, f, food, p, crew);
    }

    private static ExpeditionPlan PlanExpeditionOf(JoinerOffer offer, ExpeditionSite site, RoundsFacts f, Fact<FoodSupply> food, RoundsPolicy p, IReadOnlyList<PawnId>? explicitCrew)
    {
        var result = new ExpeditionPlan { Quest = offer.Quest, Site = site.Id };
        ExpeditionPlan Fail(QuestSkipReason reason) => result with { Reason = reason };

        if (site.Quest != offer.Quest || site.Id.Length == 0 || site.Tile < 0)
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
        if (f.QuestObservedTick.TryGetValue(out var now))
        {
            foreach (var objective in offer.Objectives)
            {
                if (objective.DeadlineTicks.TryGetValue(out var deadline) && (double)deadline - now.Value <= (double)ticks + hold)
                {
                    return Fail("deadline_capacity");
                }
            }
        }
        if (!f.QuestSparePawns.TryGetValue(out var spare))
        {
            return Fail("capacity_unknown");
        }
        if (!f.QuestDeparturePawns.TryGetValue(out var rows))
        {
            return Fail("capacity_unknown");
        }
        var strength = new Dictionary<PawnId, double>();
        foreach (var pawn in rows)
        {
            if (pawn.DefensePoints.TryGetValue(out var value) && TradePolicyRules.Finite(value) && value >= 0)
            {
                strength[pawn.Id] = value;
            }
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
            var mission = offer with
            {
                Profile = Known(new QuestProfile { Family = QuestFamily.BanditCamp }),
                Objectives = [new QuestObjective { Kind = Pb.QuestObjectiveKind.LoadPawns, Count = Known((long)explicitCrew.Count) }],
            };
            var (selected, reason) = QuestDeparture.DepartureSquadWithCrew(mission, f with { QuestSparePawns = Known(EquatableArray.From(filtered)) }, false, true);
            if (!reason.IsNone)
            {
                return Fail(reason);
            }
            crew = selected;
        }
        for (var count = 1; crew.Count == 0 && count <= spare.Count; count++)
        {
            // The common departure selector takes an explicit required headcount.
            var mission = offer with
            {
                Profile = Known(new QuestProfile { Family = QuestFamily.BanditCamp }),
                Objectives = [new QuestObjective { Kind = Pb.QuestObjectiveKind.LoadPawns, Count = Known((long)count) }],
            };
            var (candidate, reason) = QuestDeparture.DepartureSquadOf(mission, f, true);
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
        if (!food.TryGetValue(out var supply))
        {
            return Fail("food_unknown");
        }
        var capacity = 0.0;
        foreach (var id in crew)
        {
            foreach (var pawn in rows)
            {
                if (pawn.Id != id)
                {
                    continue;
                }
                var ck = pawn.CarryCapacity.TryGetValue(out var carry);
                var mk = pawn.CarriedMass.TryGetValue(out var mass);
                if (!ck || !mk || !TradePolicyRules.Finite(carry) || !TradePolicyRules.Finite(mass) || carry < 0 || mass < 0)
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
        if (!supply.Complete.TryGetValue(out var complete) || !complete || !TradePolicyRules.Finite(days) || days < 1)
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
                if (!consumer.NutritionPerDay.TryGetValue(out var rate) || !TradePolicyRules.Finite(rate) || rate <= 0)
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
            var owned = stock.Holder.TryGetValue(out var holder);
            var ck = stock.Count.TryGetValue(out var count);
            var nk = stock.Nutrition.TryGetValue(out var nutrition);

            var fk = stock.Forbidden.TryGetValue(out var forbidden);
            var mk = stock.UnitMass.TryGetValue(out var mass);
            if (!owned || !holder.IsEmpty || !ck || count <= 0 || !nk || !TradePolicyRules.Finite(nutrition) || nutrition <= 0 || stock.Corpse || stock.IsHumanMeat || stock.RawMeat
                || !fk || forbidden || stock.Reserve || stock.DefName.Value.Length == 0 || !mk || !TradePolicyRules.Finite(mass) || mass < 0)
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
                stocks[index] = stock with { Count = Known(count - mid), Nutrition = Known(nutrition - mid * unit) };
                var runwayKnown = false;
                var runway = 0.0;
                try
                {
                    runwayKnown = FoodForecastRules.ForecastFood(remaining, home).RunwayDays.TryGetValue(out runway);
                }
                catch (DomainException)
                {
                    runwayKnown = false;
                }
                if (runwayKnown && runway >= homeDays)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            stocks[index] = stock with { Count = Known(count - lo), Nutrition = Known(nutrition - lo * unit) };
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
