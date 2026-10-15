using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Transient mechanics attached to tasks, never a second execution journal.</summary>
public sealed record ForecastNeedTask(string Task, string Pawn, string Kind, string Target,
    Resource Resource, int Count, Fact<double> Gain, EquatableArray<NeedMemory> Thoughts)
{
    public string FoodReady { get; init; } = "";
    public Fact<double> FreshTicks { get; init; }
    public Fact<double> AvailableCount { get; init; }
    public string FreshFrom { get; init; } = "";
    public bool ThoughtsComplete { get; init; } = true;
}
public sealed record ForecastMaterialLot(string Lot, string Location, Resource Resource, Fact<double> Count)
{ public bool Perishable { get; init; } public Fact<double> FreshTicks { get; init; } }
public sealed record ForecastCoupledContext
{
    public required ForecastActivityProblem Activities { get; init; }
    public bool Truncated { get; init; }
    public required NeedsForecastRead Needs { get; init; }
    public required WorkResourceView Resources { get; init; }
    public ConstructionActivityRead Construction { get; init; } = new();
    public CoupledThermalRead? Thermal { get; init; }
    public EquatableArray<ForecastShelterPlan> Shelters { get; init; }
    public EquatableArray<ForecastMaterialLot> InitialLots { get; init; }
    public EquatableArray<ForecastNeedTask> NeedTasks { get; init; }
    internal IReadOnlyDictionary<(string Task, string Pawn), Methods.ActivityTaskCandidate> Bindings { get; init; } = new Dictionary<(string, string), Methods.ActivityTaskCandidate>();
    public EquatableArray<string> Findings { get; init; }
}
public sealed record ForecastCoupledOutcome(ForecastActivityPlan Plan, Fact<bool> Feasible,
    EquatableArray<ForecastStockTrajectory> Stocks,
    IReadOnlyDictionary<string, ForecastNeedsResult> Needs,
    EquatableArray<ForecastFinding> Findings)
{
    public EquatableArray<ForecastShelterOutcome> Shelters { get; init; }
    public EquatableArray<ForecastThermalExposure> ThermalExposure { get; init; }
    public EquatableArray<string> CompletedDevelopmentSites { get; init; }
    /// <summary>Known stationary-location exposure avoided relative to the same occupancy without proposed room changes.
    /// Unobserved transit/idle receive no benefit.</summary>
    public Fact<double> StationaryThermalRelief { get; init; }
    public string ThermalOccupancy { get; init; } = "";
}
public sealed record ForecastCoupledScenario(EquatableArray<ForecastCoupledOutcome> Alternatives,
    EquatableArray<string> Findings, bool Truncated)
{ public int Expansions { get; init; } }

public static partial class PolicyFunctions
{
    /// <summary>Composition boundary used by normal Rounds facts: one observed frame, stock owner and horizon.</summary>
    public static ColonyForecastRequest CoupledForecastRequestOf(ColonyForecastOrigin origin, double horizonDays,
        RoundsFacts facts, RoundsPolicy policy, DerivedDemand demand, ForecastActivityProblem activities,
        EquatableArray<ForecastNeedTask> needTasks, EquatableArray<ForecastCommitment> commitments,
        ColonyForecastDelta proposed)
    {
        if (facts.Tick.Value != origin.ObservedTick || demand.Work.Tick != facts.Tick
            || facts.ForecastObserved.Needs is not { } needs || needs.Tick != facts.Tick.Value
            || facts.WorkView is not { } resources || resources.Evidence.Tick != facts.Tick)
            throw new DomainException("coupled forecast requires one observed review frame");
        return new()
        {
            Origin = origin,
            HorizonDays = horizonDays,
            Demand = demand,
            Facts = ColonyForecastFactsOf(facts, policy),
            Commitments = commitments,
            Proposed = proposed,
            Coupled = new() { Activities = activities, Needs = needs, Resources = resources, NeedTasks = needTasks }
        };
    }

    public static ColonyForecastRequest CoupledForecastRequestOf(ColonyForecastOrigin origin, double horizonDays,
        RoundsFacts facts, RoundsPolicy policy, DerivedDemand demand, EquatableArray<ForecastCommitment> commitments,
        ColonyForecastDelta proposed)
    {
        var generated = facts.CoupledAlternatives ?? throw new DomainException("coupled observed review unavailable");
        return CoupledForecastRequestOf(origin, horizonDays, facts, policy, demand, generated.Context.Activities,
            generated.Context.NeedTasks, commitments, proposed) with
        { Coupled = generated.Context };
    }

