using System.Globalization;

namespace RimGovernor.Brain.Policy;

// Go: work_ledger_view.go and work_ledger_rows.go. The ledger view is the read-only picture of one Round's work
// ledger for the Grafana ledger dashboard: what each planner declared, where every wanted order stands against the
// benches' bills, the orphans on their way out, the dispatcher's unmet throughput and calibration windows, and
// MaintainTrade's silver gap. It is a projection of memory the Round already holds; Rows is its derived copy in the
// session store, and nothing is fed back to a planner.

/// <summary>One planner's declaration with the planner's name.</summary>
public sealed record NamedDeclared(string Name, Declared Declared);

/// <summary>The latest outcome of a ledger plan's production_bill for one order, read from the Rounder's placement index.</summary>
public sealed record PlacementAttempt
{
    public string Plan { get; init; } = "";

    public long Tick { get; init; }

    /// <summary>One of the Attempt* outcomes.</summary>
    public string Outcome { get; init; } = "";

    public string Code { get; init; } = "";

    public string Reason { get; init; } = "";
}

/// <summary>The latest attempt per order Key.</summary>
public sealed class PlacementAttempts
{
    private readonly Dictionary<string, PlacementAttempt> _attempts = [];

    public bool TryGetValue(string key, out PlacementAttempt attempt) => _attempts.TryGetValue(key, out attempt!);

    /// <summary>Keeps a as the key's attempt unless a later one is already held.</summary>
    public void Note(string key, PlacementAttempt a)
    {
        if (!_attempts.TryGetValue(key, out var held) || a.Tick >= held.Tick)
        {
            _attempts[key] = a;
        }
    }
}

/// <summary>What the Round holds when it builds the view.</summary>
public sealed record LedgerViewInput
{
    public long Tick { get; init; }

    public IReadOnlyList<NamedDeclared> Declarers { get; init; } = [];

    public LedgerPlan Plan { get; init; } = new();

    public IReadOnlyList<LedgerPlacement> Placed { get; init; } = [];

    public IReadOnlyList<OrderSpec> Unplaced { get; init; } = [];

    public IReadOnlyDictionary<string, OrderDispatch> Dispatch { get; init; } = new Dictionary<string, OrderDispatch>();

    public DispatchMemory Memory { get; init; } = new();

    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    public IReadOnlyList<UnmetThroughput> Unmet { get; init; } = [];

    public IReadOnlyList<string> Further { get; init; } = [];
}

/// <summary>The ledger as the launcher reads it.</summary>
public sealed record LedgerView
{
    public string Status { get; init; } = "";

    public long Tick { get; init; }

    /// <summary>OrphanGraceRounds: the Rounds an undeclared bill waits.</summary>
    public int GraceRounds { get; init; }

    /// <summary>Whether any planner abstained, which stops all removal.</summary>
    public bool Abstained { get; init; }

    public IReadOnlyList<LedgerDeclarerView> Declarers { get; init; } = [];

    public IReadOnlyList<LedgerOrderView> Orders { get; init; } = [];

    public IReadOnlyList<LedgerBillView> Orphans { get; init; } = [];

    public IReadOnlyList<LedgerBillView> Excluded { get; init; } = [];

    public IReadOnlyList<UnmetView> Unmet { get; init; } = [];

    public IReadOnlyList<string> FurtherBenches { get; init; } = [];

    /// <summary>Whether the placement index was seeded for the orders' Attempt.</summary>
    public bool AttemptsKnown { get; init; }

    public ExportView? Export { get; init; }

    /// <summary>The view with each order's latest placement outcome from the placement index. The receiver is not changed.</summary>
    public LedgerView WithAttempts(PlacementAttempts attempts) =>
        this with
        {
            AttemptsKnown = true,
            Orders = [.. Orders.Select(o => attempts.TryGetValue(o.Key, out var a) ? o with { Attempt = a } : o)],
        };

