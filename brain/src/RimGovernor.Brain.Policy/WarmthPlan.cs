namespace RimGovernor.Brain.Policy;

// The warmth budget (#2765, epic #3578). While extreme cold holds outdoor work, each colonist may make bounded
// trips outdoors. PlanTrips picks the trip set from the hypothermia estimator (OutdoorBudgetFor's formula):
// trips are ranked by the need they serve (WarmthTier, strict) and then by value per exposed tick, each is an
// out-and-back leg plus the work at the site, and the set is cut where the pawn's hypothermia would reach
// WarmthSeverityCap by the end of the last return. Hypothermia slows the pawn (the Moving capacity offsets of
// the vanilla stages), so a leg is walked at the speed of the severity it starts at, not the speed the pawn
// left at.
//
// Every trip is counted from home with no rest between trips. A pawn's window is planned once, from the severity
// the pawn starts at, and held in Latches.Warmth until its bound (ReviewWarmth); WarmthWindow.Until is the one
// bound every consumer reads (OpenAt). A pawn still outside at the bound is sheltered again, which recalls it
// through the Safe area.
//
// Every available colonist gets its own window (#4049), and a pawn whose window has closed gets a new one only
// once its observed hypothermia has fallen back to WarmthRefillSeverity. Nothing models the refill; the body
// read carries the severity each review, and HypothermiaRecovery only predicts the tick the refill is reached
// (WarmthRefillTicks) for the hold reason.
//
// A trip is any outdoor task the cold hold blocks (#4050): a loot pickup, a salvage, a lone hunt or a forage.
// WarmthCandidates assembles them from the loot census, the clearance census's salvage evidence and the
// acquisition census; WarmthNeedsOf reads the needs the tiers rank by.

/// <summary>What a warmth trip does at its site.</summary>
public enum WarmthTripKind
{
    Loot,
    Salvage,
    LoneHunt,
    Forage,
}

/// <summary>
/// The need a trip serves, in strict rank order: a lower tier never displaces a higher one, whatever its value.
/// ShelterMaterials is a resource the planned construction still owes beyond the stock, Fuel a fuel the fuel runway
/// is short of, Food any food while the food supply is in deficit, Other everything else.
/// </summary>
public enum WarmthTier
{
    ShelterMaterials = 0,
    Fuel = 1,
    Food = 2,
    Other = 3,
}

/// <summary>
/// One possible trip: the one-way path in cells from home, the exposed ticks worked at the site, the value gained
/// and the tier the value is ranked within.
/// </summary>
public sealed record WarmthCandidate
{
    public string Id { get; init; } = "";

    public WarmthTripKind Kind { get; init; }

    public WarmthTier Tier { get; init; } = WarmthTier.Other;

    public double Value { get; init; }

    public double PathCells { get; init; }

    public double WorkTicks { get; init; } = WarmthPlanRules.WarmthPickupTicks;
}

/// <summary>One planned trip. Exposure is its planned ticks outdoors; Kind and Id name the task the window admits.</summary>
public sealed record WarmthTrip
{
    public string Id { get; init; } = "";

    public WarmthTripKind Kind { get; init; }

    public double Value { get; init; }

    public int Exposure { get; init; }
}

/// <summary>
/// The pure input of PlanTrips: the ambient temperature, the pawn's comfortable minimum, current hypothermia and
/// move speed (cells per second, at that severity), and the candidate trips.
/// </summary>
public sealed record WarmthRequest
{
    public Tick Now { get; init; }

    public double Temperature { get; init; }

    public double ComfortMin { get; init; }

    public double Severity { get; init; }

    public double Speed { get; init; }

    public EquatableArray<WarmthCandidate> Candidates { get; init; }

    public HypothermiaTuning Tuning { get; init; } = new();
}

/// <summary>PlanTrips's result. Until is the tick the last planned return completes; zero when no window opens.</summary>
public sealed record WarmthPlan
{
    public EquatableArray<WarmthTrip> Trips { get; init; }

