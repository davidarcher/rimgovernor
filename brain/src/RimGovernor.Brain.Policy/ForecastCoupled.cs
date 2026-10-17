using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Transient mechanics attached to tasks, never a second execution journal.</summary>
public sealed record ForecastNeedTask(string Task, string Pawn, string Kind, string Target,
    Resource Resource, int Count, Fact<double> Gain, EquatableArray<NeedMemory> Thoughts)
{
    public string FoodReady { get; init; } = "";
    public double ServiceDelay { get; init; }
    public Fact<double> FreshTicks { get; init; }
    /// <summary>Stack quantity the claims on this target sum against (<see cref="Count"/> per claim); unknown skips that bound.</summary>
    public Fact<double> AvailableCount { get; init; }
    /// <summary>The produced batch this claim eats from when the target is a cell several batches can be delivered to;
    /// empty when the target itself is the stack. Claims sum against <see cref="AvailableCount"/> per (target, batch).</summary>
    public string Batch { get; init; } = "";
    /// <summary>Simultaneous claimants the target admits (vanilla ReservationManager maxPawns). Unknown is read as 1 by the evaluator.</summary>
    public Fact<int> MaxPawns { get; init; }
    public string FreshFrom { get; init; } = "";
    public bool ThoughtsComplete { get; init; } = true;
}
public sealed record ForecastMaterialLot(string Lot, string Location, Resource Resource, Fact<double> Count)
{ public bool Perishable { get; init; } public Fact<double> FreshTicks { get; init; } }
/// <summary>
/// One planned site's generated work (#3276): the composed activities it was realized as (deliveries, clearing and its
/// finish) and the finish realizations among them. Stuff is a ring site's stuff, empty for any other site. The
/// activities have no action template until placement (#3278).
/// </summary>
public sealed record ForecastPlannedSite(string Site, int Room, string Stuff, EquatableArray<string> Activities, EquatableArray<string> Finishes);

/// <summary>
/// What one schedule completes of a planned room: the planned sites (ids) whose finish completes inside the horizon,
/// and the room alternatives all of whose sites do. A room with completed sites and no complete alternative reports
/// its later effect unassessed (planned_room_effect_unassessed), never as zero.
/// </summary>
public sealed record ForecastPlannedRoomOutcome(int Room, EquatableArray<string> CompletedSites, EquatableArray<string> Complete);

