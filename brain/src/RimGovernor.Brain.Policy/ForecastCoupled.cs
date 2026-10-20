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
/// activities have no action template until placement (#3278); a Deconstruct clearing, bound to its standing building, is
/// startable work and is left out (#3557).
/// </summary>
public sealed record ForecastPlannedSite(string Site, int Room, string Stuff, EquatableArray<string> Activities, EquatableArray<string> Finishes)
{
    /// <summary>The Need of a planned site root's outcome, which marks the root as one: the selection exempts its closure
    /// from the thermal-relief gate (#3651). The units are counted from the evaluated outcome (ForecastCoupledOutcome.RequestedDevelopment).</summary>
    public const string DevelopmentNeed = "requested-development";

    /// <summary>
    /// The planned room roles that carry a priced value of their own (#3651, #3647 decision 3): their completed units are
    /// still emitted and exempt, but no longer count as development in the ranking's late tiebreak, and their deliveries are
    /// charged the material they consume (ForecastValue.ChargedMaterialActivities, #3653). Empty until a role is
    /// priced; each priced role is added here (Bedroom, Barracks, then the rest in #3650).
    /// </summary>
    public static readonly IReadOnlySet<PlannedRole> PricedRoomRoles = new HashSet<PlannedRole>();
}

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
    /// <summary>Each planned room's role, from the layout plan (#3651); a room without a row counts as development.</summary>
    public IReadOnlyDictionary<int, PlannedRole> PlannedRoomRoles { get; init; } = new Dictionary<int, PlannedRole>();
    /// <summary>The roles whose units the development count skips (#3651); the shipped set by default, tests inject one.</summary>
    public IReadOnlySet<PlannedRole> PricedRoomRoles { get; init; } = ForecastPlannedSite.PricedRoomRoles;
    /// <summary>The closed-room predictions of the planned rooms (#3339): what each room is once its ring of one stuff stands.</summary>
    public EquatableArray<ForecastPlannedClosure> Closures { get; init; }
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
    /// <summary>The player's unmet goals as the ledger values them (#3503); none without goals.</summary>
    public EquatableArray<ForecastGoalValue> GoalValues { get; init; }
    /// <summary>The capability-days currency's constants (#3520), under the player's risk profile and pace (#3503); tests inject them.</summary>
    public ForecastValuePolicy Value { get; init; } = new();
    /// <summary>The game's malnutrition and food-poisoning tables the ledger prices capacity loss with (#3519); unknown by default.</summary>
    public CapacityPenaltyTables CapacityPenalties { get; init; } = new();
    /// <summary>The expected raids the harm term prices (#3546): schedule, composition, exposure table, defense and where a hit lands; unknown by default.</summary>
    public HarmInputs Harm { get; init; } = new();
    /// <summary>The outfit (apparel and primary weapon) each pawn wears now (#3546), from the gear census's worn items; a pawn without a row has an unobserved outfit.</summary>
    public IReadOnlyDictionary<string, EquatableArray<GearOption>> Outfits { get; init; } = new Dictionary<string, EquatableArray<GearOption>>();
    /// <summary>Capability-days a unit of progress adds on each project tier 2 valued (#3531), by <see cref="ForecastProjectProgress.Project"/>: per research point, per cell sown. Empty before tier 2 ran.</summary>
    public IReadOnlyDictionary<string, double> Investment { get; init; } = new Dictionary<string, double>();
    /// <summary>Per construction material a priced planned room consumes, its value per unit (#3653), priced once per review
    /// (ForecastValue.MaterialValueOf). A material without a row is unpriced and charged nothing.</summary>
    public IReadOnlyDictionary<Resource, ConstructionMaterialValue> MaterialValues { get; init; } = new Dictionary<Resource, ConstructionMaterialValue>();
    /// <summary>Per cookable ingredient, the nutrition a unit gives cooked over what it gives raw (#3626): the best observed
    /// conversion's product Nutrition stat times its count over the ingredient count, divided by the ingredient's own Nutrition
    /// stat. Only ratios above 1 are kept; an ingredient without one costs nothing eaten raw.</summary>
    public IReadOnlyDictionary<Resource, double> CookedNutritionRatio { get; init; } = new Dictionary<Resource, double>();
    /// <summary>The catalog's break curve whose tier mixes price a break in days lost (#3520); unknown by default.</summary>
    public MoodBreakModel MoodBreaks { get; init; } = new();
    /// <summary>Diagnostic only: keep each needs result's per-point mood. The search never reads it (#3467).</summary>
    public bool MoodTrajectory { get; init; }
}
public sealed record ForecastCoupledOutcome(ForecastActivityPlan Plan, Fact<bool> Feasible,
    IReadOnlyDictionary<string, ForecastNeedsResult> Needs,
    EquatableArray<ForecastFinding> Findings)
{
    public EquatableArray<ForecastShelterOutcome> Shelters { get; init; }
    public EquatableArray<ForecastMaintenanceConsequence> Maintenance { get; init; }
    public EquatableArray<ForecastThermalExposure> ThermalExposure { get; init; }
    public EquatableArray<string> CompletedDevelopmentSites { get; init; }
    /// <summary>Per planned room with a site completed inside the horizon: its completed sites and complete alternatives.</summary>
    public EquatableArray<ForecastPlannedRoomOutcome> PlannedRooms { get; init; }
    /// <summary>The planned development units completed inside the horizon (#3334): each planned site once, and a planned
    /// room's per-cell roof as its <c>planned/room{R}/roof</c> unit. One unit each of requested development, except a unit
    /// of a room whose role is priced (ForecastCoupledContext.PricedRoomRoles, #3651), which the ranking does not count.</summary>
    public EquatableArray<string> RequestedDevelopment { get; init; }
    /// <summary>Known stationary-location exposure avoided relative to the same occupancy without proposed room changes.
    /// Unobserved transit/idle receive no benefit.</summary>
    public Fact<double> StationaryThermalRelief { get; init; }
    public string ThermalOccupancy { get; init; } = "";
    public ForecastSupportConsequences Support { get; init; } = new();
    /// <summary>When each commitment with activities and outfit changes took effect (#3621); the harm term prices its outfit from then.</summary>
    public EquatableArray<ForecastEffectiveTick> OutfitTicks { get; init; }

}
public sealed record ForecastCoupledScenario(EquatableArray<ForecastCoupledOutcome> Alternatives,
    EquatableArray<string> Findings, bool Truncated)
{ public int Expansions { get; init; } }