    public Tick Until { get; init; }
}

/// <summary>One pawn's warmth window: the pawn allowed outdoors through extreme cold, the trips planned, and the tick it closes.</summary>
public sealed record WarmthWindow
{
    public PawnId Pawn { get; init; }

    public Tick Until { get; init; }

    public EquatableArray<WarmthTrip> Trips { get; init; }

    /// <summary>The single close rule: the window is open before its bound and closed at it. Every consumer reads this; none keeps its own timer.</summary>
    public bool OpenAt(Tick now) => !Pawn.IsEmpty && now < Until;

    /// <summary>Whether the window plans the trip of <paramref name="kind"/> on <paramref name="id"/>.</summary>
    public bool Names(WarmthTripKind kind, string id)
    {
        foreach (var t in Trips)
        {
            if (t.Kind == kind && t.Id == id)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>The needs the tiers read: the resources short for planned construction, the fuels the runway is short of, and whether food is.</summary>
public sealed record WarmthNeeds
{
    public IReadOnlySet<Resource> ShelterMaterials { get; init; } = new HashSet<Resource>();

    public IReadOnlySet<Resource> Fuel { get; init; } = new HashSet<Resource>();

    public bool Food { get; init; }
}

public static class WarmthPlanRules
{
    /// <summary>
    /// The hypothermia severity a trip never reaches: the serious stage floor (Moving -0.3, Manipulation -0.5,
    /// pain). The estimator plans the whole trip set, return included, to stay below it.
    /// </summary>
    public const double WarmthSeverityCap = DashEstimateRules.HypothermiaSerious;

    /// <summary>The headroom, in hypothermia growth intervals, kept under the cap for review and step granularity (the pawn is recalled at a review, not the instant the bound passes).</summary>
    public const int WarmthMarginIntervals = 3;

    /// <summary>The exposed time at a loot pickup (stop, lift, turn).</summary>
    public const int WarmthPickupTicks = 120;

    /// <summary>
    /// The exposed time a forage spends at its plant: the food plan's forage work estimate. The acquisition census
    /// reads no work ticks per plant, so every forage is priced at the estimate.
    /// </summary>
    public const double WarmthForageWorkTicks = FoodPlanRules.FoodForageWorkTicks;

    /// <summary>
    /// The exposed time a lone hunt spends engaging its prey, between the walk out and the haul back: an estimate,
    /// one game hour. The game exposes no hunt duration (shots, misses and the prey's flight are rolled as they
    /// happen), so the allowance is fixed and the trip's walk is what the prey's distance prices.
    /// </summary>
    public const double WarmthHuntEngagementTicks = GameTime.TicksPerHour;

    /// <summary>The window length when the cold does not grow hypothermia for this pawn (its clothing holds): one review horizon, renewed each review while the facts stand.</summary>
    public const long WarmthUnboundedTicks = GameTime.TicksPerHour;

    /// <summary>
    /// The Moving capacity multiplier of the hypothermia stage at severity: the vanilla stages offset Moving by
    /// -0.1 from the minor stage (0.20), -0.3 from the serious stage (0.35) and leave it at -0.3 through the
    /// extreme stage (0.62), where consciousness caps instead.
    /// </summary>
    public static double HypothermiaMovingFactor(double severity)
    {
        if (severity >= DashEstimateRules.HypothermiaSerious)
        {
            return 0.7;
        }
        if (severity >= DashEstimateRules.HypothermiaMinor)
        {
            return 0.9;
        }
        return 1;
    }

    /// <summary>
    /// Selects the trips for one pawn with observed body measurements: candidates in tier order, then value per
    /// exposed tick, then id; each is taken only if the set so far plus it keeps the pawn under the cap with
    /// WarmthMarginIntervals of headroom, and one that does not fit is skipped, not the ones after it.
    /// </summary>
    public static WarmthPlan PlanTrips(WarmthRequest r)
    {
        var temp = r.Temperature;
        var comfort = r.ComfortMin;
        var sev = r.Severity;
        var speed = r.Speed;
        if (double.IsNaN(speed) || speed <= 0)
        {
            return new WarmthPlan();
        }
        if (r.Candidates.Count == 0)
        {
            return new WarmthPlan();
        }
        if (sev >= WarmthSeverityCap)
        {
            return new WarmthPlan();
        }
        var growth = r.Tuning.Growth(temp, comfort);
        var baseFactor = HypothermiaMovingFactor(sev);
        double Exposure(WarmthCandidate c) => (2 * c.PathCells / speed * 60) + c.WorkTicks;
        var order = r.Candidates
            .OrderBy(c => c.Tier)
            .ThenByDescending(c => c.Value / Exposure(c))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        var trips = new List<WarmthTrip>();
        var elapsed = 0.0;
        // Walk advances the clock over a leg of cells and returns the severity at its end; the speed is that
        // of the severity the leg is at, step by step.
        (double Severity, double Spent) Walk(double severity, double cells)
        {
            var spent = 0.0;
            while (cells > 0)
            {
                var v = speed * HypothermiaMovingFactor(severity) / baseFactor;
                var dt = 60.0;
                if (v * dt / 60 > cells)
                {
                    dt = cells / v * 60;
                }
                cells -= v * dt / 60;
                spent += dt;
                severity += growth * dt / DashEstimateRules.HypothermiaInterval;
            }
            return (severity, spent);
        }
        foreach (var c in order)
        {
            if (c.PathCells < 0 || double.IsNaN(c.PathCells) || c.Value <= 0 || c.WorkTicks < 0 || double.IsNaN(c.WorkTicks))
            {
                continue;
            }
            var (s, outbound) = Walk(sev, c.PathCells);
            s += growth * c.WorkTicks / DashEstimateRules.HypothermiaInterval;
            var (s2, back) = Walk(s, c.PathCells);
            s = s2;
            if (growth > 0 && s + (WarmthMarginIntervals * growth) >= WarmthSeverityCap)
            {
                continue;
            }
            sev = s;
            var spent = outbound + c.WorkTicks + back;
            elapsed += spent;
            trips.Add(new WarmthTrip { Id = c.Id, Kind = c.Kind, Value = c.Value, Exposure = (int)Math.Ceiling(spent) });
        }
        var plan = new WarmthPlan { Trips = EquatableArray.From(trips) };
        if (trips.Count == 0)
        {
            return plan;
        }
        if (growth <= 0)
        {
            return plan with { Until = new Tick(r.Now.Value + WarmthUnboundedTicks) };
        }
        return plan with { Until = new Tick(r.Now.Value + (long)Math.Ceiling(elapsed)) };
    }

    /// <summary>
    /// The severity a pawn's hypothermia must be back at before a new window opens for it: the shivering floor,
    /// so each cycle starts near clean and the budget is the full span from there to the cap less the margin.
    /// </summary>
    public const double WarmthRefillSeverity = DashEstimateRules.HypothermiaShivering;

    /// <summary>Whether a pawn at <paramref name="severity"/> may be given a new window.</summary>
    public static bool WarmthRefilled(double severity) => severity <= WarmthRefillSeverity;

    /// <summary>
    /// The ticks until a pawn rewarming from <paramref name="severity"/> is refilled, by the published recovery
    /// per step; zero when it already is. A prediction for the hold reason only: the window itself reads the
    /// observed severity.
    /// </summary>
    public static long WarmthRefillTicks(double severity)
    {
        var steps = 0L;
        var s = severity;
        while (!WarmthRefilled(s))
        {
            s -= DashEstimateRules.HypothermiaRecovery(s);
            steps++;
        }
        return steps * DashEstimateRules.HypothermiaInterval;
    }

    /// <summary>The window held for <paramref name="pawn"/>, or an empty one.</summary>
    public static WarmthWindow WarmthWindowOf(this ReviewFacts f, PawnId pawn)
    {
        foreach (var w in f.Warmth)
        {
            if (w.Pawn == pawn)
            {
                return w;
            }
        }
        return new WarmthWindow();
    }

    /// <summary>Reports whether <paramref name="pawn"/> may be outdoors at the facts' tick.</summary>
    public static bool WarmthOpenFor(this ReviewFacts f, PawnId pawn) => f.WarmthWindowOf(pawn).OpenAt(f.Tick);

    /// <summary>Reports whether any warmth window is open at the facts' tick.</summary>
    public static bool WarmthOpen(this ReviewFacts f)
    {
        foreach (var w in f.Warmth)
        {
            if (w.OpenAt(f.Tick))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The needs the tiers rank by. Shelter materials are the resources the planned construction
    /// (PlannedConstruction, every planned room) still owes beyond the observed stock; fuels are the fuel runway's
    /// needs; food is short while the read food runway reaches neither the foothold nor the season's target days.
    /// </summary>
    public static WarmthNeeds WarmthNeedsOf(ReviewFacts f, PlannerSettings p)
    {
        var shelter = new HashSet<Resource>();
        if (f.PlannedConstruction is { } rooms)
        {
            var stock = ResourceStockRules.Count(f.Census.Resources);
            foreach (var (resource, owed) in PlannedRules.PlannedTotal(rooms.Values))
            {
                if (owed > stock.GetValueOrDefault(resource))
                {
                    shelter.Add(resource);
                }
            }
        }
        var fuel = new HashSet<Resource>();
        foreach (var (resource, n) in f.FuelRunway().Needs)
        {
            if (n > 0)
            {
                fuel.Add(resource);
            }
        }
        // The food runway is read later in a pass than the windows on some paths; unread, food ranks as not short.
        var season = p.Seasonal(f.Calendar, f.DisasterConditions);
        var food = f.FoodDays is { } runway && (!runway.Reaches(season.FootholdFoodDays) || !runway.Reaches(season.FoodTargetDays));
        return new WarmthNeeds { ShelterMaterials = shelter, Fuel = fuel, Food = food };
    }

    /// <summary>The tier a trip yielding <paramref name="resource"/> ranks in; <paramref name="food"/> says the yield is edible.</summary>
    public static WarmthTier WarmthTierOf(Resource resource, bool food, WarmthNeeds needs)
    {
        if (needs.ShelterMaterials.Contains(resource))
        {
            return WarmthTier.ShelterMaterials;
        }
        if (needs.Fuel.Contains(resource))
        {
            return WarmthTier.Fuel;
        }
        return food && needs.Food ? WarmthTier.Food : WarmthTier.Other;
    }

    /// <summary>
    /// The trips the censuses offer. Loot: safe stacks with a known path and a market value, a pickup's work. Salvage:
    /// non-player clearance rows with safe salvage evidence, their native path and labor, valued at their yields.
    /// Lone hunt: food prey that does not retaliate (no predator, revenge under the squad bound), untaken, valued at
    /// its meat; its work is WarmthHuntEngagementTicks. Forage: untaken plants that are not trees, valued at their
    /// yield, worked for WarmthForageWorkTicks. The acquisition census carries no path, so hunts and forages are
    /// priced by the straight line from the colony's reference cell (the layout plan's center, else the home
    /// area's middle), as the acquisition catalog prices them; without one, none is offered.
    /// </summary>
    public static IReadOnlyList<WarmthCandidate> WarmthCandidates(ReviewFacts f, WarmthNeeds needs)
    {
        var result = new List<WarmthCandidate>();
        var market = f.Items.Market;
        var nutrition = f.Items.Nutrition;
        foreach (var row in f.EventLoot)
        {
            if (!row.SafePath(out var path) || row.SpawnForbidden)
            {
                continue;
            }
            var resource = new Resource(row.Supply.Definition);
            var mk = market.TryGetValue(resource, out var unit);
            if (!mk || row.Count <= 0 || path < 0)
            {
                continue;
            }
            var edible = nutrition.TryGetValue(resource, out var n) && n > 0;
            result.Add(new WarmthCandidate
            {
                Id = row.Supply.Thing,
                Kind = WarmthTripKind.Loot,
                Tier = WarmthTierOf(resource, edible, needs),
                Value = unit * row.Count,
                PathCells = path,
                WorkTicks = WarmthPickupTicks,
            });
        }
        if (f.Upkeep.Clearance is { Targets: var targets })
        {
            foreach (var row in targets)
            {
                if (row.Player || row.Salvage is not { Safe: true } salvage || salvage.Candidate.Path is not CandidatePath.Walk { Distance: var path } || path < 0)
                {
                    continue;
                }
                var value = 0.0;
                var tier = WarmthTier.Other;
                foreach (var y in salvage.Candidate.Yields)
                {
                    if (!y.StockOf(out var count) || count <= 0)
                    {
                        continue;
                    }
                    value += y.UnitValue * count;
                    var edible = nutrition.TryGetValue(y.Good.Def, out var n) && n > 0;
                    var t = WarmthTierOf(y.Good.Def, edible, needs);
                    tier = t < tier ? t : tier;
                }
                result.Add(new WarmthCandidate
                {
                    Id = row.EntityId,
                    Kind = WarmthTripKind.Salvage,
                    Tier = tier,
                    Value = value,
                    PathCells = path,
                    WorkTicks = salvage.Candidate.UpfrontCost.LaborTicks,
                });
            }
        }
        if (WarmthReferenceCell(f) is not { } home)
        {
            return result;
        }
        foreach (var s in f.Acquisition)
        {
            if (s.Taken || s.Entity.Place is not RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap at)
            {
                continue;
            }
            var resource = new Resource(s.Resource);
            if (!market.TryGetValue(resource, out var unit))
            {
                continue;
            }
            var path = Math.Sqrt(Math.Pow(at.Cell.X - home.X, 2) + Math.Pow(at.Cell.Z - home.Z, 2));
            if (s.Hunt)
            {
                if (!s.Food || s.Retaliates() || s.Observed.Kind is not RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt { MeatAmount: var meat } || meat <= 0)
                {
                    continue;
                }
                result.Add(new WarmthCandidate
                {
                    Id = s.Id,
                    Kind = WarmthTripKind.LoneHunt,
                    Tier = WarmthTierOf(resource, true, needs),
                    Value = unit * meat,
                    PathCells = path,
                    WorkTicks = WarmthHuntEngagementTicks,
                });
                continue;
            }
            if (s.Tree || s.Yield <= 0)
            {
                continue;
            }
            result.Add(new WarmthCandidate
            {
                Id = s.Id,
                Kind = WarmthTripKind.Forage,
                Tier = WarmthTierOf(resource, s.Food, needs),
                Value = unit * s.Yield,
                PathCells = path,
                WorkTicks = WarmthForageWorkTicks,
            });
        }
        return result;
    }

    /// <summary>The colony's reference cell for a straight-line distance: the layout plan's center, else the middle of the home area; null with neither.</summary>
    public static Cell? WarmthReferenceCell(ReviewFacts f)
    {
        if (f.LayoutPlan?.Center() is { } center)
        {
            return center;
        }
        if (f.HomeCoverage is { HomeCells.Count: > 0 } home)
        {
            long sx = 0, sz = 0;
            foreach (var c in home.HomeCells)
            {
                sx += c.X;
                sz += c.Z;
            }
            return new Cell((int)(sx / home.HomeCells.Count), (int)(sz / home.HomeCells.Count));
        }
        return null;
    }

    /// <summary>The windows a review starting with none would leave.</summary>
    public static EquatableArray<WarmthWindow> WarmthWindowsOf(ReviewFacts f, PlannerSettings p, WorkPawnsRead pawns, Tick now, HypothermiaTuning tuning) =>
        ReviewWarmth([], f, p, pawns, now, tuning);

    /// <summary>
    /// The windows the review leaves. Windows exist only while extreme cold is the sole outdoor hazard and no
    /// hostile is on the map (heat and fallout stay held); otherwise none. Candidates are WarmthCandidates's over
    /// WarmthNeedsOf; PlanWarmth does the rest.
    /// </summary>
    public static EquatableArray<WarmthWindow> ReviewWarmth(EquatableArray<WarmthWindow> held, ReviewFacts f, PlannerSettings p, WorkPawnsRead pawns, Tick now, HypothermiaTuning tuning)
    {
        var hazards = OutdoorHazardRules.OutdoorHazardsOf(f);
        var hostiles = f.Hostiles.ThreatCount;
        if (hostiles > 0 || hazards.Count != 1 || hazards[0].Kind != HazardKind.ExtremeCold)
        {
            return [];
        }
        return PlanWarmth(held, WarmthCandidates(f, WarmthNeedsOf(f, p)), pawns, f.Census.OutdoorTemperatureC, now, tuning);
    }

    /// <summary>
    /// One window per available colonist with a readable body, in pawn id order. A held window still before its
    /// bound is kept as planned: its bound was set from the severity the pawn started at, and re-planning from the
    /// severity it has since built up would close it under a pawn that is partway out. One at or past its bound
    /// closes and plans nothing for that pawn in this review, so the pawn is recalled before another window can
    /// open under it. A pawn with no open window is planned only once its observed severity is refilled
    /// (WarmthRefilled). A trip already in an earlier pawn's window (held or planned this review) is not offered
    /// again. A window whose pawn is no longer available is dropped.
    /// </summary>
    public static EquatableArray<WarmthWindow> PlanWarmth(EquatableArray<WarmthWindow> held, IReadOnlyList<WarmthCandidate> candidates,
        WorkPawnsRead pawns, double temperature, Tick now, HypothermiaTuning tuning)
    {
        var rows = pawns.Pawns.Where(w => w.Available).OrderBy(w => w.Id).ToList();
        var taken = new HashSet<(WarmthTripKind, string)>();
        var closing = new HashSet<PawnId>();
        var kept = new Dictionary<PawnId, WarmthWindow>();
        foreach (var w in held)
        {
            if (!rows.Any(r => r.Id == w.Pawn))
            {
                continue;
            }
            if (!w.OpenAt(now))
            {
                closing.Add(w.Pawn);
                continue;
            }
            kept[w.Pawn] = w;
            foreach (var t in w.Trips)
            {
                taken.Add((t.Kind, t.Id));
            }
        }
        var result = new List<WarmthWindow>();
        foreach (var w in rows)
        {
            if (kept.TryGetValue(w.Id, out var open))
            {
                result.Add(open);
                continue;
            }
            var body = w.Body;
            if (closing.Contains(w.Id) || !WarmthRefilled(body.Hypothermia))
            {
                continue;
            }
            var plan = PlanTrips(new WarmthRequest
            {
                Now = now,
                Temperature = temperature,
                ComfortMin = body.ComfortableMinC,
                Severity = body.Hypothermia,
                Speed = body.MoveSpeed,
                Candidates = EquatableArray.From(candidates.Where(c => !taken.Contains((c.Kind, c.Id)))),
                Tuning = tuning,
            });
            if (plan.Trips.Count == 0)
            {
                continue;
            }
            foreach (var t in plan.Trips)
            {
                taken.Add((t.Kind, t.Id));
            }
            result.Add(new WarmthWindow { Pawn = w.Id, Until = plan.Until, Trips = plan.Trips });
        }
        return EquatableArray.From(result);
    }
}
