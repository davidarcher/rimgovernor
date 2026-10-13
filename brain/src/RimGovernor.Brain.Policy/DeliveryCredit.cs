namespace RimGovernor.Brain.Policy;

/// <summary>One ledger counter group as the channels the census committed describe it: the rate they expect, risk-adjusted.</summary>
public sealed record CreditChannel
{
    public string Source { get; init; } = "";

    /// <summary>The summed risk-adjusted nutrition per day.</summary>
    public double Expected { get; init; }

    /// <summary>The shortest lead of the group's channels.</summary>
    public double LeadDays { get; init; }

    /// <summary>The days between a bursty channel's deliveries (a crop's grow days).</summary>
    public double CycleDays { get; init; }
}

/// <summary>
/// One Round's read of the delivery ledger. Delivered is the cumulative nutrition per counter group within the
/// load token LoadToken. An unavailable ledger is Known false and holds every factor.
/// </summary>
public sealed record CreditInput
{
    public Tick Tick { get; init; }

    public string LoadToken { get; init; } = "";

    public bool Known { get; init; }

    public IReadOnlyDictionary<string, double> Delivered { get; init; } = new Dictionary<string, double>();
}

/// <summary>A group's standing: Factor in [0,1] scales its expected rate and State is Delivering once counter differences have calibrated the flow.</summary>
public sealed record CreditResult(double Factor, CandidateState State);

/// <summary>A food_credit row: a factor that moved by CreditMoveThreshold or more, a state change, or a held factor explained.</summary>
public sealed record CreditChange
{
    public string Source { get; init; } = "";

    public string Reason { get; init; } = "";

    public string State { get; init; } = "";

    public double Expected { get; init; }

    public double Observed { get; init; }

    public double Factor { get; init; }

    public double WindowDays { get; init; }
}

/// <summary>
/// Credits each ledger counter group by what it delivers: the factor is observed over expected nutrition
/// across a trailing window, in [0,1], starting at 1. It lives in memory only; a restart starts every group
/// again at factor 1 and Designated. Historical counters establish a baseline; positive differences calibrate
/// delivering rates before a full window elapses.
/// </summary>
public sealed class DeliveryCredit
{
    private sealed class Sample(Tick tick, double delivered)
    {
        public Tick Tick { get; } = tick;

        public double Delivered { get; } = delivered;
    }

    private sealed class History
    {
        /// <summary>Set once the plan opened the group or it was seen delivering; Opened and Lead (days) are when, and the lead then.</summary>
        public bool Armed { get; set; }

        public Tick Opened { get; set; }

        public double Lead { get; set; }

        public double LeadNow { get; set; }

        public List<Sample> Samples { get; set; } = [];

        public double Factor { get; set; } = 1;

        public double Reported { get; set; } = 1;

        public bool Delivered { get; set; }

        public bool Evaluated { get; set; }

        public double Expected { get; set; }

        public double Observed { get; set; }

        public double Window { get; set; }
    }

    private string loadToken = "";
    private Tick last;
    private bool seen;
    private readonly Dictionary<string, History> groups = new(StringComparer.Ordinal);
    private Dictionary<string, CreditResult> results = new(StringComparer.Ordinal);
    private List<CreditChange> changes = [];

    /// <summary>The most rot ticks seen per unbutchered hunt corpse (HuntDelivered).</summary>
    internal Dictionary<string, long> RotPeak { get; set; } = new(StringComparer.Ordinal);

    private static double CreditWindowDays(CreditChannel c) => Math.Max(LedgerWindowDays, c.CycleDays + LedgerReplantDays);