public sealed record ForecastCoupledContext
{
    public required ForecastActivityProblem Activities { get; init; }
    public bool Truncated { get; init; }
    public required NeedsForecastRead Needs { get; init; }
    public EquatableArray<ForecastMaintenanceRequirement> MaintenanceRequirements { get; init; }
    public EquatableArray<ForecastPracticeTask> PracticeTasks { get; init; }
    public required WorkResourceView Resources { get; init; }
    public ConstructionActivityRead Construction { get; init; } = new();
    public SupportActivityRead Support { get; init; } = new();
    public CoupledThermalRead? Thermal { get; init; }
    public EquatableArray<ForecastShelterPlan> Shelters { get; init; }
    /// <summary>The planned sites generated as construction activities (#3276), and the room alternatives they make up.</summary>
    public EquatableArray<ForecastPlannedSite> PlannedSites { get; init; }
    public EquatableArray<PlannedRoomAlternative> PlannedRooms { get; init; }
    public EquatableArray<ForecastMaterialLot> InitialLots { get; init; }
    public EquatableArray<ForecastNeedTask> NeedTasks { get; init; }
    /// <summary>Observed retained waiting occupies time but is not productive labor or a need service.</summary>
    public EquatableArray<string> NonproductiveOccupancy { get; init; }
    internal IReadOnlyDictionary<(string Task, string Pawn), Methods.ActivityTaskCandidate> Bindings { get; init; } = new Dictionary<(string, string), Methods.ActivityTaskCandidate>();
    public EquatableArray<string> Findings { get; init; }
    /// <summary>Per pawn, what the temperature cards are evaluated against (#3327); a pawn without a row keeps their thoughts constant.</summary>
    public IReadOnlyDictionary<string, CardPawnFacts> CardPawns { get; init; } = new Dictionary<string, CardPawnFacts>();
    /// <summary>Per pawn, the observed thought rows (MoodPawn.Thoughts) a temperature card's offset replaces (#3327).</summary>
    public IReadOnlyDictionary<string, Fact<EquatableArray<MoodThought>>> ObservedThoughts { get; init; } = new Dictionary<string, Fact<EquatableArray<MoodThought>>>();
    /// <summary>The mechanic cards; null is the shipped catalog. Only Live cards price.</summary>
    public IReadOnlyDictionary<string, MechanicCard>? Cards { get; init; }
}
public sealed record ForecastCoupledOutcome(ForecastActivityPlan Plan, Fact<bool> Feasible,
    EquatableArray<ForecastStockTrajectory> Stocks,
    IReadOnlyDictionary<string, ForecastNeedsResult> Needs,
    EquatableArray<ForecastFinding> Findings)
{
    public EquatableArray<ForecastShelterOutcome> Shelters { get; init; }
    public EquatableArray<ForecastMaintenanceConsequence> Maintenance { get; init; }
    public EquatableArray<ForecastThermalExposure> ThermalExposure { get; init; }
    public EquatableArray<string> CompletedDevelopmentSites { get; init; }
    /// <summary>Per planned room with a site completed inside the horizon: its completed sites and complete alternatives.</summary>
    public EquatableArray<ForecastPlannedRoomOutcome> PlannedRooms { get; init; }
    /// <summary>Known stationary-location exposure avoided relative to the same occupancy without proposed room changes.
    /// Unobserved transit/idle receive no benefit.</summary>
    public Fact<double> StationaryThermalRelief { get; init; }
    public string ThermalOccupancy { get; init; } = "";
    public ForecastSupportConsequences Support { get; init; } = new();
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
        foreach (var id in context.NonproductiveOccupancy)
            if (!all.TryGetValue(id, out var wait) || wait.Inputs.Count != 0 || wait.Outputs.Count != 0
                || wait.Location != wait.EndLocation || wait.NotBefore != 0 || wait.LatestStart != 0
                || context.NeedTasks.Any(n => n.Task == id) || context.Bindings.Keys.Any(k => k.Task == id))
                throw new DomainException("retained waiting cannot carry productive or need effects");
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
        // Necessary physical-lot conservation before expensive assignment search. Ignoring time,
        // expiry and reservations only overestimates supply; their authoritative checks remain below.
        foreach (var input in problem.Activities.SelectMany(t => t.Inputs).Where(i => i.Lot.Length != 0).GroupBy(i => (i.Lot, i.Resource)))
        {
            var output = problem.Activities.SelectMany(t => t.Outputs).Where(o => o.Lot == input.Key.Lot && o.Resource == input.Key.Resource).ToArray();
            var observed = context.InitialLots.Where(l => l.Lot == input.Key.Lot && l.Resource == input.Key.Resource).ToArray();
            if (input.Any(i => !i.Units.IsKnown) || output.Any(o => !o.Units.IsKnown) || observed.Any(l => !l.Count.IsKnown)) continue;
            if (input.Sum(i => i.Units.Value) > output.Sum(o => o.Units.Value) + observed.Sum(l => l.Count.Value))
                return ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
                { Coupled = new([], [.. findings, "physical_lot_quantity_shortfall:" + input.Key.Lot], false) };
        }
        var compared = included.Count == 0 ? null : CompareActivityPlans(problem);
        if (compared is not null)
        {
            findings.UnionWith(compared.Findings);
            if (compared.Alternatives.Length == 0) findings.Add("unassessed_no_schedule_within_horizon");
        }
        var alternatives = compared?.Alternatives ?? [new ForecastActivityPlan([], 0, 0)];
        var results = alternatives.Select(plan => EvaluateCoupledSchedule(request, commitments, problem, plan)).ToArray();
        // The assignment search is bounded and tries pawns in id order, so every plan it reaches can hand the same work to a pawn the
        // needs timeline then shows cannot do it. When that leaves nothing feasible, search again without those pawn choices.
        var retried = problem;
        for (var round = 0; round < 2 && compared is not null && results.All(r => r.Feasible != Known(true)); round++)
        {
            var barred = results.SelectMany(r => r.Findings.Where(f => f.Reason == "work_outside_recovered_capacity")
                .SelectMany(f => r.Plan.Allocations.Where(a => a.Activity == f.Commitment).Select(a => (a.Activity, a.Pawn)))).ToHashSet();
            var options = retried.Options.Where(o => !barred.Contains((o.Activity, o.Pawn))
                || !retried.Options.Any(other => other.Activity == o.Activity && !barred.Contains((other.Activity, other.Pawn)))).ToArray();
            if (options.Length == retried.Options.Length) break;
            retried = retried with { Options = options };
            var again = CompareActivityPlans(retried);
            findings.UnionWith(again.Findings);
            results = [.. results, .. again.Alternatives.Select(plan => EvaluateCoupledSchedule(request, commitments, retried, plan))];
        }
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
        if (plan.Allocations.GroupBy(a => a.Activity, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new DomainException("schedule allocates one activity more than once: " + twice.Key + " to " + string.Join(",", twice.Select(a => a.Pawn + "@" + a.Start)));
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
        // Hands owns exact targets for the whole Method, including future services.
        // Temporal non-overlap cannot authorize extra participants: a target admits at most MaxPawns distinct owners
        // (an unreadable MaxPawns is read as 1, this evaluator's policy), and its stack quantity bounds the summed claims.
        var observedTargets = context.Needs.Pawns.SelectMany(p => p.Targets).GroupBy(t => t.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().MaxPawns, StringComparer.Ordinal);
        var needClaims = context.NeedTasks.Where(n => allocations.ContainsKey(n.Task) && n.Target.Length != 0)
            .Select(n => (n.Target, n.Pawn, n.Count, n.MaxPawns, n.AvailableCount, Stack: n.Batch, n.Task)).ToArray();
        var needClaimed = needClaims.Select(c => (c.Task, c.Pawn)).ToHashSet();
        var boundClaims = plan.Allocations.Select(a => context.Bindings.GetValueOrDefault((a.Activity, a.Pawn)))
            .OfType<Methods.ActivityTaskCandidate>().Where(b => observedTargets.ContainsKey(b.Target) && !needClaimed.Contains((b.Id, b.Pawn)))
            .Select(b => (b.Target, b.Pawn, Count: 0, MaxPawns: observedTargets[b.Target], AvailableCount: default(Fact<double>), Stack: "", Task: b.Id));
        foreach (var target in needClaims.Concat(boundClaims).GroupBy(c => c.Target))
        {
            var capacity = target.Min(c => c.MaxPawns.TryGetValue(out var max) ? max : 1);
            if (target.Select(c => c.Pawn).Distinct().Count() > capacity)
                Refuse("conflicting_selected_target_owners", target.Key, 0);
            foreach (var stack in target.Where(c => c.Count > 0 && c.AvailableCount.IsKnown).GroupBy(c => c.Stack))
                if (stack.Sum(c => (double)c.Count) > stack.Min(c => c.AvailableCount.Value))
                    Refuse("target_stack_quantity_exceeded", target.Key, 0);
        }
        var supportChoices = plan.Allocations.Select(a => context.Bindings.GetValueOrDefault((a.Activity, a.Pawn))).OfType<Methods.ActivityTaskCandidate>()
            .Where(b => b.Kind == PawnActivityKind.Deliver && b.SupportDemand.Length != 0)
            .Select(b => context.Support.Deliveries.FirstOrDefault(d => d.Demand == b.SupportDemand && d.Target == b.Target))
            .Where(d => d is not null && d.Bill.Length != 0).Select(d => d!);
        foreach (var choice in supportChoices.GroupBy(d => d.Bill).Where(g => g.Select(d => d.Resource).Distinct().Count() > 1))
            Refuse("conflicting_bill_material_choices", choice.Key, 0);
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
        // A commitment's mood memories, situational changes and comfort shifts take effect when all its activities
        // have completed inside the horizon, or at the forecast start when it has none. Failed or later work changes
        // nothing here; unknown completion leaves those pawns' mood (or thermal exposure) unassessed.
        var granted = new Dictionary<string, List<(NeedMemory Thought, double Start)>>(StringComparer.Ordinal);
        var situational = new Dictionary<string, List<(string Thought, double OffsetDelta, double Start)>>(StringComparer.Ordinal);
        var comfort = new Dictionary<string, List<(double MinDelta, double MaxDelta, double Start)>>(StringComparer.Ordinal);
        var grantUnknown = new HashSet<string>(StringComparer.Ordinal);
        var comfortUnknown = new HashSet<string>(StringComparer.Ordinal);
        static void Add<T>(Dictionary<string, List<T>> rows, string pawn, T row)
        { if (!rows.TryGetValue(pawn, out var list)) rows[pawn] = list = []; list.Add(row); }
        foreach (var commitment in commitments.Values.Where(c => c.MoodMemories.Count + c.SituationalChanges.Count + c.ComfortShifts.Count > 0))
        {
            Fact<double> at = Known(0.0);
            foreach (var id in commitment.ActivityCompletions)
            {
                var done = completed.GetValueOrDefault(id);
                if (!allocations.TryGetValue(id, out var allocation) || allocation.End > horizon || done == Known(false))
                { at = Known(double.PositiveInfinity); break; }
                if (!done.IsKnown) { at = default; break; }
                at = Known(Math.Max(at.Value, allocation.End));
            }
            foreach (var row in commitment.MoodMemories)
                if (!at.IsKnown) grantUnknown.Add(row.Pawn);
                else if (at.Value <= horizon) Add(granted, row.Pawn, (row.Memory, at.Value));
            foreach (var row in commitment.SituationalChanges)
                if (!at.IsKnown) grantUnknown.Add(row.Pawn);
                else if (at.Value <= horizon) Add(situational, row.Pawn, (row.Thought, row.OffsetDelta, at.Value));
            foreach (var row in commitment.ComfortShifts)
                if (!at.IsKnown) comfortUnknown.Add(row.Pawn);
                else if (at.Value <= horizon) Add(comfort, row.Pawn, (row.MinDelta, row.MaxDelta, at.Value));
        }
        var shelters = ForecastShelters(context.Shelters, plan with
        { Allocations = plan.Allocations.Where(a => completed.GetValueOrDefault(a.Activity) == Known(true)).ToArray() }, horizon);
        // Comfort shifts change the pawn's own exposure, not what a shelter relieves (relief below is outfit-neutral).
        var exposure = EquatableArray.From(ForecastThermalExposureOf(context, problem, plan, shelters, horizon, comfort: comfort)
            .Select(t => comfortUnknown.Contains(t.Pawn) ? new ForecastThermalExposure(t.Pawn, 0, horizon, 0, 0) : t));
        // Temperature mood (#3327): each Live temperature card along the pawn's assessed exposure spans, comfort shifts included.
        var temperatureCards = (context.Cards ?? MechanicCards.ByDef).Values.Where(TemperatureCard).OrderBy(c => c.Def, StringComparer.Ordinal).ToArray();
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
                    a.Depart, Math.Min(a.End, a.Start + task.ServiceDelay), a.End, gain, task.Thoughts));
            }
            var occupiedWork = plan.Allocations.Where(a => a.Pawn == pawn.Pawn && context.NeedTasks.All(n => n.Task != a.Activity)).ToArray();
            var productive = occupiedWork.Where(a => !context.NonproductiveOccupancy.Contains(a.Activity)).ToArray();
            var protectedTime = occupiedWork.Select(a => new ForecastOccupancy(a.Activity, pawn.Pawn, Known(a.Depart), Known(a.End), ForecastOccupancyState.Active)).ToList();
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
            var spans = exposure.Single(t => t.Pawn == pawn.Pawn).Spans;
            var projected = ForecastNeedsTimeline(pawn, input, choices, granted: granted.GetValueOrDefault(pawn.Pawn),
                situational: situational.GetValueOrDefault(pawn.Pawn),
                banded: temperatureCards.Select(card => TemperatureThought(card, context.CardPawns.GetValueOrDefault(pawn.Pawn),
                    context.ObservedThoughts.GetValueOrDefault(pawn.Pawn), spans)).ToArray());
            var mealsUnknown = context.NeedTasks.Any(n => n.Pawn == pawn.Pawn && !n.ThoughtsComplete && allocations.ContainsKey(n.Task));
            if (mealsUnknown || grantUnknown.Contains(pawn.Pawn))
                projected = projected with
                {
                    ThoughtOffsetAtHorizon = default,
                    MemoryAdjustedMoodTarget = default,
                    Mood = null,
                    Findings = [.. projected.Findings, mealsUnknown ? "future_meal_thoughts_unassessed" : "commitment_mood_memory_unassessed"]
                };
            needs[pawn.Pawn] = projected;
        }
        // Removing impossible work may invalidate its meals and therefore later work. Each pass removes at least one task.
        if (rejectedWork.Count > unavailableWork.Count)
            return EvaluateCoupledSchedule(request, commitments, problem, plan, rejectedWork);
        // Ordinary production needs no training mandate. Only a claimed learning-dependent later
        // speedup requires evidence that its prerequisite practice produced useful food in this slice.
        // A terminal native batch can retain remainder; XP itself never improves the consequence vector.
        foreach (var allocation in plan.Allocations.Where(a => tasks[a.Activity].RequiresFoodUse && a.Learning is { } prior
            && prior.After.AppliedGain.IsKnown && prior.Before.AppliedGain.IsKnown
            && prior.After.AppliedGain.Value > prior.Before.AppliedGain.Value
            && plan.Allocations.Any(later => later.Pawn == a.Pawn && later.Start >= a.End && later.Learning is { } learned
                && learned.WorkTicks < learned.ObservedWorkTicks)))
            if (!context.NeedTasks.Any(n => n.Kind == "food" && n.FreshFrom == allocation.Activity
                && needs.TryGetValue(n.Pawn, out var result) && result.UsefulFoodGain.GetValueOrDefault(n.Task) > 0))
                Refuse("productive_output_without_useful_demand", allocation.Activity, allocation.Start);
        var accounting = new Dictionary<string, ForecastCommitment>(commitments, StringComparer.Ordinal)
        { ["coupled/timed-effects"] = new("coupled/timed-effects", EquatableArray.From(effects)) };
        var inventory = ForecastInventory(request.Facts.Stock, accounting, request.HorizonDays);
        findings.AddRange(inventory.Findings);
        var existingRooms = EquatableArray.From(context.Shelters.Where(r => r.Prerequisites.Count == 0 && r.AlternativePrerequisites.Count == 0));
        var use = plan with { Allocations = plan.Allocations.Where(a => context.NeedTasks.Any(n => n.Task == a.Activity)).ToArray() };
        var changedUse = ForecastThermalExposureOf(context, problem, use, shelters, horizon, plan);
        var unchanged = ForecastThermalExposureOf(context with { Shelters = existingRooms }, problem, use,
            ForecastShelters(existingRooms, plan, horizon), horizon);
        var reliefKnown = changedUse.Any(t => t.AssessedTicks > 0) && changedUse.Count == unchanged.Count && changedUse.Zip(unchanged)
            .All(pair => pair.First.Pawn == pair.Second.Pawn && pair.First.AssessedTicks == pair.Second.AssessedTicks);
        if (!reliefKnown && shelters.Count != 0)
            Finding("thermal_relief_unassessed:changed=" + string.Join(",", changedUse.Select(t => t.Pawn + "/" + Math.Round(t.AssessedTicks))) + ";unchanged="
                + string.Join(",", unchanged.Select(t => t.Pawn + "/" + Math.Round(t.AssessedTicks))) + ";shelters="
                + string.Join(",", shelters.Select(s => (s.CompleteAt.IsKnown ? "c" : "-") + (s.TemperatureAtHorizon.IsKnown ? "t" : "-") + ":" + string.Join("|", s.Findings))), "thermal", 0);
        var maintenance = ForecastMaintenance(context.MaintenanceRequirements, context.PracticeTasks, plan, completed, horizon);
        foreach (var group in context.PracticeTasks.Where(p => allocations.ContainsKey(p.Task)).GroupBy(p => p.Requirement))
        {
            var without = ForecastMaintenance(context.MaintenanceRequirements,
                EquatableArray.From(context.PracticeTasks.Where(p => p.Requirement != group.Key)), plan, completed, horizon);
            if (!maintenance.Zip(without).Any(p => p.First.MissingXp.IsKnown && p.Second.MissingXp.IsKnown
                && p.First.MissingXp.Value < p.Second.MissingXp.Value))
                Refuse("practice_without_assigned_shortfall_improvement", group.Key, 0);
        }
        // A planned site counts once it finishes inside the horizon; a room only when a whole alternative does (#3276).
        var builtSites = context.PlannedSites.Where(s => s.Finishes.Any(f => completed.GetValueOrDefault(f) == Known(true)))
            .Select(s => s.Site).ToHashSet(StringComparer.Ordinal);
        var plannedRooms = context.PlannedSites.Where(s => builtSites.Contains(s.Site)).GroupBy(s => s.Room).OrderBy(g => g.Key)
            .Select(g => new ForecastPlannedRoomOutcome(g.Key, EquatableArray.From(g.Select(s => s.Site).Order(StringComparer.Ordinal)),
                EquatableArray.From(context.PlannedRooms.Where(a => a.Room == g.Key && a.Sites.All(builtSites.Contains)).Select(a => a.Id))))
            .ToArray();
        foreach (var room in plannedRooms.Where(r => r.Complete.Count == 0))
            Finding("planned_room_effect_unassessed", "planned/room" + room.Room, horizon);
        var thermalRelief = reliefKnown ? Known(unchanged.Sum(t => t.AssessedColdDegreeTicks + t.AssessedHotDegreeTicks)
            - changedUse.Sum(t => t.AssessedColdDegreeTicks + t.AssessedHotDegreeTicks)) : default;
        return new(plan, feasible, inventory.Stocks, needs, EquatableArray.From(findings))
        {
            Support = ForecastSupportConsequencesOf(context, plan, completed, horizon),
            Maintenance = maintenance,
            Shelters = shelters, ThermalExposure = exposure,
            ThermalOccupancy = string.Join(";", context.NeedTasks.Where(n => allocations.ContainsKey(n.Task))
                .Select(n => (Need: n, Allocation: allocations[n.Task]))
                .OrderBy(p => p.Need.Pawn, StringComparer.Ordinal).ThenBy(p => p.Allocation.Arrive).ThenBy(p => p.Need.Kind, StringComparer.Ordinal)
                .Select(p => FormattableString.Invariant($"{p.Need.Pawn}|{p.Need.Kind}|{tasks[p.Need.Task].Location}|{p.Allocation.Arrive:R}|{p.Allocation.End:R}"))),
            StationaryThermalRelief = thermalRelief,
            PlannedRooms = EquatableArray.From(plannedRooms),
            CompletedDevelopmentSites = EquatableArray.From(plan.Allocations.Where(a => completed.GetValueOrDefault(a.Activity) == Known(true))
                .Select(a => context.Bindings.GetValueOrDefault((a.Activity, a.Pawn)))
                .Where(b => b is { Kind: PawnActivityKind.Work } && b.Job is "FinishFrame" or "BuildRoof")
                .Select(b => b!.Job == "BuildRoof" ? "roof/" + b.Location.X + "/" + b.Location.Z : "site/" + b.Target)
                .Distinct().Order(StringComparer.Ordinal))
        };
    }
}
