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
public sealed record ForecastActivityYield(Resource Resource, Fact<double> Units);
public sealed record ForecastActivity(string Id, string Location, string EndLocation, string[] Prerequisites)
{
    /// <summary>One physical output per resource at completion; support activities normally have none.</summary>
    public EquatableArray<ForecastActivityYield> Outputs { get; init; }
}
public sealed record ForecastActivityOption(string Activity, string Pawn, Fact<double> WorkTicks, Fact<double> RecoveryLaborTicks);
/// <summary>Route duration supplied by observation/estimation, never inferred from straight-line distance.</summary>
public sealed record ForecastActivityRoute(string Pawn, string From, string To, Fact<double> Ticks);

/// <summary>A single outcome graph and its alternative cost, not a second colony demand calculation.</summary>
public sealed record ForecastActivityProblem
{
    public ForecastActivity[] Activities { get; init; } = [];
    public ForecastPawnCapacity[] Pawns { get; init; } = [];
    public ForecastActivityOption[] Options { get; init; } = [];
    public ForecastActivityRoute[] Routes { get; init; } = [];
    public Fact<double> Deadline { get; init; }
    public Fact<double> AlternativeLaborCost { get; init; }
    public int ExpansionBudget { get; init; } = 512;
}

public sealed record ForecastActivityAllocation(string Activity, string Pawn, double Depart, double Arrive,
    double Start, double End, double TravelTicks, double WaitTicks, double WorkTicks,
    double DisplacedLaborTicks, double RecoveryLaborTicks)
{
    public double LaborCost => DisplacedLaborTicks + RecoveryLaborTicks;
}
public sealed record ForecastActivityPlan(ForecastActivityAllocation[] Allocations, double CompletedAt, double LaborCost)
{
    public EquatableArray<ForecastStockChange> StockChanges { get; init; }
}
public sealed record ForecastActivityComparison(ForecastActivityPlan? Selected, ForecastActivityPlan[] Alternatives,
    string Decision, string[] Findings, int Expansions, bool SearchTruncated);