public static partial class ForecastCoupled
{
    /// <summary>
    /// The tier-1 horizon: the exact near-term schedule normal Rounds forecast over (at most one game day). A gear bill
    /// whose stuff arrives later is not offered in tier 1 (<see cref="StuffArrival"/>).
    /// </summary>
    public const double TierOneHorizonDays = 1.0;

    /// <summary>Composition boundary used by normal Rounds facts: one observed frame, stock owner and horizon.</summary>
    public static ColonyForecastRequest CoupledForecastRequestOf(ColonyForecastOrigin origin, double horizonDays,
        RoundsFacts facts, DerivedDemand demand, ForecastActivityProblem activities,
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
            Facts = ColonyForecastFactsRules.ColonyForecastFactsOf(facts),
            Commitments = commitments,
            Proposed = proposed,
            Coupled = new() { Activities = activities, Needs = needs, Resources = resources, NeedTasks = needTasks }
        };
    }

    public static ColonyForecastRequest CoupledForecastRequestOf(ColonyForecastOrigin origin, double horizonDays,
        RoundsFacts facts, DerivedDemand demand, EquatableArray<ForecastCommitment> commitments,
        ColonyForecastDelta proposed)
    {
        var generated = facts.CoupledAlternatives ?? throw new DomainException("coupled observed review unavailable");
        return CoupledForecastRequestOf(origin, horizonDays, facts, demand, generated.Context.Activities,
            generated.Context.NeedTasks, commitments, proposed) with
        { Coupled = generated.Context };
    }

    /// <summary>One portfolio's schedule search and evaluation. A search passes its one <paramref name="cache"/> (#3468), so a
    /// pawn's timelines and the context's indexes are shared by every portfolio; <paramref name="expansionBudget"/> bounds this
    /// search's schedule search without copying the context.</summary>
    internal static ColonyForecastScenario ForecastCoupledScenarioOf(ColonyForecastRequest request,
        IReadOnlyDictionary<string, ForecastCommitment> commitments, CoupledSearchCache? cache = null, int? expansionBudget = null)
    {
        var context = request.Coupled!;
        cache ??= new();
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
            return ColonyForecastRules.ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
            { Coupled = new([], ["exclusive_outcome_conflict"], false) };
        var problem = context.Activities with
        {
            Activities = all.Values.Where(t => included.Contains(t.Id)).ToArray(),
            Options = context.Activities.Options.Where(o => included.Contains(o.Activity)).ToArray(),
            Deadline = Known(horizon),
            ExpansionBudget = expansionBudget ?? context.Activities.ExpansionBudget
        };
        if (problem.Activities.Where(t => t.ChoiceIdentity.Length != 0).GroupBy(t => t.ChoiceIdentity).Any(g => g.Count() > 1))
            return ColonyForecastRules.ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
            { Coupled = new([], ["exclusive_operation_conflict"], false) };
        if (included.Count > 32) return ColonyForecastRules.ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
        { Coupled = new([], [.. findings, "selected_activity_budget_truncated"], true) };
        // Necessary physical-lot conservation before expensive assignment search. Ignoring time,
        // expiry and reservations only overestimates supply; their authoritative checks remain below.
        foreach (var input in problem.Activities.SelectMany(t => t.Inputs).Where(i => i.Lot.Length != 0).GroupBy(i => (i.Lot, i.Resource)))
        {
            var output = problem.Activities.SelectMany(t => t.Outputs).Where(o => o.Lot == input.Key.Lot && o.Resource == input.Key.Resource).ToArray();
            var observed = context.InitialLots.Where(l => l.Lot == input.Key.Lot && l.Resource == input.Key.Resource).ToArray();
            if (input.Any(i => !i.Units.IsKnown) || output.Any(o => !o.Units.IsKnown) || observed.Any(l => !l.Count.IsKnown)) continue;
            if (input.Sum(i => i.Units.Value) > output.Sum(o => o.Units.Value) + observed.Sum(l => l.Count.Value))
                return ColonyForecastRules.ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
                { Coupled = new([], [.. findings, "physical_lot_quantity_shortfall:" + input.Key.Lot], false) };
        }
        var compared = included.Count == 0 ? null : ForecastActivities.CompareActivityPlans(problem);
        if (compared is not null)
        {
            findings.UnionWith(compared.Findings);
            if (compared.Alternatives.Length == 0) findings.Add("unassessed_no_schedule_within_horizon");
        }
        var alternatives = compared?.Alternatives ?? [new ForecastActivityPlan([], 0, 0)];
        var results = alternatives.Select(plan => cache.Evaluate(request, commitments, problem, plan)).ToArray();
        // The assignment search is bounded and tries the cheapest pawn first, so every plan it reaches can hand the same work to a pawn the
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
            var again = ForecastActivities.CompareActivityPlans(retried);
            findings.UnionWith(again.Findings);
            results = [.. results, .. again.Alternatives.Select(plan => cache.Evaluate(request, commitments, retried, plan))];
        }
        // Inventory-only clients keep their conditional diagnostics; migrated consumers inspect each coupled alternative.
        return ColonyForecastRules.ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
        {
            MaterialAvailability = context.Resources.Materials,
            Coupled = new(EquatableArray.From(results), EquatableArray.From(findings), context.Truncated || (compared?.SearchTruncated ?? false)) { Expansions = compared?.Expansions ?? 0 },
        };
    }
}
