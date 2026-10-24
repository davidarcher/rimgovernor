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

    /// <summary>Planned room <paramref name="room"/>'s per-cell native roof as one completed unit (#3334).</summary>
    public static string RoofUnit(int room) => FormattableString.Invariant($"planned/room{room}/roof");
}

/// <summary>
/// What one schedule completes of a planned room: the planned sites (ids) whose finish completes inside the horizon,
/// and the room alternatives all of whose sites do. A room with completed sites and no complete alternative reports
/// its later effect unassessed (planned_room_effect_unassessed), never as zero, unless its service day is projected
/// (PlannedRoomCredits.ServiceDays, #3796), which assesses it.
/// </summary>
public sealed record ForecastPlannedRoomOutcome(int Room, EquatableArray<string> CompletedSites, EquatableArray<string> Complete);

public sealed record ForecastCoupledContext
{
    public required ForecastActivityProblem Activities { get; init; }
    public ForecastModelBasis? ModelBasis { get; init; }
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
    /// <summary>The credit of each priced room role (#3796): a role is priced when it has one (PlannedRoomCredits). The shipped
    /// credits by default; tests inject one.</summary>
    public IReadOnlyDictionary<PlannedRole, RoomCredit> RoomCredits { get; init; } = PlannedRoomCredits.Shipped;
    /// <summary>The closed-room predictions of the planned rooms (#3339): what each room is once its ring of one stuff stands.</summary>
    public EquatableArray<ForecastPlannedClosure> Closures { get; init; }
    /// <summary>The builders a room's owed work is projected over (#3744, #3796): the free colonists of the needs census
    /// capable of Construction (LayoutClusterRules.BuildersOf). None (none free, or their work facts unread) projects nothing.</summary>
    public int PlannedBuilders { get; init; }
    /// <summary>The owed work in ticks (#3744, #3796) of each planned site (by site id: the least finish, FinishTicks or a
    /// roof's RoofCellWorkTicks per cell, plus the least delivery haul and clearing it was generated with) and of each placed
    /// site and standing removal (by its id: the least work of its finishes). A room owing an item without a row is not projected.</summary>
    public IReadOnlyDictionary<string, double> OwedWork { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);
    /// <summary>The placed sites and standing removals the planned rooms hold (#3796, epic #3650 P4), by the rooms whose
    /// interior or ring cells hold them: part of those rooms' owed work.</summary>
    public EquatableArray<ForecastRoomItem> RoomItems { get; init; }
    /// <summary>The connectors among the planned rooms (#3815, epic #3650 P4): each circulation corridor or airlock, by room, and
    /// the rooms it reaches or seals (PlannedRoomCredits.ConnectorsOf). Its owed work is part of each of those rooms'.</summary>
    public IReadOnlyDictionary<int, EquatableArray<int>> Connectors { get; init; } = new Dictionary<int, EquatableArray<int>>();
    /// <summary>The open planned rooms (#3744, #3754): observed started (RoundsBuildingPlanner.PlannedRoomStartedOf) and not
    /// done (PlannedRules.PlannedRoomDone, as their latest reconcile found). Their owed work is done first in the service-day
    /// projection (#3796). Set by the round beside <see cref="InvestmentHorizonDays"/>; none by default.</summary>
    public IReadOnlySet<int> OpenRooms { get; init; } = new HashSet<int>();
    /// <summary>The owed placed sites that are blocked pieces (#3827): the one blocked predicate of PlannedRules.PlannedRoomDone
    /// (RoundsBuildingPlanner.DefinitionBlockedOf, read evidence only, #3819), resolved by the round beside <see cref="OpenRooms"/>.
    /// A blocked item does not delay its room's service day (PlannedRoomCredits.ServiceDays); none by default. A planned site of
    /// a blocked piece is never generated (PlannedBuildSite.Blocked, #3838).</summary>
    public IReadOnlySet<string> BlockedItems { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Per planned bedroom, the colonists the bedroom step would move into it (SleepingRules.PlannedBedroomOccupants,
    /// #3655, #3656: one, or a couple), by pawn id; a bedroom without a row houses nobody and earns no credit.</summary>
    public IReadOnlyDictionary<int, EquatableArray<string>> BedroomOccupants { get; init; } = new Dictionary<int, EquatableArray<string>>();
    /// <summary>Per planned storage room, yard and meal closet, the stock it would keep (RoundsFacts.PlannedStoreStock, #3798);
    /// null when the stockpile review did not read it, which leaves those rooms' credit unassessed.</summary>
    public IReadOnlyDictionary<int, PlannedStock>? StoreStock { get; init; }
    /// <summary>Per planned throne room or suite, the colonist it would serve (titled for a throne room; any suite occupant,
    /// #3826) and its title's requirement of that room (#3807, RoundsSleepingUpkeepPlanner.PlannedOccupantsOf); a room without a
    /// row serves nobody and earns no credit.</summary>
    public IReadOnlyDictionary<int, RoyalRoomNeed> RoyalRooms { get; init; } = new Dictionary<int, RoyalRoomNeed>();
    /// <summary>The layout plan's rooms by id (RoundsFacts.PlannedShelters, #3800): what a planned room's predicted
    /// cleanliness reads beside its closure prediction (PlannedRoomCleanlinessRules.Steady); the kitchen and butchery credits
    /// read their stations' meals and filth runs (#3802).</summary>
    public IReadOnlyDictionary<int, PlannedRoom> LayoutRooms { get; init; } = new Dictionary<int, PlannedRoom>();
    /// <summary>The catalog's filth model (RoundsFacts.Filth) a planned room's predicted cleanliness and its poisoning chance read (#3797, #3800, #3802).</summary>
    public FilthModel Filth { get; init; } = new();
    /// <summary>Per research bench def, its ResearchSpeedFactor from the def data (RoundsFacts.ResearchBenchSpeeds, #3800).</summary>
    public IReadOnlyDictionary<string, BenchSpeedModel> ResearchBenchSpeeds { get; init; } = new Dictionary<string, BenchSpeedModel>();
    /// <summary>The worship room's believers, presence demands and rituals (RoundsFacts.Worship, #3808).</summary>
    public WorshipFacts Worship { get; init; } = new();
    /// <summary>The planned pens by room id, each its herd's key (RoundsFacts.PlannedPens, #3812).</summary>
    public IReadOnlyDictionary<int, string> PenHerds { get; init; } = new Dictionary<int, string>();
    /// <summary>The player's roamers (AnimalUpkeepObservation.Roamers, #3812); null when native could not read the animal census,
    /// which a served pen's credit raises as a required fact.</summary>
    public IReadOnlyList<PenRoamer>? Roamers { get; init; }
    /// <summary>The bench a planned laboratory is furnished with (RoundsFacts.LaboratoryBench, #3800); "" none.</summary>
    public string LaboratoryBench { get; init; } = "";
    /// <summary>The planned deathrest chamber's caskets and other deathrest buildings as the staging resolves them (#3810,
    /// RoundsSleepingUpkeepPlanner.DeathrestFurnitureOf); none while no deathrester owes a chamber. Set by the round beside
    /// <see cref="RoyalRooms"/>.</summary>
    public EquatableArray<DeathrestPiece> DeathrestFurniture { get; init; }
    /// <summary>The benches the colony cooks at now (the coupled food read's conversion benches, #3802): the kitchen credit's
    /// current room is theirs (SupportDemand.FoodPoisonChance of their bench demands).</summary>
    public EquatableArray<string> CookingBenches { get; init; }
    /// <summary>The days a served room's credit runs to (#3655, #3796): the horizon tier 2 ran over (ColonyModelPolicy.HorizonDays),
    /// set beside <see cref="Investment"/>; 0 before tier 2 ran, which credits nothing.</summary>
    public double InvestmentHorizonDays { get; init; }
    public EquatableArray<ForecastMaterialLot> InitialLots { get; init; }
    public EquatableArray<CoupledFoodUse> FoodUses { get; init; }
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
    /// <summary>Per pawn, the apparel mood shifts of the garments the alternatives overlay on its outfit (ForecastApparel.BreakShifts,
    /// #3816), which its mood timeline integrates breaks at; none by default.</summary>
    public IReadOnlyDictionary<string, double[]> ApparelShifts { get; init; } = new Dictionary<string, double[]>(StringComparer.Ordinal);
    /// <summary>Capability-days a unit of progress adds on each project tier 2 valued (#3531), by <see cref="ForecastProjectProgress.Project"/>: per research point, per cell sown. Empty before tier 2 ran.</summary>
    public IReadOnlyDictionary<string, double> Investment { get; init; } = new Dictionary<string, double>();
    /// <summary>Per construction material a priced planned room consumes and per stock def a planned store keeps (#3798), its
    /// value per unit (#3653), priced once per review (ForecastValue.MaterialValueOf, over ForecastValue.ChargedMaterials). A
    /// material without a row is unpriced and charged nothing; a kept stack without one leaves its store's credit unassessed.</summary>
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
    public ForecastTerminalAccounting? Terminal { get; init; }
    public EquatableArray<ForecastShelterOutcome> Shelters { get; init; }
    public EquatableArray<ForecastMaintenanceConsequence> Maintenance { get; init; }
    public EquatableArray<ForecastThermalExposure> ThermalExposure { get; init; }
    public EquatableArray<string> CompletedDevelopmentSites { get; init; }
    /// <summary>Per planned room with a site completed inside the horizon: its completed sites and complete alternatives.</summary>
    public EquatableArray<ForecastPlannedRoomOutcome> PlannedRooms { get; init; }
    /// <summary>The planned rooms the schedule serves (PlannedRoomCredits.ServiceDays, #3796): inside the horizon once it
    /// finishes their owed work, past it at their projected service day.</summary>
    public EquatableArray<ForecastRoomService> ServedRooms { get; init; }
    /// <summary>The planned rooms the schedule touches whose service day is unassessed (no builders, or owed work without a row).</summary>
    public EquatableArray<int> UnassessedRooms { get; init; }
    /// <summary>The planned development units completed inside the horizon (#3334): each planned site once, and a planned
    /// room's per-cell roof as its <see cref="ForecastPlannedSite.RoofUnit"/>. One unit each of requested development, except a
    /// planned room's unit (PlannedRoomCredits.PlannedUnits, #3828), which the ranking does not count: a room wins on the ledger only.</summary>
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
    EquatableArray<string> Findings, bool Truncated);

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
    /// pawn's timelines and the context's indexes are shared by every portfolio, and its <paramref name="ledger"/> (#3722): every
    /// schedule search, the capacity-recovery retries included, runs within <paramref name="bound"/> (the context's when null) and
    /// the traversal left, and every plan's outcome evaluation draws one unit before it starts. When an allowance ends, the plans
    /// left are not evaluated, no retry starts, and the scenario is truncated with the spent limit's finding. Without a ledger
    /// nothing but <paramref name="bound"/> limits the search.</summary>
    internal static ColonyForecastScenario ForecastCoupledScenarioOf(ColonyForecastRequest request,
        IReadOnlyDictionary<string, ForecastCommitment> commitments, CoupledSearchCache? cache = null, ForecastSearchLedger? ledger = null, int? bound = null)
    {
        var context = request.Coupled!;
        cache ??= new();
        if (commitments.Values.Any(c => c.ActivityCompletions.Count != 0 && c.StockChanges.Count != 0))
            throw new DomainException("coupled commitments have one timed effect source");
        var horizon = request.HorizonDays * GameTime.TicksPerDay;
        if (horizon > GameTime.TicksPerDay || context.Needs.Tick != request.Origin.ObservedTick
            || context.Resources.Evidence.Tick.Value != request.Origin.ObservedTick)
            throw new DomainException("coupled forecast needs one observed tick and at most one day");
        var all = cache.Tasks(context.Activities);
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
        var perSearch = bound ?? context.Activities.ExpansionBudget;
        var problem = context.Activities with
        {
            Activities = all.Values.Where(t => included.Contains(t.Id)).ToArray(),
            Options = context.Activities.Options.Where(o => included.Contains(o.Activity)).ToArray(),
            Deadline = Known(horizon),
            ExpansionBudget = perSearch
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
        // Every schedule search and every outcome evaluation of this scenario, the retries' included, is charged to the ledger (#3722).
        var truncated = context.Truncated; var exhausted = false;
        var results = new List<ForecastCoupledOutcome>();
        ForecastActivityComparison? Compare(ForecastActivityProblem p)
        {
            var limit = ledger?.SchedulerBound(perSearch) ?? perSearch;
            if (limit <= 0) { findings.Add(ForecastCoupledSelection.TraversalBoundExhausted); truncated = exhausted = true; return null; }
            var compared = ForecastActivities.CompareActivityPlans(p with { ExpansionBudget = limit }, cache.Routes);
            ledger?.SpendExpansions(compared.Expansions);
            findings.UnionWith(compared.Findings);
            truncated |= compared.SearchTruncated;
            if (compared.SearchTruncated && ledger is { Mandatory: false, TraversalExhausted: true }) { findings.Add(ForecastCoupledSelection.TraversalBoundExhausted); exhausted = true; }
            return compared;
        }
        void Evaluate(ForecastActivityProblem p, IEnumerable<ForecastActivityPlan> plans)
        {
            foreach (var plan in plans)
            {
                if (ledger is not null && !ledger.TryStartEvaluation()) { findings.Add(ForecastCoupledSelection.OutcomeEvaluationsExhausted); truncated = exhausted = true; return; }
                results.Add(cache.Evaluate(request, commitments, p, plan));
            }
        }
        if (included.Count == 0) Evaluate(problem, [new ForecastActivityPlan([], 0, 0)]);
        else if (Compare(problem) is { } compared)
        {
            if (compared.Alternatives.Length == 0) findings.Add("unassessed_no_schedule_within_horizon");
            Evaluate(problem, compared.Alternatives);
            // The assignment search is bounded and tries the cheapest pawn first, so every plan it reaches can hand the same work to a pawn the
            // needs timeline then shows cannot do it. When that leaves nothing feasible, search again without those pawn choices.
            var retried = problem;
            for (var round = 0; round < 2 && !exhausted && results.All(r => r.Feasible != Known(true)); round++)
            {
                var barred = results.SelectMany(r => r.Findings.Where(f => f.Reason == "work_outside_recovered_capacity")
                    .SelectMany(f => r.Plan.Allocations.Where(a => a.Activity == f.Commitment).Select(a => (a.Activity, a.Pawn)))).ToHashSet();
                var options = retried.Options.Where(o => !barred.Contains((o.Activity, o.Pawn))
                    || !retried.Options.Any(other => other.Activity == o.Activity && !barred.Contains((other.Activity, other.Pawn)))).ToArray();
                if (options.Length == retried.Options.Length) break;
                if (ledger is { Mandatory: false, EvaluationsExhausted: true }) { findings.Add(ForecastCoupledSelection.OutcomeEvaluationsExhausted); truncated = exhausted = true; break; }
                retried = retried with { Options = options };
                if (Compare(retried) is { } again) Evaluate(retried, again.Alternatives);
            }
        }
        // Inventory-only clients keep their conditional diagnostics; migrated consumers inspect each coupled alternative.
        return ColonyForecastRules.ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays) with
        {
            MaterialAvailability = context.Resources.Materials,
            Coupled = new(EquatableArray.From(results), EquatableArray.From(findings), truncated),
        };
    }
}
