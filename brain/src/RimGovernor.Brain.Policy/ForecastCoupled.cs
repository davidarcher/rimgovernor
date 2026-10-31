using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Transient mechanics attached to tasks, never a second execution journal.</summary>
public sealed record ForecastNeedTask(string Task, string Pawn, string Kind, string Target,
    Resource Resource, int Count, double Gain, EquatableArray<NeedMemory> Thoughts)
{
    public string FoodReady { get; init; } = "";
    public double ServiceDelay { get; init; }
    /// <summary>Stack quantity the claims on this target sum against (<see cref="Count"/> per claim); a target that is not a stack has no bound.</summary>
    public TargetQuantity Quantity { get; init; } = new TargetQuantity.Unbounded();
    /// <summary>The produced batch this claim eats from when the target is a cell several batches can be delivered to;
    /// empty when the target itself is the stack. Claims sum against <see cref="Quantity"/> per (target, batch).</summary>
    public string Batch { get; init; } = "";
    /// <summary>Simultaneous claimants the target admits (vanilla ReservationManager maxPawns).</summary>
    public int MaxPawns { get; init; } = 1;
    public string FreshFrom { get; init; } = "";
}
/// <summary>A physical lot the forecast starts with. <see cref="Freshness"/> is how long it keeps from the forecast start: it
/// rots, or it never does.</summary>
public sealed record ForecastMaterialLot(string Lot, string Location, Resource Resource, double Count)
{ public FoodFreshness Freshness { get; init; } = new FoodFreshness.Never(); }
/// <summary>
/// One planned site's generated work (#3276): the composed activities it was realized as (deliveries, clearing and its
/// finish) and the finish realizations among them. Stuff is a ring site's stuff, empty for any other site. The
/// activities have no action template until placement (#3278); a Deconstruct clearing, bound to its standing building, is
/// startable work and is left out (#3557).
/// </summary>
public sealed record ForecastPlannedSite(string Site, int Room, string Stuff, EquatableArray<string> Activities, EquatableArray<string> Finishes);

/// <summary>
/// What one schedule completes of a planned room: the planned sites (ids) whose finish completes inside the horizon,
/// and the room alternatives all of whose sites do. A room with completed sites and no complete alternative reports
/// its later effect unassessed (planned_room_effect_unassessed), never as zero, unless its service day is projected
/// (PlannedRoomCredits.ServiceDays, #3796), which assesses it.
/// </summary>
public sealed record ForecastPlannedRoomOutcome(int Room, EquatableArray<string> CompletedSites, EquatableArray<string> Complete);

