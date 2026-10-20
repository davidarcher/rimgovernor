namespace RimGovernor.Brain.Policy;

/// <summary>Relative game ticks; availability excludes protected obligations. Cost is displaced baseline labor per tick.</summary>
public sealed record ForecastTimeWindow(double Start, double End)
{
    /// <summary>Position after protected time; unknown must not reuse the pawn's earlier location.</summary>
    public Fact<string> StartLocation { get; init; }
}
public sealed record ForecastPawnCapacity(string Pawn, string Location, Fact<EquatableArray<ForecastTimeWindow>> Windows,
    Fact<double> OpportunityCostPerTick);

/// <summary>Dependencies gate service, not preparatory travel. Each task and its output are counted once.</summary>
public sealed record ForecastActivityYield(Resource Resource, Fact<double> Units)
{
    public string Lot { get; init; } = "";
    public string LotLocation { get; init; } = "";
    public bool Perishable { get; init; }
    public Fact<double> FreshTicks { get; init; }
}
public sealed record ForecastActivity(string Id, string Location, string EndLocation, string[] Prerequisites)
{
    /// <summary>Relative earliest service time; dependencies may require a later start.</summary>
    public double NotBefore { get; init; }
    /// <summary>Additional known service-start bound. Null declares no bound; unknown prerequisite evidence is refused by composition.</summary>
    public double? LatestStart { get; init; }
    /// <summary>Known service-end bound for reusable facilities and exclusive availability.</summary>
    public double? LatestFinish { get; init; }
    /// <summary>Alternative realizations of one physical operation cannot execute together.</summary>
    public string ChoiceIdentity { get; init; } = "";
    public string EquipmentLocation { get; init; } = "";
    /// <summary>Ingestion-owned production: at least some output must satisfy an actual selected food use.
    /// Native indivisible batch remainder remains inventory, not additional practice value.</summary>
    public bool RequiresFoodUse { get; init; }
    /// <summary>One physical output per resource at completion; support activities normally have none.</summary>
    public EquatableArray<ForecastActivityYield> Outputs { get; init; }
    /// <summary>Exact resources consumed at service start; delivery-only movement must not consume them again.</summary>
    public EquatableArray<ForecastActivityYield> Inputs { get; init; }
    /// <summary>Research work: the project it advances and the points its work ticks add. Valued by the investment credit, never by a research-specific term.</summary>
    public ForecastProjectProgress? ProjectProgress { get; init; }
}
public sealed record ForecastProjectProgress(string Project, double PointsPerTick)
{
    /// <summary>The prefix of the project key of sowing a zone (the key is the sowing node's id, <see cref="ProjectGraph.SowId"/>, so the credit tier 2 prices lands on it).</summary>
    public const string SowPrefix = "sow:";
}
public sealed record ForecastActivityOption(string Activity, string Pawn, Fact<double> WorkTicks, Fact<double> RecoveryLaborTicks)
{
    public RimGovernor.Contracts.Wire.Observations.ProductiveSkillWork? SkillWork { get; init; }
    /// <summary>A tend's learning, paid once at completion; the observed WorkTicks stay the duration (#3632).</summary>
    public RimGovernor.Contracts.Wire.Observations.TendLearning? TendLearning { get; init; }
    /// <summary>Exclusive equipment needed throughout service; travel does not transport this equipment.</summary>
    public EquatableArray<string> Equipment { get; init; }
}
/// <summary>Route duration supplied by observation/estimation, never inferred from straight-line distance.</summary>
public sealed record ForecastActivityRoute(string Pawn, string From, string To, Fact<double> Ticks);

/// <summary>A single outcome graph and its alternative cost, not a second colony demand calculation.</summary>
public sealed record ForecastActivityProblem
{
    public ForecastActivity[] Activities { get; init; } = [];
    public ForecastPawnCapacity[] Pawns { get; init; } = [];
    public ForecastActivityOption[] Options { get; init; } = [];
    public ForecastActivityRoute[] Routes { get; init; } = [];
    public ForecastEquipmentCapacity[] Equipment { get; init; } = [];
    public Fact<double> Deadline { get; init; }
    public Fact<double> AlternativeLaborCost { get; init; }
    public int ExpansionBudget { get; init; } = 512;
}