    /// <summary>
    /// Folds one Round's ledger read into the factors and returns them by counter group. Groups absent from
    /// channels are forgotten. A tick already folded returns the standing results unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, CreditResult> Observe(CreditInput input, IReadOnlyList<CreditChannel> channels)
    {
        if (seen && input.Tick <= last)
        {
            return results;
        }
        seen = true;
        last = input.Tick;
        var rebaselined = false;
        if (input.Known)
        {
            rebaselined = loadToken != "" && input.LoadToken != loadToken;
            loadToken = input.LoadToken;
        }
        var live = new HashSet<string>(StringComparer.Ordinal);
        var previousResults = results;
        results = new Dictionary<string, CreditResult>(StringComparer.Ordinal);
        foreach (var c in channels.OrderBy(x => x.Source, StringComparer.Ordinal))
        {
            live.Add(c.Source);
            if (!groups.TryGetValue(c.Source, out var h))
            {
                h = new History();
                groups[c.Source] = h;
            }
            var window = CreditWindowDays(c);
            h.LeadNow = Math.Max(0, c.LeadDays);
            CandidateState? previous = previousResults.TryGetValue(c.Source, out var pr) ? pr.State : null;
            var reason = CreditWindow;
            var cum = input.Delivered.GetValueOrDefault(c.Source);
            if (!input.Known)
            {
                // The ledger is unavailable: every factor holds.
            }
            else if (rebaselined)
            {
                h.Samples = [];
                reason = CreditRebaseline;
            }
            else
            {
                if (cum > 0)
                {
                    h.Delivered = true;
                    Arm(h, input.Tick);
                }
                h.Samples.Add(new Sample(input.Tick, cum));
                Evaluate(h, c, input.Tick, window, cum);
            }
            var state = h.Delivered && h.Evaluated ? CandidateState.Delivering : CandidateState.Designated;
            results[c.Source] = new CreditResult(h.Factor, state);
            var held = reason != CreditWindow && h.Evaluated && rebaselined;
            var moved = Math.Abs(h.Factor - h.Reported) >= CreditMoveThreshold;
            if (held || moved || (previous is not null && state != previous))
            {
                h.Reported = h.Factor;
                changes.Add(new CreditChange
                {
                    Source = c.Source,
                    Reason = reason,
                    State = Wire.Name(state),
                    Expected = h.Expected,
                    Observed = h.Observed,
                    Factor = h.Factor,
                    WindowDays = window,
                });
            }
        }
        foreach (var source in groups.Keys.ToList())
        {
            if (!live.Contains(source))
            {
                groups.Remove(source);
            }
        }
        return results;
    }

    private static void Arm(History h, Tick now)
    {
        if (!h.Armed)
        {
            h.Armed = true;
            h.Opened = now;
            h.Lead = h.LeadNow;
        }
    }