public sealed record ForecastCoupledContext
{
    public EquatableArray<ForecastTradeWork> TradeWork { get; init; }
    public required ForecastActivityProblem Activities { get; init; }
    public ForecastModelBasis? ModelBasis { get; init; }
    /// <summary>Each skill stock's next level and the days a raise lasts (<see cref="ProjectMarginals.SkillRaises"/>, #3912); what <see cref="SkillValue"/> prices work speed by.</summary>
    public IReadOnlyDictionary<string, SkillRaise> SkillRaises { get; init; } = new Dictionary<string, SkillRaise>();
    /// <summary>What a joiner's value is priced from beside <see cref="ModelBasis"/> (#3805, <see cref="JoinerMoodRules"/>); null before tier 2 ran.</summary>
    public JoinerBasis? Joiners { get; init; }
    public bool Truncated { get; init; }
    public required NeedsForecastRead Needs { get; init; }
    public NativeFoodAssessment NativeFood { get; init; } = new NativeFoodAssessment.Unassessed("food_assessment_not_provided");
    public EquatableArray<ForecastMaintenanceRequirement> MaintenanceRequirements { get; init; }
    public EquatableArray<ForecastPracticeTask> PracticeTasks { get; init; }
    public required WorkResourceView Resources { get; init; }
    /// <summary>The construction read the candidates came from; null for a context built without one.</summary>
    public ConstructionActivityRead? Construction { get; init; }
    public SupportActivityRead Support { get; init; } = new() { Demands = [], Workers = [], Deliveries = [], Findings = [] };
    public CoupledThermalRead? Thermal { get; init; }
    public EquatableArray<ForecastShelterPlan> Shelters { get; init; }
    /// <summary>The planned sites generated as construction activities (#3276), and the room alternatives they make up.</summary>
    public EquatableArray<ForecastPlannedSite> PlannedSites { get; init; }
    public EquatableArray<PlannedRoomAlternative> PlannedRooms { get; init; }
    /// <summary>What the review's incumbents hold from a fresh order now (#3889): the forecast dispatches an outcome as the review
    /// does before reading whom it orders at tick 0.</summary>
    public ForecastIncumbentHold IncumbentHold { get; init; } = ForecastIncumbentHold.Nothing;
    /// <summary>Each planned room's role, from the layout plan (#3651); every room with a role is valued by its credit.</summary>
    public IReadOnlyDictionary<int, PlannedRole> PlannedRoomRoles { get; init; } = new Dictionary<int, PlannedRole>();
    /// <summary>Derived functions of the stations assigned to each room, sharing its one native role and stats.</summary>
    public IReadOnlyDictionary<int, EquatableArray<PlannedRole>> PlannedRoomFunctions { get; init; } = new Dictionary<int, EquatableArray<PlannedRole>>();
    /// <summary>The credit of each priced room role (#3796): a role is priced when it has one (PlannedRoomCredits). The shipped
    /// credits by default; tests inject one.</summary>
    public IReadOnlyDictionary<PlannedRole, RoomCredit> RoomCredits { get; init; } = PlannedRoomCredits.Shipped;
    /// <summary>The closed-room predictions of the planned rooms (#3339): what each room is once its ring of one stuff stands.</summary>
    public EquatableArray<ForecastPlannedClosure> Closures { get; init; }
    /// <summary>The builders a room's owed work is projected over (#3744, #3796): the free colonists of the needs census
    /// capable of Construction (LayoutClusterRules.BuildersOf). None free projects nothing.</summary>
    public int PlannedBuilders { get; init; }
    /// <summary>The canonical population census used to judge prisoner intent and read existing certainty.</summary>
    public PrisonerCensus? PrisonerPopulation { get; init; }
    /// <summary>The owed work in ticks (#3744, #3796) of each planned site (by site id: the least finish, a Finishes row's ticks or a
    /// roof's RoofCellWorkTicks per cell, plus the least delivery haul and clearing it was generated with) and of each placed
    /// site and standing removal (by its id: the least work of its finishes). A room owing an item without a row is not projected.</summary>
    public IReadOnlyDictionary<string, double> OwedWork { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);
    /// <summary>Per option's (activity, pawn), the least work in ticks its generation read (#3825), which the work shortfall
    /// (<see cref="WorkShortfallRules"/>) measures unscheduled work by; an option whose work was not read has no row.</summary>
    public IReadOnlyDictionary<(string Activity, string Pawn), double> OptionWork { get; init; } = new Dictionary<(string, string), double>();
    /// <summary>What a pawn's bed costs the colony (#3825, <see cref="PawnValueRules"/>), set by the review beside <see cref="MaterialValues"/>;
    /// null before the review priced it.</summary>
    public PawnBedFacts? Bed { get; init; }
    /// <summary>The planned layout's food walks a pawn's food upkeep hauls over (#3849, <see cref="FoodHaulRules.Legs"/>), set by the
    /// review beside <see cref="Bed"/>; null before a layout is planned, when the review values no pawn and names the plan the values
    /// await (<see cref="PawnValueRules.AwaitingLayoutPlan"/>, #3857).</summary>
    public FoodHaulLegs? FoodHaul { get; init; }
    /// <summary>The placed sites and standing removals the planned rooms hold (#3796, epic #3650 P4), by the rooms whose
    /// interior or ring cells hold them: part of those rooms' owed work.</summary>
    public EquatableArray<ForecastRoomItem> RoomItems { get; init; }
    /// <summary>The connectors among the planned rooms (#3815, epic #3650 P4): each circulation corridor or airlock, by room, and
    /// the rooms it reaches or seals (PlannedRoomCredits.ConnectorsOf). Its owed work is part of each of those rooms'.</summary>
    public IReadOnlyDictionary<int, EquatableArray<int>> Connectors { get; init; } = new Dictionary<int, EquatableArray<int>>();
    public IReadOnlyDictionary<int, EquatableArray<PlannedBuildSite>> RoomPrerequisites { get; init; } = new Dictionary<int, EquatableArray<PlannedBuildSite>>();
    public IReadOnlyList<FoodStock> FreezerStock { get; init; } = [];
    public IReadOnlyDictionary<int, double> RotRates { get; init; } = new Dictionary<int, double>();
    /// <summary>The open planned rooms (#3744, #3754): observed started (BuildingPlanner.PlannedRoomStartedOf) and not
    /// done (PlannedRules.PlannedRoomDone, as their latest reconcile found). Their owed work is done first in the service-day
    /// projection (#3796). Set by the round beside <see cref="InvestmentHorizonDays"/>; none by default.</summary>
    public IReadOnlySet<int> OpenRooms { get; init; } = new HashSet<int>();
    /// <summary>The owed placed sites that are blocked pieces (#3827): the one blocked predicate of PlannedRules.PlannedRoomDone
    /// (BuildingPlanner.DefinitionBlockedOf, read evidence only, #3819), resolved by the round beside <see cref="OpenRooms"/>.
    /// A blocked item does not delay its room's service day (PlannedRoomCredits.ServiceDays); none by default. A planned site of
    /// a blocked piece is never generated (PlannedBuildSite.Blocked, #3838).</summary>
    public IReadOnlySet<string> BlockedItems { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Per planned bedroom, the colonists the bedroom step would move into it (SleepingRules.PlannedBedroomOccupants,
    /// #3655, #3656: one, or a couple), by pawn id; a bedroom without a row houses nobody and earns no credit.</summary>
    public IReadOnlyDictionary<int, EquatableArray<string>> BedroomOccupants { get; init; } = new Dictionary<int, EquatableArray<string>>();
    /// <summary>Per planned storage room, yard and meal closet, the stock it would keep (ReviewFacts.PlannedStoreStock, #3798);
    /// null when the stockpile review did not read it, which leaves those rooms' credit unassessed.</summary>
    public IReadOnlyDictionary<int, PlannedStock>? StoreStock { get; init; }
    /// <summary>Per planned throne room or suite, the colonist it would serve (titled for a throne room; any suite occupant,
    /// #3826) and its title's requirement of that room (#3807, SleepingUpkeepPlanner.PlannedOccupantsOf); a room without a
    /// row serves nobody and earns no credit.</summary>
    public IReadOnlyDictionary<int, RoyalRoomNeed> RoyalRooms { get; init; } = new Dictionary<int, RoyalRoomNeed>();
    /// <summary>The layout plan's rooms by id (ReviewFacts.PlannedShelters, #3800): what a planned room's predicted
    /// cleanliness reads beside its closure prediction (PlannedRoomCleanlinessRules.Steady); the kitchen and butchery credits
    /// read their stations' meals and filth runs (#3802).</summary>
    public IReadOnlyDictionary<int, PlannedRoom> LayoutRooms { get; init; } = new Dictionary<int, PlannedRoom>();
    /// <summary>The catalog's filth model (ReviewFacts.Filth) a planned room's predicted cleanliness and its poisoning chance read (#3797, #3800, #3802).</summary>
    /// <summary>The current station-flow SleepDisturbed mood points per sleeper in each planned room.</summary>
    public IReadOnlyDictionary<int, double> RoomSleepDisturbance { get; init; } = new Dictionary<int, double>();
    public FilthModel Filth { get; init; } = new();
    /// <summary>Per research bench def, its ResearchSpeedFactor from the def data (ReviewFacts.ResearchBenchSpeeds, #3800).</summary>
    public IReadOnlyDictionary<string, BenchSpeedModel> ResearchBenchSpeeds { get; init; } = new Dictionary<string, BenchSpeedModel>();
    public IReadOnlyDictionary<string, BenchSpeedModel> WorkBenchSpeeds { get; init; } = new Dictionary<string, BenchSpeedModel>();
    public LedgerView? ProductionOrders { get; init; }
    /// <summary>The work dispatcher's observed calibration stalls (ReviewFacts.ProductionCalibration); unobserved until the ledger reconciles.</summary>
    public ProductionCalibration ProductionCalibration { get; init; } = new ProductionCalibration.Unobserved(ProductionCalibration.UnobservedReason);
    public IReadOnlyDictionary<string, RecipeFlow> ProductionRecipes { get; init; } = new Dictionary<string, RecipeFlow>();
    /// <summary>The worship room's believers, presence demands and rituals (ReviewFacts.Worship, #3808).</summary>
    public WorshipFacts Worship { get; init; } = new();
    /// <summary>The planned pens by room id, each its herd's key (ReviewFacts.PlannedPens, #3812).</summary>
    public IReadOnlyDictionary<int, string> PenHerds { get; init; } = new Dictionary<int, string>();
    /// <summary>The player's roamers (AnimalUpkeepObservation.Roamers, #3812); null when native could not read the animal census,
    /// which a served pen's credit raises as a required fact.</summary>
    public IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.ColonyAnimal>? Roamers { get; init; }
    /// <summary>The pawns a sparring ring trains (PracticeMethodCandidates.TraineesOf, #3813); null when the work pawns were not
    /// read, which a served ring's credit raises as a required fact.</summary>
    public PracticeTrainees? Sparring { get; init; }
    /// <summary>The pawns a training range trains (PracticeMethodCandidates.TraineesOf, #3846); null when the work pawns were not
    /// read, which a served range's credit raises as a required fact.</summary>
    public PracticeTrainees? Range { get; init; }
    /// <summary>The bench a planned laboratory is furnished with (ReviewFacts.LaboratoryBench, #3800); "" none.</summary>
    public string LaboratoryBench { get; init; } = "";
    /// <summary>
    /// Per planned room, its planned beds in site id order, one per anchor (#3840): the sleeping facts of each Build site whose
    /// def is a sleeping bed (ReviewFacts.SleepingBeds), null for a bed def whose stuff has no row. A room without a row plans no bed.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<SleepingBedFacts?>> PlannedBeds { get; init; } = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>>();
    /// <summary>Prison bed anchors, standing first then planned in site order, using the existing sleeping census and planned sites.</summary>
    public IReadOnlyDictionary<int, Cell[]> PrisonBeds { get; init; } = new Dictionary<int, Cell[]>();
    /// <summary>The canonical planned Build sites in nurseries, used to price play sources and decoration exposure.</summary>
    public EquatableArray<PlannedBuildSite> NurserySites { get; init; }
    /// <summary>The comfort need's NeedDef curve (ReviewFacts.ComfortCurve, #3840) the bedroom credit's comfort term runs; unknown by default.</summary>
    public NeedCurve ComfortCurve { get; init; }
    /// <summary>The planned deathrest chamber's caskets and other deathrest buildings as the staging resolves them (#3810,
    /// SleepingUpkeepPlanner.DeathrestFurnitureOf); none while no deathrester owes a chamber. Set by the round beside
    /// <see cref="RoyalRooms"/>.</summary>
    public EquatableArray<DeathrestPiece> DeathrestFurniture { get; init; }
    /// <summary>The planned containment cells, their predicted strength and the entity each already holds (#3811,
    /// <see cref="ForecastContainment.CellsOf"/>); none without a planned cell. Set by the round beside <see cref="DeathrestFurniture"/>.</summary>
    public EquatableArray<PlannedContainmentCell> ContainmentCells { get; init; }
    /// <summary>The creepjoiners the colony holds apart and the catalog's downside table (#3844, <see cref="ForecastIsolation"/>);
    /// unread until the round reads them. Set by the round beside <see cref="ContainmentCells"/>.</summary>
    public CreepJoinerForecast CreepJoiners { get; init; } = new();
    /// <summary>The days a served room's credit runs to (#3655, #3796): the horizon tier 2 ran over (ColonyModelPolicy.HorizonDays),
    /// set beside <see cref="Investment"/>; 0 before tier 2 ran, which credits nothing.</summary>
    public double InvestmentHorizonDays { get; init; }
    /// <summary>The tending a wound gets over the horizon, which tier 2 averages off its trajectory (<see cref="ColonyValue.TendingOverHorizon"/>,
    /// #3804, #3868); null before tier 2 ran or unread.</summary>
    public ForecastTending? Tending { get; init; }
    /// <summary>When a part, organ or medicine arrives (#3868): the review's sources plus only this outcome's selected requests (#3917).</summary>
    public StockArrival Stock { get; init; } = new();
    /// <summary>The review's stock before the selected requests; a reference, not a second read or store.</summary>
    internal StockArrival? StockBeforeRequests { get; init; }
    /// <summary>The bed a planned hospital is furnished with (#3804): the first of RoomFurniture.HospitalBeds, with its def's stats; no def without a catalog.</summary>
    public FurnitureBed HospitalBed { get; init; } = new();
    public EquatableArray<ForecastMaterialLot> InitialLots { get; init; }
    public EquatableArray<CoupledFoodUse> FoodUses { get; init; }
    public EquatableArray<ForecastNeedTask> NeedTasks { get; init; }
    /// <summary>Observed retained waiting occupies time but is not productive labor or a need service.</summary>
    public EquatableArray<string> NonproductiveOccupancy { get; init; }
    internal IReadOnlyDictionary<(string Task, string Pawn), Methods.ActivityTaskCandidate> Bindings { get; init; } = new Dictionary<(string, string), Methods.ActivityTaskCandidate>();
    public EquatableArray<string> Findings { get; init; }
    /// <summary>Per pawn, what the temperature cards are evaluated against (#3327); a pawn without a row keeps their thoughts constant.</summary>
    public IReadOnlyDictionary<string, CardPawnFacts> CardPawns { get; init; } = new Dictionary<string, CardPawnFacts>();
    /// <summary>Per colonist read whole, its observed thought rows (MoodPawn.Thoughts, #3805): the rows a temperature card's offset
    /// replaces (#3327), the room and roof thoughts a move changes, and the colony's thoughts the joiner's mood takes
    /// (<see cref="JoinerMoodRules.ColonyThoughts"/>); a colonist without a row was not read.</summary>
    public IReadOnlyDictionary<string, EquatableArray<MoodThought>> ColonistThoughts { get; init; } = new Dictionary<string, EquatableArray<MoodThought>>(StringComparer.Ordinal);
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
}
/// <summary>One number the forecast ranks or explains an outcome by: the value it assessed, or the reason it could not
/// (the finding or unread input that names why).</summary>
public abstract record ForecastTerm
{
    private ForecastTerm() { }
    public sealed record Assessed(double Value) : ForecastTerm;
    public sealed record Unassessed(string Reason) : ForecastTerm;
}
/// <summary><see cref="Feasible"/> is whether every activity of the schedule completes: an unread fact that settles one refuses it
/// with a finding that names the fact, so an infeasible outcome always says why.</summary>
public sealed record ForecastCoupledOutcome(ForecastActivityPlan Plan, bool Feasible,
    IReadOnlyDictionary<string, ForecastNeedsResult> Needs,
    EquatableArray<ForecastFinding> Findings)
{
    public ForecastTerminalAccounting? Terminal { get; init; }
    public EquatableArray<ForecastShelterOutcome> Shelters { get; init; }
    public EquatableArray<ForecastMaintenanceConsequence> Maintenance { get; init; }
    public EquatableArray<ForecastThermalExposure> ThermalExposure { get; init; }
    public EquatableArray<string> CompletedConstructionSites { get; init; }
    /// <summary>Per planned room with a site completed inside the horizon: its completed sites and complete alternatives.</summary>
    public EquatableArray<ForecastPlannedRoomOutcome> PlannedRooms { get; init; }
    /// <summary>The planned rooms the schedule serves (PlannedRoomCredits.ServiceDays, #3796): inside the horizon once it
    /// finishes their owed work, past it at their projected service day.</summary>
    public EquatableArray<ForecastRoomService> ServedRooms { get; init; }
    /// <summary>The planned rooms whose every native roof cell completes inside the horizon. Physical completion evidence for the construction relief gate.</summary>
    public EquatableArray<int> CompletedRoofRooms { get; init; }
    /// <summary>Assessed stationary-location exposure avoided relative to the same occupancy without proposed room changes.
    /// Unobserved transit/idle receive no benefit. Unassessed when the need services' exposure was not assessed in full.</summary>
    public ForecastTerm StationaryThermalRelief { get; init; } = new ForecastTerm.Unassessed("thermal_relief_not_evaluated");
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
    /// The tier-1 horizon: the exact near-term schedule normal ReviewState forecast over (at most one game day). A gear bill
    /// whose stuff arrives later is not offered in tier 1 (<see cref="StuffArrival"/>).
    /// </summary>
    public const double TierOneHorizonDays = 1.0;

    /// <summary>Composition boundary used by normal ReviewState facts: one observed frame, stock owner and horizon.</summary>
    public static ColonyForecastRequest CoupledForecastRequestOf(ColonyForecastOrigin origin, double horizonDays,
        ReviewFacts facts, DerivedDemand demand, ForecastActivityProblem activities,
        EquatableArray<ForecastNeedTask> needTasks, EquatableArray<ForecastCommitment> commitments)
    {
        if (facts.Tick.Value != origin.ObservedTick || demand.Work.Tick != facts.Tick
            || facts.ForecastObserved.Needs is not { } needs || needs.Tick != facts.Tick
            || facts.WorkView is not { } resources || resources.Evidence.Tick != facts.Tick)
            throw new DomainException("coupled forecast requires one observed review frame");
        return new()
        {
            Origin = origin,
            HorizonDays = horizonDays,
            Facts = ColonyForecastFactsRules.ColonyForecastFactsOf(facts),
            Commitments = commitments,
            Coupled = new() { Activities = activities, Needs = needs, Resources = resources, NeedTasks = needTasks }
        };
    }

    public static ColonyForecastRequest CoupledForecastRequestOf(ColonyForecastOrigin origin, double horizonDays,
        ReviewFacts facts, DerivedDemand demand, EquatableArray<ForecastCommitment> commitments)
    {
        var generated = facts.CoupledAlternatives ?? throw new DomainException("coupled observed review unavailable");
        return CoupledForecastRequestOf(origin, horizonDays, facts, demand, generated.Context.Activities,
            generated.Context.NeedTasks, commitments) with
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
        // Refused or truncated scenarios still validate the observed frame, even without a stock projection.
        foreach (var row in request.Facts.Census.Resources)
            if (!AdmissionRules.ValidResource(new Resource(row.DefName)) || row.Units < 0)
                throw new DomainException("invalid forecast observed stock");
        if (commitments.Values.Any(c => c.ActivityCompletions.Count != 0 && c.StockChanges.Count != 0))
            throw new DomainException("coupled commitments have one timed effect source");
        var horizon = request.HorizonDays * GameTime.TicksPerDay;
        if (horizon > GameTime.TicksPerDay || context.Needs.Tick.Value != request.Origin.ObservedTick
            || context.Resources.Evidence.Tick.Value != request.Origin.ObservedTick)
            throw new DomainException("coupled forecast needs one observed tick and at most one day");
        var all = cache.Tasks(context.Activities);
        foreach (var id in context.NonproductiveOccupancy)
            if (!all.TryGetValue(id, out var wait) || wait.Inputs.Count != 0 || wait.Outputs.Count != 0
                || wait.Location != wait.EndLocation || wait.NotBefore != 0 || wait.LatestStart != 0 && !context.TradeWork.Any(w => w.Task == id && w.Mission is not null)
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
            return new ColonyForecastScenario
            { Coupled = new([], ["exclusive_outcome_conflict"], false) };
        var perSearch = bound ?? context.Activities.ExpansionBudget;
        var problem = context.Activities with
        {
            Activities = all.Values.Where(t => included.Contains(t.Id)).ToArray(),
            Options = context.Activities.Options.Where(o => included.Contains(o.Activity)).ToArray(),
            Deadline = horizon,
            ExpansionBudget = perSearch
        };
        if (problem.Activities.Where(t => t.ChoiceIdentity.Length != 0).GroupBy(t => t.ChoiceIdentity).Any(g => g.Count() > 1))
            return new ColonyForecastScenario
            { Coupled = new([], ["exclusive_operation_conflict"], false) };
        if (included.Count > 32) return new ColonyForecastScenario
        { Coupled = new([], [.. findings, "selected_activity_budget_truncated"], true) };
        // Necessary physical-lot conservation before expensive assignment search. Ignoring time,
        // expiry and reservations only overestimates supply; their authoritative checks remain below.
        foreach (var input in problem.Activities.SelectMany(t => t.Inputs).Where(i => i.Lot.Length != 0).GroupBy(i => (i.Lot, i.Resource)))
        {
            var output = problem.Activities.SelectMany(t => t.Outputs).Where(o => o.Lot == input.Key.Lot && o.Resource == input.Key.Resource).ToArray();
            var observed = context.InitialLots.Where(l => l.Lot == input.Key.Lot && l.Resource == input.Key.Resource).ToArray();
            if (input.Sum(i => i.Units) > output.Sum(o => o.Units) + observed.Sum(l => l.Count))
                return new ColonyForecastScenario
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
            for (var round = 0; round < 2 && !exhausted && results.All(r => !r.Feasible); round++)
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
        return new ColonyForecastScenario
        {
            Coupled = new(EquatableArray.From(results), EquatableArray.From(findings), truncated),
        };
    }
}