    /// <summary>
    /// Flattens the view into the rows the session store keeps for Grafana, in the order the former Ledger tab
    /// listed them. A "round" row always leads.
    /// </summary>
    public IReadOnlyList<LedgerRow> Rows()
    {
        var rows = new List<LedgerRow>();
        var next = new Dictionary<string, int>();
        void Add(string section, Dictionary<string, object> cells)
        {
            rows.Add(new LedgerRow(section, next.GetValueOrDefault(section), cells));
            next[section] = next.GetValueOrDefault(section) + 1;
        }
        static string Join(IEnumerable<string> s) => string.Join(", ", s);
        Add(
            PolicyFunctions.LedgerSectionRound,
            new()
            {
                ["status"] = Status,
                ["grace_rounds"] = GraceRounds,
                ["abstained"] = Abstained,
                ["attempts_known"] = AttemptsKnown,
                ["orders"] = Orders.Count,
                ["orphans"] = Orphans.Count,
                ["declarers"] = Declarers.Count,
            });
        foreach (var o in Orders)
        {
            var c = new Dictionary<string, object>
            {
                ["owners"] = Join(o.Owners),
                ["recipe"] = o.Recipe,
                ["product"] = o.Product,
                ["mode"] = o.Mode,
                ["target"] = o.Target,
                ["bench_kind"] = o.BenchKind,
                ["worker"] = o.Worker,
                ["ingredients"] = Join(o.Ingredients),
                ["copies"] = o.Copies,
                ["state"] = o.State,
                ["benches"] = Join(o.Benches),
                ["placing"] = Join(o.Placing),
                ["unplaced"] = o.Unplaced,
                ["reason"] = Wire.Name(o.Reason),
            };
            if (o.Shortfall is not null)
            {
                c["short_per_day"] = o.Shortfall.ShortPerDay;
                c["short_reason"] = Wire.Name(o.Shortfall.Reason);
            }
            if (o.Attempt is { } a)
            {
                c["attempt_outcome"] = a.Outcome;
                c["attempt_tick"] = a.Tick;
                c["attempt_code"] = a.Code;
                c["attempt_reason"] = a.Reason;
            }
            Add(PolicyFunctions.LedgerSectionOrder, c);
        }
        foreach (var b in Orphans)
        {
            Add(
                PolicyFunctions.LedgerSectionOrphan,
                new()
                {
                    ["bench"] = b.Bench,
                    ["bill"] = b.ID,
                    ["recipe"] = b.Recipe,
                    ["target"] = b.Target,
                    ["worker"] = b.Worker,
                    ["state"] = b.State,
                    ["rounds"] = b.Rounds,
                    ["grace_left"] = b.GraceLeft,
                    ["spent"] = b.Spent,
                });
        }
        var further = FurtherBenches.ToHashSet();
        foreach (var u in Unmet)
        {
            Add(
                PolicyFunctions.LedgerSectionUnmet,
                new()
                {
                    ["bench_kind"] = u.BenchKind,
                    ["short_per_day"] = u.ShortPerDay,
                    ["reason"] = Wire.Name(u.Reason),
                    ["further_bench"] = further.Contains(u.BenchKind),
                });
        }
        foreach (var o in Orders)
        {
            if (o.Window is { } w)
            {
                var c = new Dictionary<string, object>
                {
                    ["recipe"] = o.Recipe,
                    ["predicted_per_day"] = w.PredictedPerDay,
                    ["start_stock"] = w.StartStock,
                    ["start"] = w.Start,
                    ["ends"] = w.Ends,
                    ["samples"] = w.Samples,
                    ["carrying"] = w.Carrying,
                    ["met"] = w.Met,
                };
                if (w.ObservedGain is { } gain)
                {
                    c["observed_gain"] = gain;
                }
                Add(PolicyFunctions.LedgerSectionWindow, c);
            }
        }
        foreach (var b in Excluded)
        {
            Add(PolicyFunctions.LedgerSectionExcluded, new() { ["bench"] = b.Bench, ["recipe"] = b.Recipe, ["kind"] = b.Kind, ["target"] = b.Target });
        }
        foreach (var d in Declarers)
        {
            var why = d.Abstains.Select(a => a.Concern + ": " + PolicyFunctions.Wording(a.Fact, ""));
            Add(
                PolicyFunctions.LedgerSectionDeclarer,
                new()
                {
                    ["planner"] = d.Name,
                    ["orders"] = d.Orders,
                    ["abstained"] = d.Abstains.Count > 0,
                    ["abstains"] = string.Join("; ", why),
                });
        }
        if (Export is { } e)
        {
            Add(PolicyFunctions.LedgerSectionExportGap, new() { ["gap_known"] = e.GapKnown, ["gap"] = e.Gap, ["in_flight"] = e.InFlight, ["remaining"] = e.Remaining });
            foreach (var c in e.Candidates)
            {
                Add(
                    PolicyFunctions.LedgerSectionExportCandidate,
                    new()
                    {
                        ["recipe"] = c.Recipe,
                        ["product"] = c.Product,
                        ["stuff"] = c.Stuff,
                        ["worker"] = c.Worker,
                        ["score"] = c.Score,
                        ["net"] = c.Net,
                        ["ticks"] = c.Ticks,
                        ["ordered"] = c.Ordered,
                    });
            }
            foreach (var d in e.Dropped)
            {
                Add(PolicyFunctions.LedgerSectionExportDrop, new() { ["reason"] = Wire.Name(d.Reason), ["count"] = d.Count });
            }
        }
        return rows;
    }
}