public sealed record ForecastActivityAllocation(string Activity, string Pawn, double Depart, double Arrive,
    double Start, double End, double TravelTicks, double WaitTicks, double WorkTicks,
    double DisplacedLaborTicks, double RecoveryLaborTicks)
{
    public ForecastProductiveLearning? Learning { get; init; }
    public double LaborCost => DisplacedLaborTicks + RecoveryLaborTicks;
}
/// <summary>A schedule: its allocations by activity id, completion and labor cost. Its stock changes and equipment claims
/// are derived from the problem by <see cref="ForecastActivities.StockChangesOf"/> and
/// <see cref="ForecastActivities.EquipmentClaimsOf"/> for the plans a caller reads (#3468): a search emits thousands and ranks on neither.</summary>
public sealed record ForecastActivityPlan(ForecastActivityAllocation[] Allocations, double CompletedAt, double LaborCost);
public sealed record ForecastActivityComparison(ForecastActivityPlan? Selected, ForecastActivityPlan[] Alternatives,
    string Decision, string[] Findings, int Expansions, bool SearchTruncated);

public static class ForecastActivities
{
    /// <summary>A plan's stock changes: each task's outputs once at its completion day and its inputs at its start, an unknown
    /// input quantity staying unknown.</summary>
    public static EquatableArray<ForecastStockChange> StockChangesOf(ForecastActivityProblem problem, ForecastActivityPlan plan)
    {
        var effects = new List<ForecastStockChange>();
        foreach (var a in plan.Allocations)
        {
            var task = problem.Activities.Single(t => t.Id == a.Activity);
            foreach (var output in task.Outputs) effects.Add(new(output.Resource, Known(a.End / GameTime.TicksPerDay), output.Units));
            foreach (var input in task.Inputs)
                effects.Add(new(input.Resource, Known(a.Start / GameTime.TicksPerDay), input.Units.TryGetValue(out var units) ? Known(-units) : default));
        }
        return EquatableArray.From(effects);
    }

    /// <summary>A plan's equipment claims: each allocation's option equipment, in id order, over the allocation's service.</summary>
    public static EquatableArray<ForecastEquipmentClaim> EquipmentClaimsOf(ForecastActivityProblem problem, ForecastActivityPlan plan) =>
        EquatableArray.From(plan.Allocations.SelectMany(a => problem.Options.Single(o => o.Activity == a.Activity && o.Pawn == a.Pawn).Equipment
            .Order(StringComparer.Ordinal).Select(e => new ForecastEquipmentClaim(e, Known(a.Start), Known(a.End)))));

    // The (task, pawn) rejections a search reports once each, by kind (#3468): flagged in the loop, named after it.
    private static readonly string[] PairRejections = ["unknown_capacity_or_cost", "unknown_equipment", "equipment_location_mismatch", "capacity_or_deadline", "equipment_occupied"];

