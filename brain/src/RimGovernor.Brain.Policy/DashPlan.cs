namespace RimGovernor.Brain.Policy;

// Planned dashes (#2765). While extreme cold holds outdoor work, one colonist may make bounded trips to fetch
// scattered materials. PlanDash picks the trip set from the hypothermia estimator (OutdoorBudgetFor's formula):
// trips are ordered by value per exposed tick, each is an out-and-back leg plus a pickup dwell, and the set is
// cut where the pawn's hypothermia would reach DashSeverityCap by the end of the last return. Hypothermia slows
// the pawn (the Moving capacity offsets of the vanilla stages), so a leg is walked at the speed of the severity
// it starts at, not the speed the pawn left at.
//
// Every trip is counted from home with no rest between trips. A pawn's window is planned once, from the severity
// the pawn starts at, and held in Latches.Warmth until its bound (ReviewWarmth); WarmthWindow.Until is the one
// bound every consumer reads (OpenAt). A pawn still outside at the bound is sheltered again, which recalls it
// through the Safe area.
//
// The warmth budget (#3578, #4049): every available colonist gets its own window, and a pawn whose window has
// closed gets a new one only once its observed hypothermia has fallen back to WarmthRefillSeverity. Nothing
// models the refill; the body read carries the severity each review, and HypothermiaRecovery only predicts the
// tick the refill is reached (WarmthRefillTicks) for the hold reason.

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

/// <summary>One pawn's warmth window: the pawn allowed outdoors through extreme cold, the trips planned, and the tick it closes.</summary>
public sealed record WarmthWindow
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

    /// <summary>The pickups the loot census offers: safe stacks with a known path and a known market value.</summary>
    public static IReadOnlyList<DashCandidate> DashCandidates(ReviewFacts f)
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

    /// <summary>Plans the windows afresh: ReviewWarmth with none held.</summary>
    public static EquatableArray<WarmthWindow> WarmthWindowsOf(ReviewFacts f, WorkPawnsRead pawns, Tick now, HypothermiaTuning tuning) =>
        ReviewWarmth([], f, pawns, now, tuning);

    /// <summary>
    /// The windows the review leaves. Windows exist only while extreme cold is the sole outdoor hazard and no
    /// hostile is on the map (heat and fallout stay held); otherwise none. Candidates are the loot census's;
    /// PlanWarmth does the rest.
    /// </summary>
    public static EquatableArray<WarmthWindow> ReviewWarmth(EquatableArray<WarmthWindow> held, ReviewFacts f, WorkPawnsRead pawns, Tick now, HypothermiaTuning tuning)
    {
        var hazards = OutdoorHazardRules.OutdoorHazardsOf(f);
        var hostiles = f.Hostiles.ThreatCount;
        if (hostiles > 0 || hazards.Count != 1 || hazards[0].Kind != HazardKind.ExtremeCold)
        {
            return [];
        }
        return PlanWarmth(held, DashCandidates(f), pawns, f.Census.OutdoorTemperatureC, now, tuning);
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
    public static EquatableArray<WarmthWindow> PlanWarmth(EquatableArray<WarmthWindow> held, IReadOnlyList<DashCandidate> candidates,
        WorkPawnsRead pawns, double temperature, Tick now, HypothermiaTuning tuning)
    {
        var rows = pawns.Pawns.Where(w => w.Available).OrderBy(w => w.Id).ToList();
        var taken = new HashSet<string>(StringComparer.Ordinal);
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
                taken.Add(t.Id);
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
            var plan = PlanDash(new DashRequest
            {
                Now = now,
                Temperature = temperature,
                ComfortMin = body.ComfortableMinC,
                Severity = body.Hypothermia,
                Speed = body.MoveSpeed,
                Candidates = EquatableArray.From(candidates.Where(c => !taken.Contains(c.Id))),
                Tuning = tuning,
            });
            if (plan.Trips.Count == 0)
            {
                continue;
            }
            foreach (var t in plan.Trips)
            {
                taken.Add(t.Id);
            }
            result.Add(new WarmthWindow { Pawn = w.Id, Until = plan.Until, Trips = plan.Trips });
        }
        return EquatableArray.From(result);
    }
}