    private static ColonyForecastScenario ForecastCoupledScenarioOf(ColonyForecastRequest request,
        IReadOnlyDictionary<string, ForecastCommitment> commitments)
    {
        var context = request.Coupled!;
        if (commitments.Values.Any(c => c.ActivityCompletions.Count != 0 && c.StockChanges.Count != 0))
            throw new DomainException("coupled commitments have one timed effect source");
        var horizon = request.HorizonDays * GameTime.TicksPerDay;
        if (horizon > GameTime.TicksPerDay || context.Needs.Tick != request.Origin.ObservedTick
            || context.Resources.Evidence.Tick.Value != request.Origin.ObservedTick)
            throw new DomainException("coupled forecast needs one observed tick and at most one day");
        var all = context.Activities.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.Ordinal);
        void Include(string id)
        {
            if (!all.TryGetValue(id, out var task)) throw new DomainException("unknown commitment activity: " + id);
            if (!included.Add(id)) return;
            foreach (var prerequisite in task.Prerequisites) Include(prerequisite);
        }
        foreach (var commitment in commitments.Values)
            foreach (var task in commitment.ActivityCompletions) Include(task);
        var findings = new SortedSet<string>(context.Findings, StringComparer.Ordinal);
        foreach (var gap in commitments.Values.Where(c => c.ActivityCompletions.Count == 0))
            findings.Add("commitment_capacity_unassessed:" + gap.Id);
        findings.UnionWith(context.Resources.Claims.Findings);
        foreach (var c in commitments.Values.Where(c => c.ActivityCompletions.Count == 0))
            foreach (var unsupported in c.UnsupportedEffects) findings.Add("commitment_gap:" + c.Id + ":" + unsupported);
        if (commitments.Values.Where(c => c.ExclusiveOutcome.Length != 0).GroupBy(c => c.ExclusiveOutcome).Any(g => g.Count() > 1)
    || commitments.Values.Any(c => c.Outcome is { } o && commitments.Values.Any(other => other.Id != c.Id
        && (o.Excludes.Contains(other.Id) || o.ExclusiveOutput.Length != 0 && o.ExclusiveOutput == other.Outcome?.ExclusiveOutput))))
            return ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
            { Coupled = new([], ["exclusive_outcome_conflict"], false) };
        var problem = context.Activities with
        {
            Activities = all.Values.Where(t => included.Contains(t.Id)).ToArray(),
            Options = context.Activities.Options.Where(o => included.Contains(o.Activity)).ToArray(),
            Deadline = Known(horizon)
        };
        if (problem.Activities.Where(t => t.ChoiceIdentity.Length != 0).GroupBy(t => t.ChoiceIdentity).Any(g => g.Count() > 1))
            return ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
            { Coupled = new([], ["exclusive_operation_conflict"], false) };
        if (included.Count > 32) return ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
        { Coupled = new([], [.. findings, "selected_activity_budget_truncated"], true) };
        var compared = included.Count == 0 ? null : CompareActivityPlans(problem);
        if (compared is not null)
        {
            findings.UnionWith(compared.Findings);
            if (compared.Alternatives.Length == 0) findings.Add("unassessed_no_schedule_within_horizon");
        }
        var alternatives = compared?.Alternatives ?? [new ForecastActivityPlan([], 0, 0)];
        var results = alternatives.Select(plan => EvaluateCoupledSchedule(request, commitments, problem, plan)).ToArray();
        // Inventory-only clients keep their conditional diagnostics; migrated consumers inspect each coupled alternative.
        return ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
        {
            MaterialAvailability = context.Resources.Materials,
            Coupled = new(EquatableArray.From(results), EquatableArray.From(findings), context.Truncated || (compared?.SearchTruncated ?? false)) { Expansions = compared?.Expansions ?? 0 },
        };
    }

    private static ForecastCoupledOutcome EvaluateCoupledSchedule(ColonyForecastRequest request,
        IReadOnlyDictionary<string, ForecastCommitment> commitments, ForecastActivityProblem problem, ForecastActivityPlan plan, HashSet<string>? unavailableWork = null)
    {
        var context = request.Coupled!;
        unavailableWork ??= new(StringComparer.Ordinal);
        var horizon = request.HorizonDays * GameTime.TicksPerDay;
        var findings = new List<ForecastFinding>();
        var tasks = problem.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var allocations = plan.Allocations.ToDictionary(a => a.Activity, StringComparer.Ordinal);
        var effects = new List<ForecastStockChange>();
        var completed = new Dictionary<string, Fact<bool>>(StringComparer.Ordinal);
        var lots = context.InitialLots.ToDictionary(l => (l.Lot, l.Location, l.Resource), l => l.Count);
        var expiry = context.InitialLots.Where(l => l.Perishable).GroupBy(l => l.Lot).ToDictionary(g => g.Key, g => g.First().FreshTicks);
        var material = CoupledMaterialBudgetOf(request, commitments, problem, plan);
        var balance = material.TryGetValue(out var budget)
            ? new Dictionary<Resource, Fact<double>>(budget) : new Dictionary<Resource, Fact<double>>();
        var physicalKnown = material.IsKnown;
        Fact<bool> feasible = Known(true);
        void Finding(string reason, string id, double time, Resource resource = default, Fact<double> units = default)
            => findings.Add(new(reason, id, resource, Known(time / GameTime.TicksPerDay), units));
        void Uncertain(string reason, string id, double time)
        { if (feasible != Known(false)) feasible = default; Finding(reason, id, time); }
        void Refuse(string reason, string id, double time)
        { feasible = Known(false); Finding(reason, id, time); }
        var external = commitments.Values.Where(c => c.ActivityCompletions.Count == 0)
            .SelectMany(c => c.StockChanges.Select(change => (Owner: c.Id, Change: change))).ToArray();
        foreach (var item in external.Where(e => !e.Change.Day.IsKnown || !e.Change.Units.IsKnown))
        { balance[item.Change.Resource] = default; Uncertain("unknown_external_stock_effect", item.Owner, 0); }
        var timedExternal = external.Where(e => e.Change.Day.IsKnown && e.Change.Units.IsKnown && e.Change.Day.Value <= request.HorizonDays)
            .OrderBy(e => e.Change.Day.Value).ThenBy(e => e.Owner, StringComparer.Ordinal).ToArray();
        var nextExternal = 0;
        void ApplyExternal(double time)
        {
            while (nextExternal < timedExternal.Length && timedExternal[nextExternal].Change.Day.Value * GameTime.TicksPerDay <= time)
            {
                var item = timedExternal[nextExternal++]; var change = item.Change;
                Expire(change.Day.Value * GameTime.TicksPerDay);
                if (change.Units.Value > 0)
                { balance[change.Resource] = default; Uncertain("unbound_external_output", item.Owner, change.Day.Value * GameTime.TicksPerDay); continue; }
                var physical = lots.Keys.Where(k => k.Resource == change.Resource && lots[k] != Known(0.0)).ToArray();
                if (physical.Length == 1 && lots[physical[0]].TryGetValue(out var units))
                    lots[physical[0]] = Known(Math.Max(0, units + change.Units.Value));
                else if (physical.Length > 0)
                {
                    foreach (var lot in physical) lots[lot] = default;
                    Uncertain("unbound_external_consumption", item.Owner, change.Day.Value * GameTime.TicksPerDay);
                }
                var before = balance.GetValueOrDefault(change.Resource, Known(0.0));
                balance[change.Resource] = before.IsKnown ? Known(before.Value + change.Units.Value) : default;
                if (before.IsKnown && before.Value + change.Units.Value < 0)
                    Refuse("external_stock_shortfall", item.Owner, change.Day.Value * GameTime.TicksPerDay);
            }
        }
        void Expire(double time)
        {
            foreach (var lot in lots.Keys.Where(k => expiry.TryGetValue(k.Lot, out var until) && until.IsKnown && until.Value <= time).ToArray())
            {
                if (!lots[lot].TryGetValue(out var count) || count <= 0) continue;
                lots[lot] = Known(0.0);
                var before = balance.GetValueOrDefault(lot.Resource, Known(0.0));
                balance[lot.Resource] = before.IsKnown ? Known(before.Value - count) : default;
                effects.Add(new(lot.Resource, Known(expiry[lot.Lot].Value / GameTime.TicksPerDay), Known(-count)));
                Finding("observed_lot_expired", lot.Lot, expiry[lot.Lot].Value, lot.Resource, Known(count));
            }
        }
        // Completion precedes dependent start for positive-duration work; zero-duration tasks cannot finance themselves.
        var events = plan.Allocations.SelectMany(a => new[] { (Time: a.Start, Order: 1, Start: true, Allocation: a),
            (Time: a.End, Order: a.End > a.Start ? 0 : 2, Start: false, Allocation: a) })
            .OrderBy(e => e.Time).ThenBy(e => e.Order).ThenBy(e => e.Allocation.Activity, StringComparer.Ordinal);
        foreach (var e in events)
        {
            ApplyExternal(e.Time);
            Expire(e.Time);
            var task = tasks[e.Allocation.Activity];
            if (e.Time > horizon) { Finding("beyond_horizon", task.Id, e.Time); continue; }
            if (e.Start)
            {
                if (unavailableWork.Contains(task.Id))
                { completed[task.Id] = Known(false); Refuse("work_outside_recovered_capacity", task.Id, e.Time); continue; }
                var dependencies = task.Prerequisites.Select(p => completed.GetValueOrDefault(p)).ToArray();
                if (dependencies.Any(p => p == Known(false)))
                { completed[task.Id] = Known(false); Refuse("failed_prerequisite", task.Id, e.Time); continue; }
                if (dependencies.Any(p => !p.IsKnown))
                { completed[task.Id] = default; Uncertain("unknown_prerequisite", task.Id, e.Time); continue; }
                var ingestion = context.NeedTasks.SingleOrDefault(n => n.Task == task.Id && n.Kind == "food");
                if (ingestion is not null)
                {
                    // Physical lot accounting below owns quantity and dependency completion owns access.
                    // Ingestion additionally needs the selected actual lot fresh through chewing.
                    var foodExpiry = task.Inputs.Where(i => i.Resource == ingestion.Resource).Select(i => expiry.GetValueOrDefault(i.Lot)).ToArray();
                    if (foodExpiry.Length == 0 || foodExpiry.Any(t => !t.IsKnown))
                    { completed[task.Id] = default; Uncertain("unknown_food_access_or_expiry", task.Id, e.Time); continue; }
                    if (foodExpiry.Any(t => e.Allocation.End >= t.Value))
                    { completed[task.Id] = Known(false); Refuse("food_unavailable_at_ingestion", task.Id, e.Time); continue; }

                }
                var inputs = task.Inputs.GroupBy(i => i.Resource).ToArray();
                if (!physicalKnown && inputs.Length != 0 || inputs.Any(g => g.Any(i => !i.Units.IsKnown)
                    || !balance.GetValueOrDefault(g.Key, Known(0.0)).IsKnown))
                { completed[task.Id] = default; Uncertain("unknown_timed_material", task.Id, e.Time); continue; }
                if (inputs.Any(g => balance.GetValueOrDefault(g.Key, Known(0.0)).Value < g.Sum(i => i.Units.Value)))
                { completed[task.Id] = Known(false); Refuse("timed_material_shortfall", task.Id, e.Time); continue; }
                var timedLots = task.Inputs.Where(i => i.Lot.Length != 0 && expiry.ContainsKey(i.Lot)).Select(i => expiry[i.Lot]).ToArray();
                if (timedLots.Any(t => !t.IsKnown))
                { completed[task.Id] = default; Uncertain("unknown_ingredient_expiry", task.Id, e.Time); continue; }
                if (timedLots.Any(t => t.Value <= e.Time))
                { completed[task.Id] = Known(false); Refuse("ingredient_expired_before_service", task.Id, e.Time); continue; }
                var lotInputs = task.Inputs.Where(i => i.Lot.Length != 0).GroupBy(i => (i.Lot, Location: i.LotLocation.Length == 0 ? task.Location : i.LotLocation, i.Resource)).ToArray();
                if (lotInputs.Any(g => !lots.GetValueOrDefault(g.Key, Known(0.0)).IsKnown))
                { completed[task.Id] = default; Uncertain("unknown_source_lot", task.Id, e.Time); continue; }
                if (lotInputs.Any(g => lots.GetValueOrDefault(g.Key, Known(0.0)).Value < g.Sum(i => i.Units.Value)))
                { completed[task.Id] = Known(false); Refuse("source_lot_shortfall", task.Id, e.Time); continue; }
                foreach (var group in lotInputs) lots[group.Key] = Known(lots.GetValueOrDefault(group.Key, Known(0.0)).Value - group.Sum(i => i.Units.Value));
                completed[task.Id] = Known(true);
                foreach (var group in inputs)
                {
                    var units = group.Sum(i => i.Units.Value);
                    balance[group.Key] = Known(balance.GetValueOrDefault(group.Key, Known(0.0)).Value - units);
                    effects.Add(new(group.Key, Known(e.Time / GameTime.TicksPerDay), Known(-units)));
                }
            }
            else if (completed.GetValueOrDefault(task.Id) == Known(true))
            {
                foreach (var output in task.Outputs)
                {
                    if (output.Lot.Length != 0)
                    {
                        if (output.Perishable && !expiry.ContainsKey(output.Lot)) expiry[output.Lot] = output.FreshTicks.IsKnown ? Known(e.Time + output.FreshTicks.Value) : default;
                        var key = (output.Lot, output.LotLocation.Length == 0 ? task.EndLocation : output.LotLocation, output.Resource);
                        var beforeLot = lots.GetValueOrDefault(key, Known(0.0));
                        lots[key] = beforeLot.IsKnown && output.Units.IsKnown ? Known(beforeLot.Value + output.Units.Value) : default;
                    }
                    var before = balance.GetValueOrDefault(output.Resource, Known(0.0));
                    balance[output.Resource] = before.IsKnown && output.Units.IsKnown ? Known(before.Value + output.Units.Value) : default;
                    effects.Add(new(output.Resource, Known(e.Time / GameTime.TicksPerDay), output.Units));
                }
            }
        }
        ApplyExternal(horizon);
        Expire(horizon);
        var needs = new Dictionary<string, ForecastNeedsResult>(StringComparer.Ordinal);
        var rejectedWork = new HashSet<string>(unavailableWork, StringComparer.Ordinal);
        foreach (var pawn in context.Needs.Pawns)
        {
            var observed = context.Resources.Evidence.Pawns.TryGetValue(out var rows) ? rows.SingleOrDefault(p => p.Pawn == pawn.Pawn) : null;
            var choices = new List<ForecastNeedChoice>();
            foreach (var task in context.NeedTasks.Where(t => t.Pawn == pawn.Pawn && allocations.ContainsKey(t.Task)))
            {
                var a = allocations[task.Task];
                if (completed.GetValueOrDefault(task.Task) != Known(true) || !task.Gain.TryGetValue(out var gain))
                { Uncertain("unassessed_need_completion", task.Task, a.End); continue; }
                choices.Add(new(task.Task, pawn.Pawn, task.Kind, task.Target, task.Resource.Value, task.Count,
                    a.Depart, a.Start, a.End, gain, task.Thoughts));
            }
            var productive = plan.Allocations.Where(a => a.Pawn == pawn.Pawn && context.NeedTasks.All(n => n.Task != a.Activity)).ToArray();
            var protectedTime = productive.Select(a => new ForecastOccupancy(a.Activity, pawn.Pawn, Known(a.Depart), Known(a.End), ForecastOccupancyState.Active)).ToList();
            if (context.Resources.Claims.Pawns.Contains(pawn.Pawn))
                protectedTime.Add(new("journal", pawn.Pawn, Known(0.0), default, ForecastOccupancyState.Active));
            var input = new ForecastCapacityInput
            {
                Pawn = new()
                {
                    Id = new(pawn.Pawn),
                    Available = observed?.Available ?? default,
                    Applies = observed?.Applies ?? default
                },
                Location = "start",
                HorizonTicks = horizon,
                ProtectTimetable = false,
                LocalHour = observed?.LocalHour ?? default,
                OpportunityCostPerTick = Known(1.0),
                CurrentWork = new(new(pawn.Pawn), observed?.Occupied ?? default, default,
                    observed is { Occupied.IsKnown: true, Occupied.Value: false } ? Known(0.0) : default),
                Occupancy = context.Resources.Claims.Truncated ? default : Known(EquatableArray.From(protectedTime))
            };
            // Needs recovery is assessed without masking physiological unavailability beneath planned work.
            var physiology = ForecastNeedsTimeline(pawn, input with { Occupancy = Known<EquatableArray<ForecastOccupancy>>([]) }, choices);
            if (!physiology.Capacity.Capacity.Windows.TryGetValue(out var able))
            { if (productive.Length != 0 || choices.Count != 0) Uncertain("unknown_future_capacity", pawn.Pawn, 0); }
            else foreach (var work in productive)
                if (!able.Any(w => w.Start <= work.Depart && w.End >= work.End))
                { rejectedWork.Add(work.Activity); completed[work.Activity] = Known(false); Refuse("work_outside_recovered_capacity", work.Activity, work.Start); }
            var projected = ForecastNeedsTimeline(pawn, input, choices);
            if (context.NeedTasks.Any(n => n.Pawn == pawn.Pawn && !n.ThoughtsComplete && allocations.ContainsKey(n.Task)))
                projected = projected with
                {
                    ThoughtOffsetAtHorizon = default,
                    MemoryAdjustedMoodTarget = default,
                    Mood = null,
                    Findings = [.. projected.Findings, "future_meal_thoughts_unassessed"]
                };
            needs[pawn.Pawn] = projected;
        }
        // Removing impossible work may invalidate its meals and therefore later work. Each pass removes at least one task.
        if (rejectedWork.Count > unavailableWork.Count)
            return EvaluateCoupledSchedule(request, commitments, problem, plan, rejectedWork);
        var accounting = new Dictionary<string, ForecastCommitment>(commitments, StringComparer.Ordinal)
        { ["coupled/timed-effects"] = new("coupled/timed-effects", EquatableArray.From(effects)) };
        var inventory = ForecastInventory(request.Facts.Stock, accounting, request.HorizonDays);
        findings.AddRange(inventory.Findings);
        var shelters = ForecastShelters(context.Shelters, plan with
        { Allocations = plan.Allocations.Where(a => completed.GetValueOrDefault(a.Activity) == Known(true)).ToArray() }, horizon);
        var exposure = ForecastThermalExposureOf(context, problem, plan, shelters, horizon);
        var existingRooms = EquatableArray.From(context.Shelters.Where(r => r.Prerequisites.Count == 0 && r.AlternativePrerequisites.Count == 0));
        var use = plan with { Allocations = plan.Allocations.Where(a => context.NeedTasks.Any(n => n.Task == a.Activity)).ToArray() };
        var changedUse = ForecastThermalExposureOf(context, problem, use, shelters, horizon);
        var unchanged = ForecastThermalExposureOf(context with { Shelters = existingRooms }, problem, use,
            ForecastShelters(existingRooms, plan, horizon), horizon);
        var reliefKnown = changedUse.Any(t => t.AssessedTicks > 0) && changedUse.Count == unchanged.Count && changedUse.Zip(unchanged)
            .All(pair => pair.First.Pawn == pair.Second.Pawn && pair.First.AssessedTicks == pair.Second.AssessedTicks);
        return new(plan, feasible, inventory.Stocks, needs, EquatableArray.From(findings))
        {
            Shelters = shelters, ThermalExposure = exposure,
            ThermalOccupancy = string.Join(";", context.NeedTasks.Where(n => allocations.ContainsKey(n.Task))
                .Select(n => (Need: n, Allocation: allocations[n.Task]))
                .OrderBy(p => p.Need.Pawn, StringComparer.Ordinal).ThenBy(p => p.Allocation.Arrive).ThenBy(p => p.Need.Kind, StringComparer.Ordinal)
                .Select(p => FormattableString.Invariant($"{p.Need.Pawn}|{p.Need.Kind}|{tasks[p.Need.Task].Location}|{p.Allocation.Arrive:R}|{p.Allocation.End:R}"))),
            StationaryThermalRelief = reliefKnown ? Known(unchanged.Sum(t => t.AssessedColdDegreeTicks + t.AssessedHotDegreeTicks)
                - changedUse.Sum(t => t.AssessedColdDegreeTicks + t.AssessedHotDegreeTicks)) : default,
            CompletedDevelopmentSites = EquatableArray.From(plan.Allocations.Where(a => completed.GetValueOrDefault(a.Activity) == Known(true))
                .Select(a => context.Bindings.GetValueOrDefault((a.Activity, a.Pawn)))
                .Where(b => b is { Kind: PawnActivityKind.Work } && b.Job is "FinishFrame" or "BuildRoof")
                .Select(b => b!.Job == "BuildRoof" ? "roof/" + b.Location.X + "/" + b.Location.Z : "site/" + b.Target)
                .Distinct().Order(StringComparer.Ordinal))
        };
    }
}
