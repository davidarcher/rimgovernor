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
public sealed record ForecastProjectProgress(string Project, double PointsPerTick);
public sealed record ForecastActivityOption(string Activity, string Pawn, Fact<double> WorkTicks, Fact<double> RecoveryLaborTicks)
{
    public RimGovernor.Contracts.Wire.Observations.ProductiveSkillWork? SkillWork { get; init; }
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
public sealed record ForecastActivityPlan(ForecastActivityAllocation[] Allocations, double CompletedAt, double LaborCost)
{
    public EquatableArray<ForecastEquipmentClaim> EquipmentClaims { get; init; }
    public EquatableArray<ForecastStockChange> StockChanges { get; init; }
}
public sealed record ForecastActivityComparison(ForecastActivityPlan? Selected, ForecastActivityPlan[] Alternatives,
    string Decision, string[] Findings, int Expansions, bool SearchTruncated);

public static class ForecastActivities
{
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
        var visited = new HashSet<long[]>(PrefixComparer.Instance);
        var taskIndex = tasks.Select((t, i) => (t.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        var pawnIndex = problem.Pawns.Select((p, i) => (p.Pawn, i)).ToDictionary(p => p.Pawn, p => p.i, StringComparer.Ordinal);
        // The prefix being searched by task index, its prerequisites by index, and each task's options by pawn.
        var placed = new ForecastActivityAllocation?[tasks.Length];
        var prerequisites = tasks.Select(t => t.Prerequisites.Select(id => taskIndex[id]).ToArray()).ToArray();
        var optionsOf = tasks.Select(t => problem.Options.Where(o => o.Activity == t.Id).OrderBy(o => o.Pawn, StringComparer.Ordinal).ToArray()).ToArray();
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
            // Interned and bitwise, so the prefix identity is exactly the old round-trip text's (#3468).
            var prefix = new long[4 * assigned.Count];
            var slot = 0;
            for (var index = 0; index < placed.Length; index++)
            {
                if (placed[index] is not { } a) continue;
                prefix[slot++] = (long)index << 32 | (uint)pawnIndex[a.Pawn];
                prefix[slot++] = BitConverter.DoubleToInt64Bits(a.Depart);
                prefix[slot++] = BitConverter.DoubleToInt64Bits(a.Start);
                prefix[slot++] = BitConverter.DoubleToInt64Bits(a.End);
            }
            if (!visited.Add(prefix)) return;
            if (assigned.Count == tasks.Length)
            {
                // Task order is id order, so the placed allocations are the rows by activity.
                var rows = new ForecastActivityAllocation[tasks.Length];
                for (var index = 0; index < rows.Length; index++) rows[index] = placed[index]!;
                var total = rows.Sum(a => a.LaborCost);
                if (!double.IsFinite(total)) throw new DomainException("activity plan cost overflow");
                var effects = new List<ForecastStockChange>();
                for (var index = 0; index < rows.Length; index++)
                {
                    var a = rows[index];
                    foreach (var output in tasks[index].Outputs) effects.Add(new(output.Resource, Known(a.End / GameTime.TicksPerDay), output.Units));
                    foreach (var input in tasks[index].Inputs)
                        effects.Add(new(input.Resource, Known(a.Start / GameTime.TicksPerDay), input.Units.TryGetValue(out var units) ? Known(-units) : default));
                }
                plans.Add(new(rows, rows.Max(a => a.End), total)
                {
                    StockChanges = EquatableArray.From(effects),
                    EquipmentClaims = EquatableArray.From(rows.SelectMany(a => options[(a.Activity, a.Pawn)].Equipment.Order(StringComparer.Ordinal)
                        .Select(e => new ForecastEquipmentClaim(e, Known(a.Start), Known(a.End))))),
                });
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
                foreach (var option in optionsOf[taskAt])
                {
                    // Unknown/refused options also consume the deterministic search budget.
                    if (!SpendExpansion()) return;
                    var pawn = pawns[option.Pawn];
                    if (!pawn.Windows.TryGetValue(out var windows)
                        || !pawn.OpportunityCostPerTick.TryGetValue(out var rate) || !option.WorkTicks.TryGetValue(out var work)
                        || !option.RecoveryLaborTicks.TryGetValue(out var recovery))
                    { findings.Add("unknown_capacity_or_cost:" + task.Id + ":" + pawn.Pawn); continue; }
                    if (option.Equipment.Any(id => !equipment.TryGetValue(id, out var e) || !e.Windows.IsKnown))
                    { findings.Add("unknown_equipment:" + task.Id + ":" + pawn.Pawn); continue; }
                    if (option.Equipment.Any(id => equipment[id].Location != (task.EquipmentLocation.Length == 0 ? task.Location : task.EquipmentLocation)))
                    { findings.Add("equipment_location_mismatch:" + task.Id + ":" + pawn.Pawn); continue; }
                    // Without equipment nothing is shared and service is ready when the task is.
                    var shared = option.Equipment.Count == 0 ? []
                        : assigned.Where(a => options[(a.Activity, a.Pawn)].Equipment.Intersect(option.Equipment).Any()).ToArray();
                    var serviceTimes = option.Equipment.Count == 0 ? [ready] : option.Equipment.SelectMany(id => equipment[id].Windows.Value.Select(w => w.Start))
                        .Concat(shared.Select(a => a.End)).Append(ready).Select(t => Math.Max(t, ready)).Distinct().Order().ToArray();
                    ForecastActivityAllocation? previous = null;
                    for (var i = assigned.Count - 1; i >= 0 && previous is null; i--)
                        if (assigned[i].Pawn == pawn.Pawn) previous = assigned[i];
                    var released = previous?.End ?? 0;
                    // Earliest departure and just-in-time departure are generic alternatives. Delayed travel
                    // assumes the baseline work is divisible; neither is a named helper/squad template.
                    foreach (var window in windows)
                    {
                        var location = previous is not null && previous.Depart >= window.Start && previous.End <= window.End
                            ? Known(tasks[taskIndex[previous.Activity]].EndLocation)
                            : window.StartLocation.IsKnown ? window.StartLocation : window.Start == 0 ? Known(pawn.Location) : Unknown<string>();
                        if (!location.TryGetValue(out var origin))
                        { if (!SpendExpansion()) return; findings.Add("unknown_window_location:" + pawn.Pawn); continue; }
                        var route = origin == task.Location ? Known(0.0)
                            : routes.TryGetValue((pawn.Pawn, origin, task.Location), out var observed) ? observed.Ticks : Unknown<double>();
                        if (!route.TryGetValue(out var travel))
                        { if (!SpendExpansion()) return; findings.Add("unknown_route:" + pawn.Pawn + ":" + origin + ":" + task.Location); continue; }
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
                                { findings.Add("capacity_or_deadline:" + task.Id + ":" + pawn.Pawn); continue; }
                                if (option.Equipment.Any(id => !equipment[id].Windows.Value.Any(w => start >= w.Start && end <= w.End))
                                    || shared.Any(a => start < a.End && a.Start < end && end > start && a.End > a.Start))
                                { findings.Add("equipment_occupied:" + task.Id + ":" + pawn.Pawn); continue; }
                                assigned.Add(placed[taskAt] = new(task.Id, pawn.Pawn, depart, arrive, start, end, travel, start - arrive, work,
                                    (end - depart) * rate, recovery) { Learning = learning.Evidence });
                                Search(assigned);
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
        var keys = new Dictionary<ForecastActivityPlan, string>(ReferenceEqualityComparer.Instance);
        string Key(ForecastActivityPlan plan)
        {
            if (!keys.TryGetValue(plan, out var key))
                keys.Add(plan, key = string.Join(";", plan.Allocations.Select(a => FormattableString.Invariant($"{a.Activity}:{a.Pawn}:{a.Depart:R}:{a.Start:R}:{a.End:R}"))));
            return key;
        }
        // Cost, then completion, then the plan's text: a total order, since no two plans share their text.
        var ranked = plans.ToArray();
        Array.Sort(ranked, (x, y) => x.LaborCost != y.LaborCost ? x.LaborCost.CompareTo(y.LaborCost)
            : x.CompletedAt != y.CompletedAt ? x.CompletedAt.CompareTo(y.CompletedAt) : string.CompareOrdinal(Key(x), Key(y)));
        if (truncated) findings.Add("search_truncated");
        if (!problem.AlternativeLaborCost.TryGetValue(out var alternative))
            return new(null, ranked, "unknown_alternative_cost", findings.Append("unknown_alternative_cost").ToArray(), expansions, truncated);
        var selected = ranked.FirstOrDefault(p => p.LaborCost < alternative);
        return new(selected, ranked, selected != null ? "lower_labor_cost" : ranked.Length == 0 ? "no_known_feasible_plan" : "retain_alternative",
            findings.ToArray(), expansions, truncated);
    }

    private sealed class PrefixComparer : IEqualityComparer<long[]>
    {
        internal static readonly PrefixComparer Instance = new();
        public bool Equals(long[]? x, long[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(long[] prefix)
        {
            var hash = new HashCode();
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(prefix.AsSpan()));
            return hash.ToHashCode();
        }
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