/// <summary>One row of a ledger table: its section, its place in the section and its fields by name. Fields are bounded vocabulary, numbers and booleans; a list is one comma-joined string.</summary>
public sealed record LedgerRow(string Section, int Position, IReadOnlyDictionary<string, object> Cells);

/// <summary>One planner's declaration summary.</summary>
public sealed record LedgerDeclarerView(string Name, int Orders, IReadOnlyList<AbstainView> Abstains);

/// <summary>One concern's abstain and the input it lacked.</summary>
public sealed record AbstainView(string Concern, Cause Fact);

/// <summary>One wanted order and where it stands.</summary>
public sealed record LedgerOrderView
{
    public string Key { get; init; } = "";

    /// <summary>The concerns that declared it; a spec two concerns declare is one order that lists both.</summary>
    public IReadOnlyList<string> Owners { get; init; } = [];

    public string Recipe { get; init; } = "";

    public string Product { get; init; } = "";

    public string Mode { get; init; } = "";

    public int Target { get; init; }

    public string BenchKind { get; init; } = "";

    public string Worker { get; init; } = "";

    public IReadOnlyList<string> Ingredients { get; init; } = [];

    /// <summary>How many benches should carry it (the rate model).</summary>
    public int Copies { get; init; }

    public int Eligible { get; init; }

    public string State { get; init; } = "";

    public IReadOnlyList<string> Benches { get; init; } = [];

    public IReadOnlyList<string> Placing { get; init; } = [];

    /// <summary>Counts the copies no bench could take, Reason why.</summary>
    public int Unplaced { get; init; }

    public UnmetReason Reason { get; init; }

    public double? Demand { get; init; }

    public double? Capacity { get; init; }

    public int? Wanted { get; init; }

    /// <summary>The calibration's extra benches.</summary>
    public int Widen { get; init; }

    /// <summary>The closed window's verdict while it holds.</summary>
    public UnmetView? Shortfall { get; init; }

    public WindowView? Window { get; init; }

    /// <summary>The journal's latest placement outcome, null when none was tried.</summary>
    public PlacementAttempt? Attempt { get; init; }
}

/// <summary>An order's calibration window: predicted against observed.</summary>
public sealed record WindowView
{
    public bool Open { get; init; }

    public long Start { get; init; }

    public long Ends { get; init; }

    public long StartStock { get; init; }

    public long Deficit { get; init; }

    /// <summary>The carrying benches' units per day.</summary>
    public double PredictedPerDay { get; init; }

    /// <summary>The stock gained since Start, null while the stock is unread.</summary>
    public long? ObservedGain { get; init; }

    public int Carrying { get; init; }

    public int Samples { get; init; }

    public int Busy { get; init; }

    public bool Inactive { get; init; }

    public bool Met { get; init; }
}