    /// <summary>Bounded joint assignment/list scheduling over a dependency DAG. No activity-name policy.</summary>
    public static ForecastActivityComparison CompareActivityPlans(ForecastActivityProblem problem)
    {
        ValidateActivityProblem(problem);
        var tasks = problem.Activities.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
        var pawns = problem.Pawns.ToDictionary(p => p.Pawn, StringComparer.Ordinal);
        var routes = problem.Routes.ToDictionary(r => (r.Pawn, r.From, r.To));
        var equipment = problem.Equipment.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var options = problem.Options.ToDictionary(o => (o.Activity, o.Pawn));
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        // The visited prefixes already keep one plan per assignment and time, so plans need no key until ranking ties.
        var plans = new List<ForecastActivityPlan>();
        var visited = new HashSet<(ulong, ulong)>();
        ulong prefixLow = 0, prefixHigh = 0;
        var taskIndex = tasks.Select((t, i) => (t.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        var pawnIndex = problem.Pawns.Select((p, i) => (p.Pawn, i)).ToDictionary(p => p.Pawn, p => p.i, StringComparer.Ordinal);
        // The prefix being searched by task index, its prerequisites by index, and each task's options by pawn.
        var placed = new ForecastActivityAllocation?[tasks.Length];
        var prerequisites = tasks.Select(t => t.Prerequisites.Select(id => taskIndex[id]).ToArray()).ToArray();
        var optionsOf = tasks.Select(t => problem.Options.Where(o => o.Activity == t.Id)
            .OrderBy(o => DirectCost(o, t, pawns, routes)).ThenBy(o => o.Pawn, StringComparer.Ordinal).ToArray()).ToArray();
        var rejected = new bool[PairRejections.Length * tasks.Length * Math.Max(1, problem.Pawns.Length)];
        void Reject(int kind, int taskAt, string pawn) => rejected[(kind * tasks.Length + taskAt) * problem.Pawns.Length + pawnIndex[pawn]] = true;
        var ready1 = new double[1];
        var expansions = 0;
        var truncated = false;
        if (!problem.Deadline.TryGetValue(out var deadline))
            return new(null, [], "unknown_deadline", ["unknown_deadline"], 0, false);

        bool SpendExpansion()
        {
            if (expansions == problem.ExpansionBudget) { truncated = true; return false; }
            expansions++;
            return true;
        }

        void Search(List<ForecastActivityAllocation> assigned)
        {
            // Independent DAG traversal orders can reach exactly the same scheduled prefix.
            // Retain every distinct assignment/time; do not spend the budget expanding identical state twice.
            // The prefix is known by the 128-bit hash each placement adds to and removes from (#3468).
            if (!visited.Add((prefixLow, prefixHigh))) return;
            if (assigned.Count == tasks.Length)
            {
                // Task order is id order, so the placed allocations are the rows by activity.
                var rows = new ForecastActivityAllocation[tasks.Length];
                double total = 0, completedAt = 0;
                for (var index = 0; index < rows.Length; index++)
                {
                    var a = rows[index] = placed[index]!;
                    total += a.LaborCost;
                    if (index == 0 || a.End > completedAt) completedAt = a.End;
                }
                if (!double.IsFinite(total)) throw new DomainException("activity plan cost overflow");
                plans.Add(new(rows, completedAt, total));
                return;
            }
            for (var taskAt = 0; taskAt < tasks.Length; taskAt++)
            {
                if (placed[taskAt] is not null) continue;
                double after = 0;
                var waiting = false;
                for (var i = 0; i < prerequisites[taskAt].Length && !waiting; i++)
                {
                    if (placed[prerequisites[taskAt][i]] is not { } prerequisite) { waiting = true; continue; }
                    if (i == 0 || prerequisite.End > after) after = prerequisite.End;
                }
                if (waiting) continue;
                var task = tasks[taskAt];
                var ready = Math.Max(task.NotBefore, after);
                foreach (var option in Continuing(optionsOf[taskAt], prerequisites[taskAt], placed))
                {
                    // Unknown/refused options also consume the deterministic search budget.
                    if (!SpendExpansion()) return;
                    var pawn = pawns[option.Pawn];
                    if (!pawn.Windows.TryGetValue(out var windows)
                        || !pawn.OpportunityCostPerTick.TryGetValue(out var rate) || !option.WorkTicks.TryGetValue(out var work)
                        || !option.RecoveryLaborTicks.TryGetValue(out var recovery))
                    { Reject(0, taskAt, pawn.Pawn); continue; }
                    var unknownEquipment = false;
                    var mismatch = false;
                    var location = task.EquipmentLocation.Length == 0 ? task.Location : task.EquipmentLocation;
                    foreach (var id in option.Equipment)
                    {
                        if (!equipment.TryGetValue(id, out var e) || !e.Windows.IsKnown) { unknownEquipment = true; break; }
                        mismatch |= e.Location != location;
                    }
                    if (unknownEquipment) { Reject(1, taskAt, pawn.Pawn); continue; }
                    if (mismatch) { Reject(2, taskAt, pawn.Pawn); continue; }
                    // Without equipment nothing is shared and service is ready when the task is.
                    List<ForecastActivityAllocation>? shared = null;
                    double[] serviceTimes;
                    if (option.Equipment.Count == 0)
                    {
                        ready1[0] = ready;
                        serviceTimes = ready1;
                    }
                    else
                    {
                        shared = [];
                        foreach (var a in assigned)
                            if (options[(a.Activity, a.Pawn)].Equipment.Intersect(option.Equipment).Any()) shared.Add(a);
                        serviceTimes = option.Equipment.SelectMany(id => equipment[id].Windows.Value.Select(w => w.Start))
                            .Concat(shared.Select(a => a.End)).Append(ready).Select(t => Math.Max(t, ready)).Distinct().Order().ToArray();
                    }
                    ForecastActivityAllocation? previous = null;
                    for (var i = assigned.Count - 1; i >= 0 && previous is null; i--)
                        if (assigned[i].Pawn == pawn.Pawn) previous = assigned[i];
                    var released = previous?.End ?? 0;
                    // Earliest departure and just-in-time departure are generic alternatives. Delayed travel
                    // assumes the baseline work is divisible; neither is a named helper/squad template.
                    foreach (var window in windows)
                    {
                        var origin = previous is not null && previous.Depart >= window.Start && previous.End <= window.End
                            ? Known(tasks[taskIndex[previous.Activity]].EndLocation)
                            : window.StartLocation.IsKnown ? window.StartLocation : window.Start == 0 ? Known(pawn.Location) : Unknown<string>();
                        if (!origin.TryGetValue(out var from))
                        { if (!SpendExpansion()) return; findings.Add("unknown_window_location:" + pawn.Pawn); continue; }
                        var route = from == task.Location ? Known(0.0)
                            : routes.TryGetValue((pawn.Pawn, from, task.Location), out var observed) ? observed.Ticks : Unknown<double>();
                        if (!route.TryGetValue(out var travel))
                        { if (!SpendExpansion()) return; findings.Add("unknown_route:" + pawn.Pawn + ":" + from + ":" + task.Location); continue; }
                        var free = Math.Max(released, window.Start);
                        foreach (var serviceReady in serviceTimes)
                        {
                            // Earliest, then just-in-time when it differs.
                            var justInTime = Math.Max(free, serviceReady - travel);
                            for (var departure = 0; departure < (justInTime.Equals(free) ? 1 : 2); departure++)
                            {
                                var depart = departure == 0 ? free : justInTime;
                                if (!SpendExpansion()) return;
                                var arrive = depart + travel;
                                var start = Math.Max(arrive, serviceReady);
                                var learning = ForecastProductiveWorkPolicy.ForecastProductiveWork(option, assigned, start);
                                if (!learning.WorkTicks.TryGetValue(out var duration))
                                { findings.Add("productive_work_unassessed:" + task.Id + ":" + learning.Reason); continue; }
                                work = duration;
                                var end = start + work;
                                var cost = (end - depart) * rate + recovery;
                                if (!double.IsFinite(end) || !double.IsFinite(cost)) throw new DomainException("activity estimate overflow");
                                if (end > window.End || end > deadline || task.LatestStart is { } latest && start > latest
                                    || task.LatestFinish is { } latestFinish && end > latestFinish)
                                { Reject(3, taskAt, pawn.Pawn); continue; }
                                if (EquipmentOccupied(option, equipment, shared, start, end))
                                { Reject(4, taskAt, pawn.Pawn); continue; }
                                assigned.Add(placed[taskAt] = new(task.Id, pawn.Pawn, depart, arrive, start, end, travel, start - arrive, work,
                                    (end - depart) * rate, recovery) { Learning = learning.Evidence });
                                var (low, high) = PrefixHash(taskAt, pawnIndex[pawn.Pawn], depart, start, end);
                                prefixLow += low; prefixHigh += high;
                                Search(assigned);
                                prefixLow -= low; prefixHigh -= high;
                                assigned.RemoveAt(assigned.Count - 1);
                                placed[taskAt] = null;
                                if (truncated) return;
                            }
                        }
                    }
                }
            }
        }
        Search([]);
        for (var kind = 0; kind < PairRejections.Length; kind++)
            for (var taskAt = 0; taskAt < tasks.Length; taskAt++)
                for (var pawnAt = 0; pawnAt < problem.Pawns.Length; pawnAt++)
                    if (rejected[(kind * tasks.Length + taskAt) * problem.Pawns.Length + pawnAt])
                        findings.Add(PairRejections[kind] + ":" + tasks[taskAt].Id + ":" + problem.Pawns[pawnAt].Pawn);
        var ranked = RankPlans(plans);
        if (truncated) findings.Add("search_truncated");
        if (!problem.AlternativeLaborCost.TryGetValue(out var alternative))
            return new(null, ranked, "unknown_alternative_cost", findings.Append("unknown_alternative_cost").ToArray(), expansions, truncated);
        var selected = ranked.FirstOrDefault(p => p.LaborCost < alternative);
        return new(selected, ranked, selected != null ? "lower_labor_cost" : ranked.Length == 0 ? "no_known_feasible_plan" : "retain_alternative",
            findings.ToArray(), expansions, truncated);
    }

    /// <summary>A task's options with the pawns its placed prerequisites went to first (#3624), each part in the
    /// <see cref="DirectCost"/> order: the cook that made a meal tries carrying it before a cheaper hand that would wait for it,
    /// so ordering by cost alone does not lead the bounded search into chains of waits.</summary>
    private static IEnumerable<ForecastActivityOption> Continuing(ForecastActivityOption[] options, int[] prerequisites, ForecastActivityAllocation?[] placed)
    {
        if (prerequisites.Length == 0) return options;
        var pawns = prerequisites.Select(i => placed[i]!.Pawn).ToHashSet(StringComparer.Ordinal);
        return options.Where(o => pawns.Contains(o.Pawn)).Concat(options.Where(o => !pawns.Contains(o.Pawn)));
    }

    /// <summary>The search's order of a task's options (#3624): the labor the option costs from where its pawn stands (work plus
    /// the walk to the task at the pawn's opportunity cost, plus recovery), the plans' own ranking measure, unknown last. The
    /// bounded search reaches the plans that put its first options first, so trying pawns in id order handed the early tasks to
    /// whichever pawn sorted first: a level-6 cook took the first three meals beside a level-15 one a few cells away. The order
    /// only decides which plans a truncated search reaches; ranking still decides among them.</summary>
    private static double DirectCost(ForecastActivityOption option, ForecastActivity task, Dictionary<string, ForecastPawnCapacity> pawns,
        Dictionary<(string Pawn, string From, string To), ForecastActivityRoute> routes)
    {
        if (!option.WorkTicks.TryGetValue(out var work) || !option.RecoveryLaborTicks.TryGetValue(out var recovery)
            || !pawns.TryGetValue(option.Pawn, out var pawn) || !pawn.OpportunityCostPerTick.TryGetValue(out var rate)) return double.PositiveInfinity;
        var travel = 0d;
        if (pawn.Location != task.Location && !(routes.TryGetValue((option.Pawn, pawn.Location, task.Location), out var route)
            && route.Ticks.TryGetValue(out travel))) return double.PositiveInfinity;
        var cost = (work + travel) * rate + recovery;
        return double.IsFinite(cost) ? cost : double.PositiveInfinity;
    }

    private static bool EquipmentOccupied(ForecastActivityOption option, Dictionary<string, ForecastEquipmentCapacity> equipment,
        List<ForecastActivityAllocation>? shared, double start, double end)
    {
        foreach (var id in option.Equipment)
        {
            var inside = false;
            foreach (var w in equipment[id].Windows.Value)
                if (start >= w.Start && end <= w.End) { inside = true; break; }
            if (!inside) return true;
        }
        if (shared is not null)
            foreach (var a in shared)
                if (start < a.End && a.Start < end && end > start && a.End > a.Start) return true;
        return false;
    }

    /// <summary>
    /// Cost, then completion, then the plan's text: a total order, since no two plans share their text. The text
    /// (<c>activity:pawn:depart:start:end</c> per allocation, joined by <c>;</c>, round-trip doubles) is formatted into one
    /// pooled buffer only for the plans that tie on cost and completion, and compared ordinally there (#3468).
    /// </summary>
    private static ForecastActivityPlan[] RankPlans(List<ForecastActivityPlan> plans)
    {
        var count = plans.Count;
        var order = new int[count];
        for (var i = 0; i < count; i++) order[i] = i;
        var keys = new PlanKeys(plans);
        order.AsSpan().Sort(keys);
        keys.Return();
        var ranked = new ForecastActivityPlan[count];
        for (var i = 0; i < count; i++) ranked[i] = plans[order[i]];
        return ranked;
    }

    private sealed class PlanKeys(List<ForecastActivityPlan> plans) : IComparer<int>
    {
        private readonly int[] offsets = new int[plans.Count];
        private readonly int[] lengths = new int[plans.Count];
        private char[] text = System.Buffers.ArrayPool<char>.Shared.Rent(1024);
        private int used;

        public int Compare(int x, int y)
        {
            ForecastActivityPlan a = plans[x], b = plans[y];
            if (a.LaborCost != b.LaborCost) return a.LaborCost.CompareTo(b.LaborCost);
            if (a.CompletedAt != b.CompletedAt) return a.CompletedAt.CompareTo(b.CompletedAt);
            if (x == y) return 0;
            Format(x); Format(y);
            return text.AsSpan(offsets[x], lengths[x]).SequenceCompareTo(text.AsSpan(offsets[y], lengths[y]));
        }

        private void Format(int index)
        {
            if (lengths[index] != 0) return;
            var start = used;
            foreach (var a in plans[index].Allocations)
            {
                if (used != start) Append(';');
                Append(a.Activity); Append(':'); Append(a.Pawn); Append(':');
                Append(a.Depart); Append(':'); Append(a.Start); Append(':'); Append(a.End);
            }
            // An empty text is a plan with no allocations, kept distinct from one not yet formatted.
            if (used == start) Append('\0');
            offsets[index] = start;
            lengths[index] = used - start;
        }

        private void Append(char c)
        {
            Reserve(1);
            text[used++] = c;
        }

        private void Append(string s)
        {
            Reserve(s.Length);
            s.CopyTo(text.AsSpan(used));
            used += s.Length;
        }

        private void Append(double value)
        {
            Reserve(32);
            value.TryFormat(text.AsSpan(used), out var written, "R", System.Globalization.CultureInfo.InvariantCulture);
            used += written;
        }

        private void Reserve(int more)
        {
            if (used + more <= text.Length) return;
            var grown = System.Buffers.ArrayPool<char>.Shared.Rent(Math.Max(text.Length * 2, used + more));
            text.AsSpan(0, used).CopyTo(grown);
            System.Buffers.ArrayPool<char>.Shared.Return(text);
            text = grown;
        }

        public void Return() => System.Buffers.ArrayPool<char>.Shared.Return(text);
    }

    /// <summary>Two independent 64-bit mixes of one placed allocation (its task and pawn indexes and the raw bits of its
    /// times). A prefix's hash is their sums over its allocations: the order that placed them does not matter, and a
    /// placement adds one term and its undoing subtracts it (#3468).</summary>
    private static (ulong Low, ulong High) PrefixHash(int taskAt, int pawnAt, double depart, double start, double end)
    {
        var key = (ulong)taskAt << 32 | (uint)pawnAt;
        ulong departBits = (ulong)BitConverter.DoubleToInt64Bits(depart), startBits = (ulong)BitConverter.DoubleToInt64Bits(start),
            endBits = (ulong)BitConverter.DoubleToInt64Bits(end);
        return (Mix(0x9E3779B97F4A7C15UL, key, departBits, startBits, endBits), Mix(0xC2B2AE3D27D4EB4FUL, key, departBits, startBits, endBits));
    }

    private static ulong Mix(ulong seed, ulong key, ulong depart, ulong start, ulong end)
    {
        var hash = Scramble(seed ^ key);
        hash = Scramble(hash ^ depart);
        hash = Scramble(hash ^ start);
        return Scramble(hash ^ end);
    }

    // SplitMix64's finalizer: every input bit reaches every output bit.
    private static ulong Scramble(ulong x)
    {
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        return x ^ x >> 31;
    }

    internal static void ValidateActivityProblem(ForecastActivityProblem p, bool allowEmpty = false)
    {
        static bool Id(string id) => !string.IsNullOrWhiteSpace(id) && !id.Contains(':') && !id.Contains(';');
        static void Number(Fact<double> fact)
        {
            if (fact.TryGetValue(out var value) && (!double.IsFinite(value) || value < 0)) throw new DomainException("invalid activity estimate");
        }
        if (p.ExpansionBudget is < 1 or > 4096 || p.Activities.Length > 32 || !allowEmpty && p.Activities.Length == 0
            || p.Activities.Any(t => !Id(t.Id) || !Id(t.Location) || !Id(t.EndLocation))
            || p.Activities.Select(t => t.Id).Distinct().Count() != p.Activities.Length
            || p.Pawns.Any(a => !Id(a.Pawn) || !Id(a.Location)) || p.Pawns.Select(a => a.Pawn).Distinct().Count() != p.Pawns.Length)
            throw new DomainException("invalid activity graph or search budget");
        var tasks = p.Activities.ToDictionary(t => t.Id);
        if (p.Equipment.Select(e => e.Id).Distinct().Count() != p.Equipment.Length)
            throw new DomainException("duplicate equipment capacity");
        foreach (var equipment in p.Equipment)
        {
            if (!Id(equipment.Id) || !Id(equipment.Location)) throw new DomainException("invalid equipment identity or location");
            if (!equipment.Windows.TryGetValue(out var windows)) continue;
            double end = 0;
            foreach (var window in windows)
            {
                if (!double.IsFinite(window.Start) || !double.IsFinite(window.End) || window.Start < end || window.End < window.Start)
                    throw new DomainException("invalid equipment availability");
                end = window.End;
            }
        }
        var pawns = p.Pawns.Select(a => a.Pawn).ToHashSet();
        foreach (var task in p.Activities)
        {
            if (!double.IsFinite(task.NotBefore) || task.NotBefore < 0) throw new DomainException("invalid earliest activity time");
            if (task.LatestStart is { } latest && (!double.IsFinite(latest) || latest < task.NotBefore))
                throw new DomainException("invalid latest activity time");
            if (task.LatestFinish is { } finish && (!double.IsFinite(finish) || finish < task.NotBefore))
                throw new DomainException("invalid latest activity finish");
            if (task.Prerequisites.Distinct().Count() != task.Prerequisites.Length || task.Prerequisites.Any(id => !tasks.ContainsKey(id)))
                throw new DomainException("invalid activity dependency");
            if (task.Outputs.Select(o => o.Resource).Distinct().Count() != task.Outputs.Count
                || task.Inputs.Select(o => (o.Resource, o.Lot, o.LotLocation)).Distinct().Count() != task.Inputs.Count)
                throw new DomainException("duplicate activity output");
            foreach (var output in task.Outputs.Concat(task.Inputs))
            {
                if (!AdmissionRules.ValidResource(output.Resource)) throw new DomainException("invalid activity output resource");
                Number(output.Units);
            }
        }
        var remaining = tasks.Keys.ToHashSet();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => tasks[id].Prerequisites.All(d => !remaining.Contains(d))).ToArray();
            if (ready.Length == 0) throw new DomainException("cyclic activity dependency");
            foreach (var id in ready) remaining.Remove(id);
        }
        Number(p.Deadline); Number(p.AlternativeLaborCost);
        foreach (var pawn in p.Pawns)
        {
            Number(pawn.OpportunityCostPerTick);
            if (pawn.Windows.TryGetValue(out var windows))
            {
                double end = 0;
                foreach (var window in windows)
                {
                    if (!double.IsFinite(window.Start) || !double.IsFinite(window.End) || window.Start < end || window.End < window.Start)
                        throw new DomainException("invalid pawn availability");
                    end = window.End;
                }
            }
        }
        if (p.Options.Select(o => (o.Activity, o.Pawn)).Distinct().Count() != p.Options.Length
            || p.Routes.Select(r => (r.Pawn, r.From, r.To)).Distinct().Count() != p.Routes.Length)
            throw new DomainException("duplicate activity option or route");
        foreach (var option in p.Options)
        {
            if (option.Equipment.Any(e => !Id(e)) || option.Equipment.Distinct().Count() != option.Equipment.Count)
                throw new DomainException("invalid equipment requirement");
            if (!tasks.ContainsKey(option.Activity) || !pawns.Contains(option.Pawn)) throw new DomainException("unknown activity option identity");
            Number(option.WorkTicks); Number(option.RecoveryLaborTicks);
        }
        foreach (var route in p.Routes)
        {
            if (!pawns.Contains(route.Pawn) || !Id(route.From) || !Id(route.To)) throw new DomainException("invalid route identity");
            Number(route.Ticks);
        }
    }
}
