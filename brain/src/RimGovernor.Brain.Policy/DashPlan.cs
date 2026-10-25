namespace RimGovernor.Brain.Policy;

// Planned dashes (#2765). While extreme cold holds outdoor work, one colonist may make bounded trips to fetch
// scattered materials. PlanDash picks the trip set from the hypothermia estimator (OutdoorBudgetFor's formula):
// trips are ordered by value per exposed tick, each is an out-and-back leg plus a pickup dwell, and the set is
// cut where the pawn's hypothermia would reach DashSeverityCap by the end of the last return. Hypothermia slows
// the pawn (the Moving capacity offsets of the vanilla stages), so a leg is walked at the speed of the severity
// it starts at, not the speed the pawn left at.
//
// Every trip is counted from home with no rest between trips. The window is planned once, from the severity the
// pawn starts at, and held in RoundsLatches.Dash until its bound (ReviewDash); DashWindow.Until is the one
// bound every consumer reads (OpenAt). A pawn still outside at the bound is sheltered again, which recalls it
// through the Safe area.

/// <summary>One pickup: the one-way path in cells from home and the value gained by fetching it.</summary>
public sealed record DashCandidate
{
    public string Id { get; init; } = "";

    public double Value { get; init; }

    public double PathCells { get; init; }
}

/// <summary>One planned trip. Exposure is its planned ticks outdoors.</summary>
public sealed record DashTrip
{
    public string Id { get; init; } = "";

    public double Value { get; init; }

    public int Exposure { get; init; }
}

/// <summary>
/// The pure input of PlanDash: the ambient temperature, the pawn's comfortable minimum, current hypothermia and
/// move speed (cells per second, at that severity), and the candidate pickups.
/// </summary>
public sealed record DashRequest
{
    public Tick Now { get; init; }

    public double Temperature { get; init; }

    public double ComfortMin { get; init; }

    public double Severity { get; init; }

    public double Speed { get; init; }

    public EquatableArray<DashCandidate> Candidates { get; init; }

    public HypothermiaTuning Tuning { get; init; } = new();
}

/// <summary>PlanDash's result. Until is the tick the last planned return completes; zero when no window opens.</summary>
public sealed record DashPlan
{
    public EquatableArray<DashTrip> Trips { get; init; }

    public Tick Until { get; init; }
}

/// <summary>The open window: the one pawn allowed outdoors through extreme cold, the trips planned, and the tick it closes.</summary>
public sealed record DashWindow
{
    public PawnId Pawn { get; init; }

    public Tick Until { get; init; }

    public EquatableArray<DashTrip> Trips { get; init; }

    /// <summary>The single close rule: the window is open before its bound and closed at it. Every consumer reads this; none keeps its own timer.</summary>
    public bool OpenAt(Tick now) => !Pawn.IsEmpty && now < Until;
}

public static class DashPlanRules
{
    /// <summary>
    /// The hypothermia severity a dash never reaches: the serious stage floor (Moving -0.3, Manipulation -0.5,
    /// pain). The estimator plans the whole trip set, return included, to stay below it.
    /// </summary>
    public const double DashSeverityCap = DashEstimateRules.HypothermiaSerious;

    /// <summary>The headroom, in hypothermia growth intervals, kept under the cap for review and step granularity (the pawn is recalled at a review, not the instant the bound passes).</summary>
    public const int DashMarginIntervals = 3;

    /// <summary>The exposed time at a pickup (stop, lift, turn).</summary>
    public const int DashPickupTicks = 120;

    /// <summary>The window length when the cold does not grow hypothermia for this pawn (its clothing holds): one review horizon, renewed each review while the facts stand.</summary>
    public const long DashUnboundedTicks = GameTime.TicksPerHour;


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