/// <summary>A bill standing on a bench.</summary>
public sealed record LedgerBillView
{
    public string ID { get; init; } = "";

    public string Bench { get; init; } = "";

    public string Kind { get; init; } = "";

    public string Recipe { get; init; } = "";

    public string Mode { get; init; } = "";

    public int Target { get; init; }

    public string Worker { get; init; } = "";

    public bool Spent { get; init; }

    public string State { get; init; } = "";

    public int Rounds { get; init; }

    public int GraceLeft { get; init; }

    public IReadOnlyList<string> Filter { get; init; } = [];
}

/// <summary>A throughput shortfall in product units per day.</summary>
public sealed record UnmetView(string BenchKind, double ShortPerDay, UnmetReason Reason);

/// <summary>MaintainTrade's latest declaration: the silver gap, what in flight netted off it and the best scored candidates.</summary>
public sealed record ExportView
{
    public bool GapKnown { get; init; }

    public double Gap { get; init; }

    public double InFlight { get; init; }

    /// <summary>The gap left to order against once the goods in flight are netted.</summary>
    public double Remaining { get; init; }

    public IReadOnlyList<ExportCandidateView> Candidates { get; init; } = [];

    /// <summary>Counts the candidates that never reached an order, by reason.</summary>
    public IReadOnlyList<ExportDropView> Dropped { get; init; } = [];
}

/// <summary>How many candidates one reason dropped.</summary>
public sealed record ExportDropView(ExportDrop Reason, int Count);

/// <summary>One scored sale good; Ordered marks the ones the Round declared.</summary>
public sealed record ExportCandidateView
{
    public string Recipe { get; init; } = "";

    public string BenchKind { get; init; } = "";

    public string Product { get; init; } = "";

    public string Stuff { get; init; } = "";

    public string Worker { get; init; } = "";

    public double Value { get; init; }

    public double Net { get; init; }

    public double Ticks { get; init; }

    public double Score { get; init; }

    public bool Ordered { get; init; }
}

public static partial class PolicyFunctions
{
    // Ledger view statuses.

    /// <summary>No review has filed the ledger yet.</summary>
    public const string LedgerViewNone = "no_review";

    /// <summary>No planner has declared into the ledger.</summary>
    public const string LedgerViewInactive = "inactive";

    /// <summary>The bench readback was unread, so nothing was diffed.</summary>
    public const string LedgerViewUnread = "readback_unknown";

    /// <summary>The Round declared and diffed.</summary>
    public const string LedgerViewReconciled = "reconciled";

    // Order states in the view.

    /// <summary>Every wanted copy stands on a bench.</summary>
    public const string OrderPlaced = "placed";

    /// <summary>The Round's plan places at least one copy.</summary>
    public const string OrderPlacing = "placing";

    /// <summary>At least one copy fits no bench (Reason says why).</summary>
    public const string OrderUnplaced = "unplaced";

    // Orphan states in the view.

    /// <summary>The grace is spent; this Round's plan removes the bill.</summary>
    public const string OrphanRemoving = "removing";

    /// <summary>Undeclared, counting down GraceLeft Rounds.</summary>
    public const string OrphanPending = "pending_removal";

    /// <summary>Undeclared, but a planner abstained so nothing is removed.</summary>
    public const string OrphanHeld = "held_by_abstain";

    // Placement outcomes in the view: what the placement index holds for the latest production_bill of an order.

    /// <summary>Native took the bill.</summary>
    public const string AttemptAccepted = "accepted";

    /// <summary>Native refused it (Code and Reason say why).</summary>
    public const string AttemptRefused = "refused";

    /// <summary>Sent, but native's answer is unknown or it never left.</summary>
    public const string AttemptUnconfirmed = "unconfirmed";

    /// <summary>Committed, not yet dispatched.</summary>
    public const string AttemptPending = "pending";

    // Ledger row sections: one per table the Grafana ledger dashboard shows.
    public const string LedgerSectionRound = "round";

    public const string LedgerSectionOrder = "order";

    public const string LedgerSectionOrphan = "orphan";

    public const string LedgerSectionExcluded = "excluded";