public static partial class PolicyFunctions
{
    /// <summary>Bounded joint assignment/list scheduling over a dependency DAG. No activity-name policy.</summary>
    public static ForecastActivityComparison CompareActivityPlans(ForecastActivityProblem problem)
    {
        ValidateActivityProblem(problem);
        var tasks = problem.Activities.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
        var pawns = problem.Pawns.ToDictionary(p => p.Pawn, StringComparer.Ordinal);
        var routes = problem.Routes.ToDictionary(r => (r.Pawn, r.From, r.To));
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var plans = new Dictionary<string, ForecastActivityPlan>(StringComparer.Ordinal);
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
            if (assigned.Count == tasks.Length)
            {
                var rows = assigned.OrderBy(a => a.Activity, StringComparer.Ordinal).ToArray();
                var key = string.Join(";", rows.Select(a => FormattableString.Invariant($"{a.Activity}:{a.Pawn}:{a.Depart:R}:{a.Start:R}:{a.End:R}")));
                var total = rows.Sum(a => a.LaborCost);
                if (!double.IsFinite(total)) throw new DomainException("activity plan cost overflow");
                var effects = rows.SelectMany(a => tasks.First(t => t.Id == a.Activity).Outputs
                    .Select(o => new ForecastStockChange(o.Resource, Known(a.End / GameTime.TicksPerDay), o.Units)));
                plans.TryAdd(key, new(rows, rows.Max(a => a.End), total) { StockChanges = EquatableArray.From(effects) });
                return;
            }
            var done = assigned.ToDictionary(a => a.Activity, StringComparer.Ordinal);
            foreach (var task in tasks.Where(t => !done.ContainsKey(t.Id) && t.Prerequisites.All(done.ContainsKey)))
            {
                var ready = task.Prerequisites.Select(id => done[id].End).DefaultIfEmpty(0).Max();
                foreach (var option in problem.Options.Where(o => o.Activity == task.Id).OrderBy(o => o.Pawn, StringComparer.Ordinal))
                {
                    // Unknown/refused options also consume the deterministic search budget.
                    if (!SpendExpansion()) return;
                    var pawn = pawns[option.Pawn];
                    if (!pawn.Windows.TryGetValue(out var windows)
                        || !pawn.OpportunityCostPerTick.TryGetValue(out var rate) || !option.WorkTicks.TryGetValue(out var work)
                        || !option.RecoveryLaborTicks.TryGetValue(out var recovery))
                    { findings.Add("unknown_capacity_or_cost:" + task.Id + ":" + pawn.Pawn); continue; }
                    var previous = assigned.LastOrDefault(a => a.Pawn == pawn.Pawn);
                    var released = previous?.End ?? 0;
                    // Earliest departure and just-in-time departure are generic alternatives. Delayed travel
                    // assumes the baseline work is divisible; neither is a named helper/squad template.
                    foreach (var window in windows)
                    {
                        var location = previous is not null && previous.Depart >= window.Start && previous.End <= window.End
                            ? Known(tasks.First(t => t.Id == previous.Activity).EndLocation)
                            : window.StartLocation.IsKnown ? window.StartLocation : window.Start == 0 ? Known(pawn.Location) : Unknown<string>();
                        if (!location.TryGetValue(out var origin))
                        { if (!SpendExpansion()) return; findings.Add("unknown_window_location:" + pawn.Pawn); continue; }
                        var route = origin == task.Location ? Known(0.0)
                            : routes.TryGetValue((pawn.Pawn, origin, task.Location), out var observed) ? observed.Ticks : Unknown<double>();
                        if (!route.TryGetValue(out var travel))
                        { if (!SpendExpansion()) return; findings.Add("unknown_route:" + pawn.Pawn + ":" + origin + ":" + task.Location); continue; }
                        var free = Math.Max(released, window.Start);
                        foreach (var depart in new[] { free, Math.Max(free, ready - travel) }.Distinct())
                        {
                            if (!SpendExpansion()) return;
                            var arrive = depart + travel;
                            var start = Math.Max(arrive, ready);
                            var end = start + work;
                            var cost = (end - depart) * rate + recovery;
                            if (!double.IsFinite(end) || !double.IsFinite(cost)) throw new DomainException("activity estimate overflow");
                            if (end > window.End || end > deadline)
                            { findings.Add("capacity_or_deadline:" + task.Id + ":" + pawn.Pawn); continue; }
                            assigned.Add(new(task.Id, pawn.Pawn, depart, arrive, start, end, travel, start - arrive, work,
                                (end - depart) * rate, recovery));
                            Search(assigned);
                            assigned.RemoveAt(assigned.Count - 1);
                            if (truncated) return;
                        }
                    }
                }
            }
        }
        Search([]);
        var ranked = plans.OrderBy(p => p.Value.LaborCost).ThenBy(p => p.Value.CompletedAt)
            .ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
        if (truncated) findings.Add("search_truncated");
        if (!problem.AlternativeLaborCost.TryGetValue(out var alternative))
            return new(null, ranked, "unknown_alternative_cost", findings.Append("unknown_alternative_cost").ToArray(), expansions, truncated);
        var selected = ranked.FirstOrDefault(p => p.LaborCost < alternative);
        return new(selected, ranked, selected != null ? "lower_labor_cost" : ranked.Length == 0 ? "no_known_feasible_plan" : "retain_alternative",
            findings.ToArray(), expansions, truncated);
    }

    private static void ValidateActivityProblem(ForecastActivityProblem p)
    {
        static bool Id(string id) => !string.IsNullOrWhiteSpace(id) && !id.Contains(':') && !id.Contains(';');
        static void Number(Fact<double> fact)
        {
            if (fact.TryGetValue(out var value) && (!double.IsFinite(value) || value < 0)) throw new DomainException("invalid activity estimate");
        }
        if (p.ExpansionBudget is < 1 or > 4096 || p.Activities.Length is < 1 or > 32
            || p.Activities.Any(t => !Id(t.Id) || !Id(t.Location) || !Id(t.EndLocation))
            || p.Activities.Select(t => t.Id).Distinct().Count() != p.Activities.Length
            || p.Pawns.Any(a => !Id(a.Pawn) || !Id(a.Location)) || p.Pawns.Select(a => a.Pawn).Distinct().Count() != p.Pawns.Length)
            throw new DomainException("invalid activity graph or search budget");
        var tasks = p.Activities.ToDictionary(t => t.Id);
        var pawns = p.Pawns.Select(a => a.Pawn).ToHashSet();
        foreach (var task in p.Activities)
        {
            if (task.Prerequisites.Distinct().Count() != task.Prerequisites.Length || task.Prerequisites.Any(id => !tasks.ContainsKey(id)))
                throw new DomainException("invalid activity dependency");
            if (task.Outputs.Select(o => o.Resource).Distinct().Count() != task.Outputs.Count)
                throw new DomainException("duplicate activity output");
            foreach (var output in task.Outputs)
            {
                if (!ValidResource(output.Resource)) throw new DomainException("invalid activity output resource");
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