    /// <summary>Selects the trips for one pawn with observed body measurements.</summary>
    public static DashPlan PlanDash(DashRequest r)
    {
        var temp = r.Temperature;
        var comfort = r.ComfortMin;
        var sev = r.Severity;
        var speed = r.Speed;
        if (double.IsNaN(speed) || speed <= 0)
        {
            return new DashPlan();
        }
        if (r.Candidates.Count == 0)
        {
            return new DashPlan();
        }
        if (sev >= DashSeverityCap)
        {
            return new DashPlan();
        }
        var growth = r.Tuning.Growth(temp, comfort);
        var baseFactor = HypothermiaMovingFactor(sev);
        double Exposure(DashCandidate c) => (2 * c.PathCells / speed * 60) + DashPickupTicks;
        var order = r.Candidates
            .OrderByDescending(c => c.Value / Exposure(c))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        var trips = new List<DashTrip>();
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
            if (c.PathCells < 0 || double.IsNaN(c.PathCells) || c.Value <= 0)
            {
                continue;
            }
            var (s, outbound) = Walk(sev, c.PathCells);
            s += growth * DashPickupTicks / DashEstimateRules.HypothermiaInterval;
            var (s2, back) = Walk(s, c.PathCells);
            s = s2;
            if (growth > 0 && s + (DashMarginIntervals * growth) >= DashSeverityCap)
            {
                continue;
            }
            sev = s;
            var spent = outbound + DashPickupTicks + back;
            elapsed += spent;
            trips.Add(new DashTrip { Id = c.Id, Value = c.Value, Exposure = (int)Math.Ceiling(spent) });
        }
        var plan = new DashPlan { Trips = EquatableArray.From(trips) };
        if (trips.Count == 0)
        {
            return plan;
        }
        if (growth <= 0)
        {
            return plan with { Until = new Tick(r.Now.Value + DashUnboundedTicks) };
        }
        return plan with { Until = new Tick(r.Now.Value + (long)Math.Ceiling(elapsed)) };
    }

    /// <summary>Reports whether <paramref name="pawn"/> may be outdoors at the facts' tick.</summary>
    public static bool DashOpenFor(this RoundsFacts f, PawnId pawn) =>
        f.Dash.Pawn == pawn && f.Dash.OpenAt(f.Tick);

    /// <summary>Reports whether any dash window is open at the facts' tick.</summary>
    public static bool DashOpen(this RoundsFacts f) => f.Dash.OpenAt(f.Tick);

    /// <summary>The pickups the loot census offers: safe stacks with a known path and a known market value.</summary>
    public static IReadOnlyList<DashCandidate> DashCandidates(RoundsFacts f)
    {
        var result = new List<DashCandidate>();
        foreach (var row in f.EventLoot)
        {
            if (!row.SafePath(out var path) || row.SpawnForbidden)
            {
                continue;
            }
            var mk = f.Items.Market.TryGetValue(new Resource(row.Supply.Definition), out var unit);
            if (!mk || row.Count <= 0 || path < 0)
            {
                continue;
            }
            result.Add(new DashCandidate { Id = row.Supply.Thing, Value = unit * row.Count, PathCells = path });
        }
        return result;
    }

    /// <summary>Plans a window afresh: ReviewDash with none held.</summary>
    public static DashWindow DashWindowOf(RoundsFacts f, WorkPawnsRead pawns, Tick now, HypothermiaTuning tuning) =>
        ReviewDash(new DashWindow(), f, pawns, now, tuning);

    /// <summary>
    /// The window the review leaves. A window exists only while extreme cold is the sole outdoor hazard and no
    /// hostile is on the map (heat and fallout stay held). One planned and still before its bound is kept as
    /// planned: its bound was set from the severity the pawn started at, and re-planning from the severity it
    /// has since built up would close it under a pawn that is partway out. One at or past its bound closes and
    /// plans nothing in this review, so the pawn is recalled before another window can open under it.
    /// Otherwise each available colonist with a readable body is planned and the plan with the most value
    /// wins, ties to the lowest pawn id. Unknown while the hazards, hostiles or the roster are unknown.
    /// </summary>
    public static DashWindow ReviewDash(DashWindow held, RoundsFacts f, WorkPawnsRead pawns, Tick now, HypothermiaTuning tuning)
    {
        var hazards = OutdoorHazardRules.OutdoorHazardsOf(f);
        var hostiles = f.Hostiles.ThreatCount;
        var rows = pawns.Pawns;
        if (hostiles > 0 || hazards.Count != 1 || hazards[0].Kind != HazardKind.ExtremeCold)
        {
            return new DashWindow();
        }
        if (!held.Pawn.IsEmpty)
        {
            if (held.OpenAt(now) && rows.Any(w => w.Id == held.Pawn && w.Available))
            {
                return held;
            }
            return new DashWindow();
        }
        var candidates = DashCandidates(f);
        var best = new DashWindow();
        var bestValue = 0.0;
        foreach (var w in rows)
        {
            if (!w.Available)
            {
                continue;
            }
            var body = w.Body;
            var plan = PlanDash(new DashRequest
            {
                Now = now,
                Temperature = f.Census.OutdoorTemperatureC,
                ComfortMin = body.ComfortableMinC,
                Severity = body.Hypothermia,
                Speed = body.MoveSpeed,
                Candidates = EquatableArray.From(candidates),
                Tuning = tuning,
            });
            if (plan.Trips.Count == 0)
            {
                continue;
            }
            var value = 0.0;
            foreach (var t in plan.Trips)
            {
                value += t.Value;
            }
            if (value > bestValue || (value == bestValue && w.Id < best.Pawn))
            {
                best = new DashWindow { Pawn = w.Id, Until = plan.Until, Trips = plan.Trips };
                bestValue = value;
            }
        }
        return best;
    }
}