    public const string LedgerSectionUnmet = "unmet";

    public const string LedgerSectionWindow = "window";

    public const string LedgerSectionDeclarer = "declarer";

    public const string LedgerSectionExportGap = "export_gap";

    public const string LedgerSectionExportCandidate = "export_candidate";

    public const string LedgerSectionExportDrop = "export_drop";

    /// <summary>One planner's declaration summary.</summary>
    public static LedgerDeclarerView DeclarerView(string name, Declared d) =>
        new(name, d.Orders.Count, [.. d.Abstains.Select(a => new AbstainView(a.Concern.Value, a.Fact))]);

    /// <summary>The view of one export declaration against the gap it read. The candidates are the best scored pairs plus every ordered one.</summary>
    public static ExportView NewExportView(ExportPlan plan, Fact<double> gap)
    {
        var dropped = new List<ExportDropView>();
        foreach (var why in AllExportDrops)
        {
            if (plan.Dropped.TryGetValue(why, out var n) && n > 0)
            {
                dropped.Add(new ExportDropView(why, n));
            }
        }
        var gapKnown = gap.TryGetValue(out var g);
        var shown = new List<ExportCandidate>(plan.Candidates);
        foreach (var c in plan.Ranked)
        {
            if (!SameCandidate(shown, c))
            {
                shown.Add(c);
            }
        }
        var candidates = shown
            .Select(c => new ExportCandidateView
            {
                Recipe = c.Recipe,
                BenchKind = c.BenchKind,
                Product = c.Product.Value,
                Stuff = c.Stuff.Value,
                Worker = c.Worker.Value,
                Value = c.Value,
                Net = c.Net,
                Ticks = c.Ticks,
                Score = c.Score,
                Ordered = SameCandidate(plan.Ranked, c),
            })
            .ToList();
        return new ExportView
        {
            InFlight = plan.InFlight,
            GapKnown = gapKnown,
            Gap = gapKnown ? g : 0,
            Remaining = gapKnown ? Math.Max(0, g - plan.InFlight) : 0,
            Candidates = candidates,
            Dropped = dropped,
        };
    }

    // Whether list holds c, by identity: ExportCandidate carries slices.
    private static bool SameCandidate(IReadOnlyList<ExportCandidate> list, ExportCandidate c) =>
        list.Any(o => o.Recipe == c.Recipe && o.Stuff == c.Stuff && o.Worker == c.Worker && o.BenchKind == c.BenchKind);

    /// <summary>An empty view with the given status.</summary>
    public static LedgerView NewLedgerView(string status, long tick) => new() { Status = status, Tick = tick, GraceRounds = OrphanGraceRounds };