    /// <summary>
    /// Tells the credit which counter groups a plan just opened: a group is judged only from its lead plus one
    /// window after the plan opened it, so a candidate nobody asked for is never penalised for not delivering.
    /// </summary>
    internal void Opened(FoodPlan plan, IEnumerable<SupplyCandidate> channels, Tick now)
    {
        var source = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in channels)
        {
            if (c.Source != "")
            {
                source[Wire.Name(c.Kind) + "/" + c.Id] = c.Source;
            }
        }
        foreach (var e in plan.Portfolio)
        {
            if (e.Decision != FoodPlanDecision.Open)
            {
                continue;
            }
            if (source.TryGetValue(Wire.Name(e.Channel.Kind) + "/" + e.Channel.Id, out var group) && groups.TryGetValue(group, out var h))
            {
                Arm(h, now);
            }
        }
    }

    /// <summary>Uses the trailing window, or the available history once deliveries exist. Undelivered proposals retain their lead-plus-window grace period.</summary>
    private static void Evaluate(History h, CreditChannel c, Tick now, double window, double cum)
    {
        var cutoff = now.Value - (long)(window * GameTime.TicksPerDay);
        // Keep the newest baseline before the window even for closed sources.
        while (h.Samples.Count > 1 && h.Samples[1].Tick.Value <= cutoff)
        {
            h.Samples.RemoveAt(0);
        }
        if (!h.Armed)
        {
            return;
        }
        var start = h.Opened.Value + (long)(h.Lead * GameTime.TicksPerDay);
        var baseIndex = -1;
        for (var i = 0; i < h.Samples.Count; i++)
        {
            var s = h.Samples[i];
            if (s.Tick.Value <= cutoff && s.Tick.Value >= start)
            {
                baseIndex = i;
            }
        }
        if (baseIndex < 0)
        {
            if (!h.Delivered || h.Samples.Count < 2)
            {
                return;
            }
            // A positive cumulative counter establishes a baseline, not a sustained flow. Once deliveries exist,
            // use the available history immediately rather than crediting the forecast for a whole window.
            baseIndex = 0;
        }
        h.Samples.RemoveRange(0, baseIndex);
        var span = (double)(now.Value - h.Samples[0].Tick.Value) / GameTime.TicksPerDay;
        if (h.Delivered)
        {
            // A single burst between nearby Rounds cannot promise many days of food; like hunt cadence, price at least one day's demand.
            span = Math.Max(1, span);
        }
        var expected = c.Expected * span;
        if (expected <= 0)
        {
            return;
        }
        var observed = Math.Max(0, cum - h.Samples[0].Delivered);
        h.Expected = expected;
        h.Observed = observed;
        h.Window = window;
        h.Factor = Math.Min(1, observed / expected);
        h.Evaluated = true;
    }

    /// <summary>Returns the changes since the last call, in source order.</summary>
    public IReadOnlyList<CreditChange> Drain()
    {
        var output = changes;
        changes = [];
        return output;
    }

    internal static bool Committed(SupplyCandidate c) =>
        c.State.TryGetValue(out var s) && (s == CandidateState.Designated || s == CandidateState.Delivering);

    /// <summary>The days between a crop channel's deliveries, from its Terms; hunting is judged over HuntCreditWindowDays.</summary>
    internal static double CycleDays(SupplyCandidate c)
    {
        if (c.Kind == CandidateKind.Hunt)
        {
            return PolicyFunctions.HuntCreditWindowDays - LedgerReplantDays;
        }
        foreach (var t in c.Terms)
        {
            if (t.Name == "grow_days")
            {
                return t.Value;
            }
        }
        return 0;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The trailing window a channel's observed deliveries are compared with its expected ones over. A channel that delivers in bursts is judged over at least one of its cycles plus LedgerReplantDays.</summary>
    public const double LedgerWindowDays = 3.0;

    public const double LedgerReplantDays = 1.0;

    /// <summary>The factor change that earns a food_credit row.</summary>
    public const double CreditMoveThreshold = 0.1;

    // Reasons of a CreditChange.

    /// <summary>The trailing window moved the factor or the state.</summary>
    public const string CreditWindow = "window";

    /// <summary>The ledger's load token changed; the factor is held.</summary>
    public const string CreditRebaseline = "rebaseline";

    /// <summary>
    /// Credits the channels: a committed channel with a counter group takes the group's state and its nutrition
    /// rate is scaled by the group's factor. A null DeliveryCredit applies nothing. Channels without a Source,
    /// and Closed ones, are returned as they are.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> Apply(this DeliveryCredit? d, IReadOnlyList<SupplyCandidate> channels, CreditInput input)
    {
        if (d is null)
        {
            return channels;
        }
        var byGroup = new Dictionary<string, CreditChannel>(StringComparer.Ordinal);
        foreach (var c in channels)
        {
            if (c.Source == "")
            {
                continue;
            }
            if (!c.Nutrition().PerDay.TryGetValue(out var rate) || double.IsNaN(rate) || double.IsInfinity(rate))
            {
                continue;
            }
            if (!byGroup.TryGetValue(c.Source, out var g))
            {
                g = new CreditChannel { Source = c.Source, LeadDays = double.PositiveInfinity };
            }
            // Keep the source's history while it is in the census, including gaps between designations; only committed channels expect delivery.
            if (!DeliveryCredit.Committed(c))
            {
                byGroup[c.Source] = g;
                continue;
            }
            var risk = 0.0;
            foreach (var r in c.Risk)
            {
                risk += r.Weight;
            }
            g = g with { Expected = g.Expected + (rate * Math.Max(0, 1 - risk)) };
            if (c.LeadDays.TryGetValue(out var lead) && lead < g.LeadDays)
            {
                g = g with { LeadDays = Math.Max(0, lead) };
            }
            var cycle = DeliveryCredit.CycleDays(c);
            if (cycle > g.CycleDays)
            {
                g = g with { CycleDays = cycle };
            }
            byGroup[c.Source] = g;
        }
        var groupList = byGroup.Values
            .Select(g => double.IsPositiveInfinity(g.LeadDays) ? g with { LeadDays = 0 } : g)
            .ToList();
        var results = d.Observe(input, groupList);
        var output = channels.ToList();
        for (var i = 0; i < output.Count; i++)
        {
            var c = output[i];
            if (!results.TryGetValue(c.Source, out var r) || c.Source == "" || !DeliveryCredit.Committed(c))
            {
                continue;
            }
            c = c with { State = Known(r.State) };
            if (c.Nutrition().PerDay.TryGetValue(out var rate) && !double.IsNaN(rate) && !double.IsInfinity(rate))
            {
                c = c.WithNutritionPerDay(Known(rate * r.Factor));
            }
            output[i] = c;
        }
        return output;
    }

    /// <summary>Tells the credit which counter groups a plan just opened. A null credit does nothing.</summary>
    public static void Opened(this DeliveryCredit? d, FoodPlan plan, IEnumerable<SupplyCandidate> channels, Tick now) => d?.Opened(plan, channels, now);
}