    /// <summary>Projects one Round's ledger state. The result shares none of the dispatcher memory it reads.</summary>
    public static LedgerView BuildLedgerView(LedgerViewInput input)
    {
        var declared = input.Declarers.Select(d => d.Declared).ToList();
        var declarers = input.Declarers.Select(d => DeclarerView(d.Name, d.Declared)).ToList();
        var (wanted, abstain) = WantedOrders(declared);
        var owners = OrderOwners(declared);
        var matched = new Dictionary<string, List<string>>();
        foreach (var b in input.Plan.Matched)
        {
            Append(matched, b.Spec.Key(), b.Bench);
        }
        var placing = new Dictionary<string, List<string>>();
        foreach (var p in input.Placed)
        {
            Append(placing, p.Spec.Key(), p.Bench);
        }
        var unplaced = new Dictionary<string, int>();
        foreach (var o in input.Unplaced)
        {
            unplaced[o.Key()] = unplaced.GetValueOrDefault(o.Key()) + 1;
        }
        var stockKnown = input.Stock.TryGetValue(out var stock);
        var orders = new List<LedgerOrderView>();
        foreach (var k in wanted.Keys.Order(StringComparer.Ordinal))
        {
            var o = wanted[k];
            var d = input.Dispatch.GetValueOrDefault(k) ?? new OrderDispatch();
            var row = new LedgerOrderView
            {
                Key = k,
                Owners = owners.TryGetValue(k, out var names) ? [.. names.Select(c => c.Value)] : [],
                Recipe = o.Recipe,
                Product = o.Product.Value,
                Mode = BillModeName(o.Mode),
                Target = o.Target,
                BenchKind = o.BenchKind,
                Worker = o.Worker,
                Ingredients = [.. o.Ingredients.Items],
                Copies = Math.Max(d.Copies, 1),
                Eligible = d.Eligible,
                State = OrderPlaced,
                Benches = SortedStrings(matched.GetValueOrDefault(k)),
                Placing = SortedStrings(placing.GetValueOrDefault(k)),
                Unplaced = unplaced.GetValueOrDefault(k),
                Demand = d.Demand.TryGetValue(out var demand) ? demand : null,
                Capacity = d.Capacity.TryGetValue(out var capacity) ? capacity : null,
                Wanted = d.Wanted.TryGetValue(out var benchesWanted) ? benchesWanted : null,
                Widen = input.Memory.Widen.GetValueOrDefault(k),
            };
            if (row.Unplaced > 0)
            {
                row = row with { State = OrderUnplaced, Reason = d.Eligible == 0 ? UnmetReason.NoBench : UnmetReason.SlotsFull };
            }
            else if (row.Placing.Count > 0)
            {
                row = row with { State = OrderPlacing };
            }
            if (input.Memory.Shortfalls.TryGetValue(k, out var s))
            {
                row = row with { Shortfall = new UnmetView(s.BenchKind, s.ShortPerDay, s.Reason) };
            }
            if (input.Memory.Windows.TryGetValue(k, out var w))
            {
                row = row with
                {
                    Window = new WindowView
                    {
                        Open = w.Open,
                        Start = w.Start,
                        Ends = w.Start + ThroughputWindowTicks,
                        StartStock = w.StartStock,
                        Deficit = w.Deficit,
                        PredictedPerDay = w.PerDay,
                        Carrying = w.Carrying,
                        Samples = w.Samples,
                        Busy = w.Busy,
                        Inactive = w.Inactive,
                        Met = w.Met,
                        ObservedGain = stockKnown ? stock.GetValueOrDefault(o.Product) - w.StartStock : null,
                    },
                };
            }
            orders.Add(row);
        }
        var orphans = new List<LedgerBillView>();
        foreach (var b in input.Plan.Remove)
        {
            orphans.Add(BillView(b, OrphanRemoving, OrphanGraceRounds));
        }
        foreach (var b in input.Plan.Held)
        {
            orphans.Add(BillView(b, abstain ? OrphanHeld : OrphanPending, input.Plan.Orphans.GetValueOrDefault(b.ID)));
        }
        return NewLedgerView(LedgerViewReconciled, input.Tick) with
        {
            FurtherBenches = [.. input.Further],
            Declarers = declarers,
            Abstained = abstain,
            Orders = orders,
            Orphans = orphans,
            Excluded = [.. input.Plan.Excluded.Select(b => BillView(b, "", 0))],
            Unmet = [.. input.Unmet.Select(u => new UnmetView(u.BenchKind, u.ShortPerDay, u.Reason))],
        };
    }

    private static void Append(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = map[key] = [];
        }
        list.Add(value);
    }

    // A bill row; rounds is the consecutive Rounds it has been undeclared.
    private static LedgerBillView BillView(ActualBill b, string state, int rounds) =>
        new()
        {
            ID = b.ID,
            Bench = b.Bench,
            Kind = Wire.Name(b.Kind),
            Recipe = b.Spec.Recipe,
            Mode = BillModeName(b.Spec.Mode),
            Target = b.Spec.Target,
            Worker = b.Spec.Worker,
            Spent = b.Spent,
            State = state,
            Rounds = rounds,
            GraceLeft = state == OrphanPending ? Math.Max(0, OrphanGraceRounds - rounds) : 0,
            Filter = [.. b.Spec.Ingredients.Items],
        };

    private static IReadOnlyList<string> SortedStrings(List<string>? s) => [.. (s ?? []).Order(StringComparer.Ordinal)];

    internal static string FormatInvariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
