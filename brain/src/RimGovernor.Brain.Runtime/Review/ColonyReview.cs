using System.Diagnostics;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

/// <summary>
/// ColonyReview observes and journals needs under Player's existing gate.
/// Its planning steps admit Methods to the journal; Hands issues their game orders.
/// It does not acquire authority.
/// </summary>
public sealed partial class ColonyReview
{
    /// <summary>The families' Needs the runtime composes (#3280).</summary>
    internal IReadOnlySet<NeedId> Needs = new HashSet<NeedId>();

    /// <summary>The composed Needs; a bare ColonyReview enables no coupled activities.</summary>
    internal EquatableArray<NeedId> AvailableNeeds =>
        [.. Needs.OrderBy(n => n.Value, StringComparer.Ordinal)];
    internal Player Player;
    internal IReviewSource Native;
    internal IClock Clock;
    internal PlannerSettings Policy;

    /// <summary>
    /// The native surface of the resource planner, set when it is composed: the Round's resource supply plan
    /// reads mine sources and bench bills through it. Null plans only the census kinds (chop, harvest, hunt).
    /// </summary>
    internal GameClient? ResourceNative;

    internal TimeSpan MaxAge;

    /// <summary>The latest review reading for the planners of the same tick; see reviewCensus.</summary>
    internal CensusStore Census = new();

    /// <summary>
    /// Receives each review's decoded sections; the scheduler that steps this reviewer sets it, a standalone
    /// reviewer files nowhere.
    /// </summary>
    internal FactStore? Store;

    /// <summary>The latest review's derived resource demand (derived state, empty until the first review after a restart).</summary>
    internal ReviewMemory<DerivedDemand> Demand = new(new());
    /// <summary>The current selected outcome's purchase waits; derived once per review, never saved as a request promise.</summary>
    internal ReviewMemory<(EquatableArray<StockNeed> Needs, EquatableArray<Resource> Medicines)> TradeWaits = new(([], []));

    /// <summary>Whether the review turns the player's goals into demand (Capabilities.Goals, #3494).</summary>
    internal bool Goals;

    /// <summary>What the player's goals asked of the existing channels in the latest review (derived state, none until the first review after a restart); ObserveReviewAsync stamps it onto a fresh planner read.</summary>
    internal ReviewMemory<GoalDemand> GoalDemands = new(GoalDemand.None);

    /// <summary>The safety floor every route to a goal broke in the latest coupled comparison, by goal need id (#3503; derived state, none until the first comparison after a restart); the goals read reports those goals infeasible.</summary>
    internal ReviewMemory<IReadOnlyDictionary<string, string>> GoalInfeasible = new(new Dictionary<string, string>());

    /// <summary>The game day the tier-2 model completes each unmet goal in the latest review, by goal need id (#3505; derived display state, never stored, none until the first review after a restart); the goals read puts it on the goal's status.</summary>
    internal ReviewMemory<IReadOnlyDictionary<string, int>> GoalEta = new(new Dictionary<string, int>());

    /// <summary>The per-pawn outfit filter and insulation floor the latest review with a coupled selection chose (GearChoices; derived state, none until the first such review after a restart).</summary>
    internal ReviewMemory<EquatableArray<GearChoice>> GearChoices = new([]);

    /// <summary>
    /// The latest known coupled comparison's planned-room selection (#3278; derived state, empty until the first comparison
    /// after a restart): the room reconcile holds an assessed unstarted room it did not select and places only the sites it did.
    /// </summary>
    internal ReviewMemory<PlannedSelection> RoomSelection = new(new());

    /// <summary>
    /// The planned rooms whose latest room reconcile owed nothing a builder can do now (#3754, PlannedRules.PlannedRoomDone;
    /// derived state, none until a reconcile after a restart): a started room not in it is open, and its ring closes first
    /// in the ring-closure projection (ForecastCoupledContext.OpenRooms).
    /// </summary>
    internal ReviewMemory<EquatableArray<int>> DoneRooms = new([]);

    /// <summary>The urgent placements that wait for a review to compare their sites (#3537); the clock stays held meanwhile.</summary>
    internal UrgentBuilds UrgentBuilds { get; } = new();

    /// <summary>The ids the latest coupled comparison that selected anything chose (#3563) and the planned rooms of that review (#3556): the next search's warm start; derived state, empty until the first such review after a restart.</summary>
    internal ReviewMemory<(EquatableArray<string> Selected, EquatableArray<PlannedRoom> Rooms)> CoupledMemory = new(([], []));

    /// <summary>The pawn values of the latest review that valued pawns (#3825): the next review's creepjoiner Leaves and Aggressive price (#3844); derived state, empty until the first such review after a restart.</summary>
    internal ReviewMemory<EquatableArray<PawnValue>> PawnValueMemory = new([]);

    /// <summary>Each placed site's open construction progress window the latest review observed (ConstructionStalls, task 5.4):
    /// in-memory derived state, empty after a restart and for another world, so a stall needs a whole window of fresh readings.</summary>
    internal ReviewMemory<EquatableArray<ConstructionProgressWindow>> ConstructionMemory = new([]);

    /// <summary>Each tend demand's open unserved window the latest review observed (CareStalls, task 5.5): in-memory derived state,
    /// empty after a restart and for another world, so a stall needs a whole window of fresh readings.</summary>
    internal ReviewMemory<EquatableArray<UnservedCareWindow>> CareMemory = new([]);

    /// <summary>Since when each room has had unfinished blueprints or frames; see noteStall.</summary>
    internal Dictionary<string, RoomStall> RoomStalls = [];

    /// <summary>The quest skips already logged.</summary>
    internal HashSet<QuestSkipKey> SkipsLogged = [];

    /// <summary>The last tech tier logged: the OTLP log ring records a change once, not every review.</summary>
    internal TechTier? TechTierLogged;

    /// <summary>The realized-consumption ledger of the current load, in memory only.</summary>
    internal ConsumptionMemory Consumption = new();

    /// <summary>The safe-area Need's (shelter/safe-area) memory of the bot-owned allowed areas.</summary>
    internal SafeAreaMemory SafeArea = new();

    /// <summary>The firebreak ring's review memory, set when the layout family composes layout/firebreak.</summary>
    internal FirebreakMemory? Firebreak;

    /// <summary>Set when the admin family runs admin/creep-joiners: the review reads its record for the isolation room.</summary>
    internal bool CreepJoinerIsolation;

    /// <summary>Set when the mood relief planner is composed: it casts mood psycasts, so the review reads the royalty facts.</summary>

    /// <summary>The acceptance fault that pins the food plan's gap to zero (Faults.FoodGapZero); the scheduler sets it.</summary>
    internal bool FoodGapZero;

    /// <summary>The acceptance fault that raises the stock target the station chain sizes storage for; the scheduler sets it.</summary>
    internal Dictionary<Resource, long>? StockDemand;

    /// <summary>The delivery credit's factors, in memory only.</summary>
    internal DeliveryCredit FoodCredit = new();

    /// <summary>The formation hunts the plan opened, in memory only.</summary>
    internal HuntAdmission HuntAdmission = new();

    /// <summary>The tick of the last layout survey this process read; see reviewLayoutPlan.</summary>
    internal Tick PlanChecked;

    /// <summary>
    /// The world the last layout survey was read in; null before the first. The reviewer outlives a load, so a survey of
    /// another colony, map or load is no survey of this one (<see cref="Surveyed"/>).
    /// </summary>
    internal GenerationSnapshot? PlanSurveyedIn;

    /// <summary>
    /// What the colony's first plan holds the clock for (#3652; <see cref="ReviewFirstPlanAsync"/>): the station fact
    /// its last derive found unread, or <see cref="LayoutPlanning.FirstPlanPending"/> while the recorded plan waits for a
    /// review that starts with it. Empty holds nothing. In memory only: a restart derives again.
    /// </summary>
    internal string FirstPlanUnread = "";

    /// <summary>The tier and finished research the saved plan was last grown for, in memory only: a restart replans once.</summary>
    internal string PlanGrownFor = "";

    internal int PlanPawns;

    /// <summary>The last replan's survey and inputs; an hourly check that finds them unchanged skips the replan.</summary>
    internal LayoutInputs PlanInputs = LayoutInputs.Empty;

    /// <summary>When the last adopted replan started, for the daily floor (<see cref="LayoutPlanning.ReplanDaily"/>); in memory only, as PlanInputs.</summary>
    internal Tick PlanReplanned;

    /// <summary>The advice of the last station chain run.</summary>
    internal StationAdvice StationLast = new();

    /// <summary>Whether the last station chain run made advice.</summary>
    internal bool StationHeld;

    /// <summary>The layout replan running off the hold path, until a review adopts or drops it (#3565).</summary>
    internal PendingLayoutReplan? LayoutPending;

    /// <summary>What each review lap entered on, against the previous review's (#3564): the rounds_review row's laps_changed.</summary>
    internal readonly LapInputs LapInputs = new();

    /// <summary>An invalid saved layout plan is logged once.</summary>
    internal bool LayoutInvalidLogged;

    /// <summary>Draws the layout plan as a native overlay; the overlay fields record the last draw.</summary>
    internal bool LayoutOverlay;

    /// <summary>Whether the review reads the clearance census (Capabilities.ClearanceCensus).</summary>
    internal bool ClearanceCensus;

    /// <summary>Whether the review reads the jail shell definitions (Capabilities.JailShell).</summary>
    internal bool JailShell;

    internal string OverlayKey = "";
    internal Tick OverlayDrawn;
    internal bool OverlayCleared;

    internal Tick HeatDrawn;
    internal bool HeatCleared;

    /// <summary>The last safety layer sent; see drawSafetyOverlay.</summary>
    internal OverlayState Safety = new();

    /// <summary>The last work-spot layer sent; see drawSpotOverlay.</summary>
    internal OverlayState Spots = new();

    /// <summary>The last stock layer sent; see drawStockOverlay.</summary>
    internal OverlayState Stock = new();

    /// <summary>The load's catalog item facts for a planner that holds no frame; none when the reviewer's source serves no definitions.</summary>
    internal async Task<ItemFacts> ItemFactsAsync(GenerationSnapshot snapshot, CancellationToken ct)
    {
        if (Native is not IDefinitionSource source)
        {
            return new ItemFacts();
        }
        var catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        return catalog.ItemFacts();
    }

    /// <summary>The load's combat drugs in catalog preference order.</summary>
    internal async Task<List<string>> CombatDrugsAsync(GenerationSnapshot snapshot, CancellationToken ct)
    {
        var items = await ItemFactsAsync(snapshot, ct).ConfigureAwait(false);
        var defs = new List<string>();
        foreach (var drug in items.CombatDrugs())
        {
            defs.Add(drug.Def.ToString());
        }
        return defs;
    }

    /// <summary>
    /// Records the reading's tech tier in the OTLP log ring once per change: `[layout] tech tier Masonry
    /// (Stonecutting)`. The reading always carries its observed tier.
    /// </summary>
    internal void LogTechTier(ColonyReading projection, CancellationToken ct)
    {
        var tier = TechTierRules.SelectTechTier(projection.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), projection.Snapshot.PlayerTechLevel);
        if (TechTierLogged == tier)
        {
            return;
        }
        TechTierLogged = tier;
        var evidence = TechTierRules.TechTierEvidence(projection.Facts.Research.Finished, projection.Snapshot.PlayerTechLevel);
        var message = "tech tier " + tier;
        if (evidence != "")
        {
            message += " (" + evidence + ")";
        }
        ClockEvents.Event("layout", "tech_tier", message, ("tier", tier.ToString()), ("evidence", evidence));
    }

    /// <summary>Records each quest offer the colony does not accept (policy.QuestSkips) once per quest and reason.</summary>
    internal void LogQuestSkips(ReviewFacts facts, CancellationToken ct)
    {
        foreach (var skip in QuestSelection.QuestSkips(facts))
        {
            LogQuestSkip(skip, ct);
        }
    }

    internal void LogQuestSkip(QuestSkip skip, CancellationToken ct)
    {
        if (!SkipsLogged.Add(new QuestSkipKey(skip.Quest, skip.Reason)))
        {
            return;
        }
        Tel.Decide(QuestSkipDecision(skip));
    }

    /// <summary>
    /// The configured policy with its food and wood targets widened by the calendar the reading carries
    /// (policy.PlannerSettings.Seasonal): the same targets DetectReview measures the latches against.
    /// </summary>
    internal PlannerSettings Seasonal(ReviewFacts facts) => Policy.Seasonal(facts.Calendar, facts.DisasterConditions);

    /// <summary>
    /// Keeps the review frame's colony facts, by reference, for the census the review retains: planner reads inside
    /// the reuse window are served that section (<see cref="ColonyFactsAsync"/>). The frame is immutable once
    /// published. A frame without colony facts keeps the section already held.
    /// </summary>
    internal void PublishFrame(ReviewFrame frame)
    {
        if (frame.Colony is not null)
        {
            Census.RememberColony(frame.Colony);
        }
    }

    /// <summary>
    /// Constructs a reviewer. Throws <see cref="ControlException"/> when the arguments cannot review.
    /// </summary>
    public ColonyReview(Player player, IReviewSource native, IClock clock, PlannerSettings thresholds, TimeSpan maxAge, params Capabilities[] capabilities)
    {
        if (player is null || native is null || clock is null || maxAge <= TimeSpan.Zero || maxAge > TimeSpan.FromMinutes(1))
        {
            throw new ControlException("NewRounder: player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > t");
        }
        try
        {
            thresholds.Validate();
        }
        catch (DomainException)
        {
            throw new ControlException("NewRounder: player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > t");
        }
        if (native is not IReviewWorkBenchSource)
        {
            throw new ControlException("NewRounder: native lacks the bench census read");
        }
        var reviewerOverlay = false;
        var clearanceCensus = false;
        var jailShell = false;
        if (capabilities.Length > 1)
        {
            throw new ControlException("NewRounder: len(capabilities) > 1");
        }
        if (capabilities.Length == 1)
        {
            reviewerOverlay = capabilities[0].LayoutOverlay;
            clearanceCensus = capabilities[0].ClearanceCensus;
            jailShell = capabilities[0].JailShell;
            Goals = capabilities[0].Goals;
            Needs = capabilities[0].Needs.ToHashSet();

        }
        Player = player;
        Native = native;
        Clock = clock;
        Policy = thresholds;
        MaxAge = maxAge;
        LayoutOverlay = reviewerOverlay;
        ClearanceCensus = clearanceCensus;
        JailShell = jailShell;
    }

    public async Task<ReviewResult> StepAsync(CancellationToken ct)
    {
        using var entry = await Player.EnterAsync("rounds_review", false, ct).ConfigureAwait(false);
        return await StepAsync(new StepArbiter(), false, entry.Call, entry.Epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Also usable by a scheduler already holding the player gate; <paramref name="isPartial"/> is set when only
    /// a subset of the planners follows the review.
    /// </summary>
    internal async Task<ReviewResult> StepAsync(StepArbiter arbiter, bool isPartial, CancellationToken ct, CancellationToken epoch)
    {
        var timer = Stopwatch.StartNew();
        var stages = new StepStages();
        var fine = new StepStages();
        var span = Tel.StartSpan("buildingruntime", "rounds.review");
        Exception? failure = null;
        try
        {
            return await ReviewStepAsync(arbiter, isPartial, stages, fine, ct, epoch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            Tel.Decide(StepDecision(failure, timer.Elapsed, isPartial, stages, fine));
            Tel.EndSpan(span, failure);
        }
    }

    internal Task<ReviewResult> ReviewStepAsync(StepArbiter arbiter, bool isPartial, CancellationToken ct, CancellationToken epoch) =>
        ReviewStepAsync(arbiter, isPartial, new StepStages(), new StepStages(), ct, epoch);

    // The review's stages (journal, observe, layout, derive, acquisition, review, admit, record) are lapped onto
    // stages, published on the rounds planner_step row as stage_ms / stage_alloc_kb; the d_stock and derive laps are
    // split further onto fine (fine_ms / fine_alloc_kb; #3566).
    internal async Task<ReviewResult> ReviewStepAsync(StepArbiter arbiter, bool isPartial, StepStages stages, StepStages fine, CancellationToken ct, CancellationToken epoch)
    {
        var p = Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return await p.StopReviewAsync(ct).ConfigureAwait(false);
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native.Value == 0");
        }
        var previous = await p.Journal.LoadReviewAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(Native, ct).ConfigureAwait(false);
        if (expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map || !expected.NativeGeneration.IsAt(state.Snapshot.Native.Value))
        {
            throw new ControlException("step: expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.S");
        }
        var plans = await p.Journal.LoadPlansAsync(ct).ConfigureAwait(false);
        var definitions = ProjectBillReads.ProjectDefinitions(plans, state.Snapshot);
        var readDefinitions = definitions;
        if (Needs.Contains(FoodRules.CookingNeed))
        {
            readDefinitions = [.. readDefinitions, "NutrientPasteDispenser", "Hopper"];
        }
        if (RoomsEnabled())
        {
            readDefinitions = [.. readDefinitions, .. RoomQualityRules.RoomBeautyDefinitions()];
        }
        if (Needs.Contains(AdminFamily.Burial))
        {
            readDefinitions = [.. readDefinitions, .. BurialFacts.BurialDefinitions];
        }
        if (Needs.Contains(MilitaryFamily.Training))
        {
            readDefinitions = [.. readDefinitions, .. MilitaryReview.TrainingDefinitions];
        }
        if (Needs.Contains(FoodRules.CookingNeed) || Needs.Contains(FoodStoresRules.RefrigerationNeed) || JailShell)
        {
            // The planned kitchen, freezer and jail shells; the jail bed is a furniture rule's, read with every catalog.
            readDefinitions = [.. readDefinitions, "Wall", "Door"];
        }
        if (Needs.Contains(AdminFamily.Stockpiles))
        {
            // The warehouse shelf upgrade needs the Shelf's availability.
            readDefinitions = [.. readDefinitions, StorageRules.ShelfDefinition];
        }
        // The tier-2 model prices every crop a zone grows or a goal's stuff needs, so the review reads the sowable crops as the field and
        // acquisition readings do; the starter set alone leaves a devilstrand or healroot zone without a crop row (#3567).
        readDefinitions = [.. readDefinitions, .. await SowableCropsAsync(state.Snapshot, ct).ConfigureAwait(false)];
        var claims = await p.Journal.ConstructionClaimsAsync(state.Snapshot, expected.Tick, ct).ConfigureAwait(false);
        // A colony with no layout plan reads what its first plan needs (#3652): the rooms census carries the dining furniture.
        var (_, haveLayout) = await LayoutPlanAsync(state.Snapshot, expected.Tick, ct).ConfigureAwait(false);
        stages.Lap("journal");
        LapInputs.Begin(state.Snapshot);
        using var window = PlannerWindowRules.StandaloneWindow(Native);
        var reading = await ObserveReviewAsync(Native, expected, claims, readDefinitions, ct).ConfigureAwait(false);
        // Each lap through derive leaves its reading with LapInputs, so the row can name the laps whose inputs changed (#3564).
        void Lap(string name)
        {
            stages.Lap(name);
            LapInputs.Left(name, reading.Reading);
        }
        Lap("observe");
        PublishFrame(reading.Frame);
        await BreakDispatch.ReleaseBreakWorkAsync(p.Journal, state.Snapshot, reading.Emergency, plans, ct).ConfigureAwait(false);
        LogTechTier(reading.Reading, ct);
        LogQuestSkips(reading.Reading.Facts, ct);
        var foodProjection = reading.Reading;
        PlanFood(ref foodProjection);
        reading = reading.WithReading(foodProjection);
        reading = reading.WithFacts(rf => rf with { ConstructionClaims = claims });
        reading = reading.WithFacts(rf => rf with
        {
            WorkAdmission = new()
            {
                Buildings = (reading.Catalog ?? throw new RequiredFactException("definition_catalog")).WorkBuildingCosts(),
                Recipes = (reading.Catalog ?? throw new RequiredFactException("definition_catalog")).ProductionRecipeInputs(
                    reading.Frame.Tables.Buildings.Table.Values().Select(b => b.Building.DefName)),
                Bills = ConstructionReads.ProductionBatchStates(reading.Frame.Bills),
            }
        });
        var batches = await p.Journal.ProductionBatchesAsync(state.Snapshot, reading.Reading.Identity.Tick, ct).ConfigureAwait(false);
        var workActions = p.Journal.WorkActions(state.Snapshot, ct);
        reading = reading.WithFacts(rf => WorkRules.ReviewWorkResources(rf, batches, workActions));
        reading = reading.WithColonySection(c => c with { Facts = c.Facts with { ConstructionClaims = reading.Reading.Facts.ConstructionClaims } });
        ReviewRoyalty(reading);
        // A creepjoiner held apart owes the isolation room, so the census is read before the child rooms and the layout.
        if (CreepJoinerIsolation)
        {
            var creepRecord = await p.Journal.LoadCreepJoinersAsync(Player.PlayerWorld(state.Snapshot), ct).ConfigureAwait(false);
            reading = reading.WithFacts(f => f with { Isolation = CreepJoiner.CreepJoinerIsolation(reading.Reading, reading.Frame, reading.Catalog, creepRecord) });
        }
        reading = ReviewChildRooms(reading);
        reading = reading.WithReading(await ReviewLayoutPlanAsync(state.Snapshot, reading.Reading, ct).ConfigureAwait(false));
        Lap("layout");
        reading = reading.WithColonySection(c => c with { Facts = c.Facts with { LayoutPlan = reading.Reading.Facts.LayoutPlan } });
        var layoutPlan = reading.Reading.Facts.LayoutPlan;
        Census.RememberLayout(reading.Reading.Identity, reading.Reading.Facts.LayoutPlan);
        reading = reading.WithFacts(rf => rf with { TitleClaimQuests = [.. SleepingUpkeepPlanner.TitleClaimQuests(reading.Reading)] });
        reading = reading.WithFacts(rf => rf with { BedroomsOwed = SleepingUpkeepPlanner.BedroomsOwedOf(reading.Reading) });
        reading = reading.WithFacts(rf => rf with { ButcherTableAffordable = FacilityPlanningRules.ButcherTableAffordable(reading.Reading.Facts) });
        reading = reading.WithFacts(rf => rf with { WarmRooms = BurialFacts.WarmCoolingRooms(reading.Reading) });
        reading = reading.WithFacts(rf => rf with { MealClosetOwed = BurialFacts.MealClosetOwed(reading.Reading) });
        reading = reading.WithFacts(rf => rf with { CampfireRetireOwed = FacilityPlanningRules.CampfireRetireOwed(reading.Reading.Facts, nameof(RimWorld.ThingDefOf.Campfire)) });
        reading = reading.WithFacts(rf => rf with { TemperatureOwed = FacilityPlanningRules.TemperatureOwed(reading.Reading.Facts) });
        Lap("d_owed");
        var targets = await ArmoryShells.ShellTargetsAsync(Native, p.Journal, state.Snapshot, reading.Reading, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { ShellTargets = EquatableArray.From(targets), ShellsShort = ArmoryShellsRules.ShellsShort(targets, ResourceStockRules.Count(reading.Reading.Snapshot.Resources)) });
        if (Needs.Contains(ShelterFamily.SafeArea))
        {
            reading = reading.WithFacts(rf => rf with { SafeAreaOwed = SafeArea.Review(StockpilePlanner.StockpileWorld(state.Snapshot), reading.Reading) });
        }
        if (Needs.Contains(RoomsFamily.Art))
        {
            reading = reading.WithFacts(rf => rf with { SculptureRoomsOwed = SleepingUpkeepPlanner.SculptureRoomsOwedOf(reading.Reading) });
        }
        reading = reading.WithFacts(rf => rf with { ExportProducts = [.. Exports.Products(state.Snapshot)] });
        var vSaleArt = await SleepingUpkeepPlanner.ReviewSaleArtAsync(Native, Boundary.Identity(state.Snapshot), reading.Reading, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { SaleArt = vSaleArt });
        var (parts, benches) = await SurgeryPartDemandAsync(Native, Boundary.Identity(state.Snapshot), reading.Reading.Facts.MedicalPawns, ColonyPlanningRules.SurgeryContext(reading.Reading.Facts), ct).ConfigureAwait(false);
        if (parts.Count > 0)
        {
            reading = reading.WithFacts(rf => rf with { FabricableParts = SurgeryRules.FabricableParts(benches) });
        }
        Lap("d_reads");
        fine.Mark();
        // The journal's zone claims and shelf plans are read once; the zone census and the stockpile review share them (#3566).
        var zoneReads = ReadStockpileZones(state.Snapshot, reading.Reading, ct);
        reading = reading.WithReading(CensusStockpileZones(reading.Reading, zoneReads));
        fine.Lap("d_stock/zones");
        // The review's shelf candidates become planned sites below.
        // The stockpile review and the consumption window read nothing of each other, so their hops share frames.
        var stockFine = new StepStages();
        var stockpileReview = ReviewStockpilesAsync(state.Snapshot, reading.Reading, zoneReads, ct, stockFine);
        var vResourceConsumption = await ResourceConsumptionAsync(state.Snapshot, ct).ConfigureAwait(false);
        var (shelfCandidates, stockpileRequest) = await stockpileReview.ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { ResourceConsumption = vResourceConsumption });
        fine.Lap("d_stock/stockpile");
        fine.Add(stockFine);
        var vResourceSurfaceOre = await ResourceSurfaceOreAsync(state.Snapshot, reading.Reading.Facts.Items, reading.Reading.Facts.ResourceConsumption, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { ResourceSurfaceOre = vResourceSurfaceOre });
        fine.Lap("d_stock/ore");
        var drugPolicies = WorkPlanner.OwnedDrugPolicies(reading.Reading.Snapshot.Policies);
        reading = reading.WithFacts(rf => rf with { DrugUsers = DrugRunwayRules.DrugUsers(reading.Reading.Facts.Items, drugPolicies) });
        reading = reading.WithReading(ReviewMeals(reading.Reading));
        var reserveProjection = reading.Reading;
        ReviewReserve(ref reserveProjection);
        reading = reading.WithReading(reserveProjection);
        reading = reading.WithReading(ReviewBabyFeeding(reading.Reading));
        if (Needs.Contains(RoomsFamily.Flooring))
        {
            reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Flooring = BuildingPlanner.TrafficFlooringFacts(reading.Reading) } });
            reading = reading.WithColonySection(c => c with { Facts = c.Facts with { Upkeep = c.Facts.Upkeep with { Flooring = reading.Reading.Facts.Upkeep.Flooring } } });
        }
        fine.Lap("d_stock/meals");
        Lap("d_stock");
        // The owed diff over every built room is taken once a review; the pricing and the sites read it.
        var plannedOwed = PlannedOwedOf(reading.Reading, Policy.Flooring);
        var layout = reading.Reading.Facts.LayoutPlan;
        IReadOnlyList<PlannedRoom> plannedRooms = layout is not null ? layout.Rooms : [];
        reading = reading.WithFacts(rf => rf with
        {
            PlannedConstruction = PlannedConstructionOf(reading.Reading, plannedOwed),
            PlannedShelters = EquatableArray.From(plannedRooms.Where(r => !r.Outdoor)),
            PlannedLinks = EquatableArray.From(layout?.Links ?? []),
            PlannedPens = plannedRooms.Where(r => r.Role == PlannedRole.Pen && r.Id > 0).DistinctBy(r => r.Id).ToDictionary(r => r.Id, r => r.Herd),
            // The planned stores' stock (#3798) is read from the request the stockpile review just reviewed; null when it did not run.
            PlannedStoreStock = stockpileRequest is { } stockRequest ? StoreRules.PlannedStoreStock(stockRequest, plannedRooms, PlannedRoomCredits.FunctionsOf(reading.Reading.Facts)) : null,
        });
        // The firebreak review reads nothing the planned sites answer, so its tile reads share frames with the site reads.
        fine.Mark();
        var firebreak = Firebreak?.ReviewAsync(Boundary.Identity(state.Snapshot), state.Snapshot, reading.Reading, FirebreakPlan.FirebreakBusy(plans, state.Snapshot), ct);
        var vPlannedSites = await PlannedSitesAsync(state.Snapshot, reading.Reading, plannedOwed, shelfCandidates, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { PlannedSites = vPlannedSites });
        fine.Lap("d_planned/sites");
        var (vPlannedThermal, vRoomPredictions, vRoomPrerequisites) = await PlannedShelterReadsAsync(state.Snapshot, reading.Reading, reading.Reading.Facts.PlannedShelters, vPlannedSites, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { PlannedThermal = vPlannedThermal, PlannedRoomPredictions = vRoomPredictions, RoomPrerequisites = vRoomPrerequisites });
        fine.Lap("d_planned/shelter");
        if (firebreak is not null)
        {
            // The ring plan's dwell clock advances every review; the layout family reads the work it leaves.
            await firebreak.ConfigureAwait(false);
        }
        fine.Lap("d_planned/firebreak");
        Lap("d_planned");

        reading = reading.WithColonySection(c => c with { Facts = c.Facts with { FoodPlan = reading.Reading.Facts.FoodPlan, FoodReserve = reading.Reading.Facts.FoodReserve } });
        Census.Retain(reading);
        var filedScope = new FactScope(expected.Load.Value, expected.Map.Value, state.Snapshot.Native.Value);
        reading.Sections.File(Store, filedScope);
        // A served native section may have gained a fresh derived food review.
        if (Store is not null && reading.Sections.Colony is { Source: not "" } colonyHeld)
        {
            FactStore.Put(Store, filedScope, FactSection.Colony, colonyHeld);
        }
        var emergency = EmergencySnapshot.New(state.Snapshot, expected.Tick, reading.Emergency);
        var (hostiles, criticalPatients) = EmergencyNeedsRules.EmergencyNeeds(emergency, state.Snapshot, expected.Tick);
        reading = reading.WithFacts(rf => rf with { Hostiles = hostiles, CriticalPatients = criticalPatients });
        reading = reading.WithFacts(rf => rf with { QuestCalm = QuestCapacity.QuestCalmColony(reading.Reading.Facts, EmergencyRules.EvaluateEmergency(emergency, state.Snapshot, expected.Tick), emergency.PodsPending) });
        reading = reading.WithFacts(rf => rf with { DangerSeeds = EquatableArray.From(ShelterRules.DangerSeeds(reading.Emergency.Threats)) });
        var seeds = reading.Reading.Facts.DangerSeeds;
        reading = reading.WithFacts(rf => rf with { DangerWindow = SafeArea.DangerWindow(StockpilePlanner.StockpileWorld(state.Snapshot), reading.Reading.Facts.Hostiles, seeds, expected.Tick) });
        reading = reading.WithFacts(rf => rf with { DangerHaulers = ShelterRules.DangerHaulers(reading.Reading.Facts.ForecastObserved.WorkPawns) });
        reading = reading.WithFacts(rf => rf with { CoupledWorkActive = plans.Any(plan => !plan.Retired
            && plan.SupersededBy.IsEmpty
            && plan.Progress.Any(a => a.Action.PawnActivities is { Tasks.Count: > 0 } batch
                && batch.Orders.Any(o => !o.Release && PawnActivities.Intervention(o))
                && a.View.Stage is not (Stage.Unsuccessful or Stage.Cancelled))) });
        reading = reading.WithFacts(rf => rf with { Dash = DashPlanRules.ReviewDash(previous.Latches.Dash, reading.Reading.Facts, reading.Reading.Facts.ForecastObserved.WorkPawns, expected.Tick, DashEstimateRules.PublishedHypothermiaTuning) });
        reading = reading.WithFacts(rf => rf with { UrgentPatients = EmergencyNeedsRules.UrgentPatients(emergency, state.Snapshot, expected.Tick) });
        var (amputationCritical, amputationUrgent) = EmergencyNeedsRules.PatientCounts(reading.Emergency, reading.Reading.Facts.MedicalPawns);
        reading = reading.WithFacts(rf => rf with { CriticalPatients = amputationCritical, UrgentPatients = amputationUrgent });
        // The admin/work-assignments Need is owed while a drafted colonist no live plan needs waits for the undraft sweep. A draft
        // its plan still holds is working, not stranded.
        var needed = await p.Journal.PlannedDraftsAsync(ct).ConfigureAwait(false);
        var stray = DraftNeeds.IdleDrafts(state, reading.Emergency, reading.Frame.Pawns, needed);
        reading = reading.WithFacts(rf => rf with { CleanupPawns = stray.Count > 0 });
        reading = reading.WithFacts(rf => rf with { HostilityOwed = HostilityRules.HostilityOwed(WorkPlanner.HostilityPawns(reading.Reading.Facts.ForecastObserved.WorkPawns), reading.Emergency.Threats) });
        reading = reading.WithFacts(rf => rf with { MedicalCareOwed = MedicalRules.MedicalCareOwed(reading.Reading.Facts, Policy.MedicalReserve) });
        reading = reading.WithFacts(rf => rf with { NamesOwed = PawnRules.NamesOwed(reading.Reading.Facts.Population) });
        reading = reading.WithFacts(f => ColonyPlanningRules.ApplyFieldBudget(f, Seasonal(f).FoodTargetDays));
        // The workshop ladder's recorded research rung is the derived economy/research target; it is journal
        // evidence, not a native read, so a review costs no extra call for it.
        NoteEquipment(state.Snapshot, reading.Reading.Facts);
        Lap("d_file");
        GoalsFacts? goalFacts = null;
        if (Goals)
        {
            // The player's goals become demand before any family reads: stock floors, a colonist head count and herd
            // targets ride the facts, the research they wait on joins what the census asks for (#3494).
            var goals = await p.Journal.LoadGoalsAsync(Player.PlayerWorld(state.Snapshot), ct).ConfigureAwait(false);
            goalFacts = GoalsFacts.From(reading.Reading.Facts, TechTierRules.SelectTechTier(reading.Reading.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), reading.Reading.Snapshot.PlayerTechLevel), reading.Reading.Facts.ForecastObserved.WorkPawns, goals.Goals, goals.Themes, new Dictionary<NeedId, OwnerProgress>(), GoalsReview.StuffResearch(reading.Reading.Facts.Definitions)) with { Rooms = reading.Reading.Facts.Upkeep.Rooms };
            var goalDemand = GoalRules.DemandOf(goalFacts);
            GoalDemands.Set(state.Snapshot, goalDemand);
            _goalResearch = goalDemand.Research;
            reading = reading.WithFacts(rf => GoalRules.Apply(rf, goalDemand));
        }
        var needs = ProjectBillReads.ResearchNeeds(p.Journal, state.Snapshot, EquipmentOwed(state.Snapshot), ct);
        needs = CensusResearchNeeds(needs);
        reading = reading.WithFacts(rf => rf with { ResearchNeeds = [.. needs] });
        var medicine = MedicalRules.ReviewMedicalReserve(reading.Reading.Facts.MedicalReserve, previous.Snapshot == state.Snapshot && previous.Latches.MedicalReserve, Policy.MedicalReserve);
        reading = reading.WithFacts(rf => rf with { MedicineCarryOwed = MedicineCarryRules.MedicineCarryOwed(reading.Reading.Facts.ForecastObserved.WorkPawns, medicine) });
        // The routine trade buys medicine before parts (#3907): the stock-arrival model claims the gap's silver and carries the latch.

        // A prisoner surgery blocked only by the herbal care limit.
        reading = reading.WithFacts(rf => rf with { ResourceNeeds = PrisonerMedicineRules.PrisonerHerbalNeeds(reading.Reading.Facts.ResourceNeeds, reading.Reading.Facts, SilverGapRules.SilverShort(reading.Reading.Facts, Policy, medicine.Active)) });
        // A willing colonist's psylink neuroformer, bought or made by the resource ladder.
        if (Needs.Contains(RoyaltyFamily.Psylink) && reading.Reading.Facts.Royalty is RoyaltyState.Active { Facts: var psylinkRoyalty })
        {
            var psylinkCandidates = PsylinkRules.PsylinkCandidates(psylinkRoyalty, reading.Reading.Facts.ForecastObserved.WorkPawns);
            reading = reading.WithFacts(rf => rf with { ResourceNeeds = PsylinkRules.NeuroformerNeeds(reading.Reading.Facts.ResourceNeeds, psylinkRoyalty, psylinkCandidates) });
        }
        // The work goal's coverage reads the demand the last review derived: the value the work planner dispatches on.
        var resourceTargets = ResourceTargets(state.Snapshot);
        var pawns = reading.Reading.Facts.ForecastObserved.WorkPawns.Pawns;
        {
            reading = reading.WithFacts(rf => rf with { WorkProfiles = [.. PawnRules.Profiles(pawns)] });
            var required = WorkDemandRules.ProjectWork(definitions, reading.Reading.Facts.Definitions);
            var nativeBench = (IReviewWorkBenchSource)Native;
            var benchSource = BenchSource(nativeBench, expected, true);
            var recovered = ResearchRules.ResourceTargetNeed(resourceTargets, reading.Reading.Facts.ObservedStock()).Recovered;
            var deficitTargets = WorkDemandRules.DeficitTargets(resourceTargets, !recovered, reading.Reading.Facts.Gear);
            var benchWork = await ProjectBillReads.BenchWorkAsync(benchSource, state.Snapshot, plans, deficitTargets, deficitTargets.Count > 0, reading.Reading.Facts.Items.Wort, ct).ConfigureAwait(false);
            required = WorkDemandRules.MergeWorkRequirements([.. required], benchWork);
            required = WorkDemandRules.MergeWorkRequirements([.. required], WorkDemandRules.ResearchWork(needs, reading.Reading.Facts.Research));
            required = WorkDemandRules.MergeWorkRequirements([.. required], FieldPlanner.FishingWork(reading.Reading));
            required = WorkDemandRules.MergeWorkRequirements([.. required], EntityStudyRules.StudyWork(reading.Reading.Facts.Containment));
            {
                var demand = Disease.DiseaseDemand(reading.Reading, definitions, previous, state.Snapshot);
                {
                    var work = WorkRules.PlanWork(pawns, required!, demand);
                    reading = reading.WithFacts(rf => rf with { WorkCoverage = work.Planned ? (work.Matches ? WorkCoverageState.Matched : WorkCoverageState.Unmatched) : WorkCoverageState.Unread });
                    if (ConstructionSkillRules.ConstructionSkillChoices(reading.Reading.Facts.CurrentConstruction, pawns).Count > 0)
                    {
                        reading = reading.WithFacts(rf => rf with { WorkCoverage = WorkCoverageState.Unmatched });
                    }
                    if (work.Planned)
                    {
                        reading = reading.WithFacts(rf => rf with { WorkRoster = new WorkRosterRead.Read([.. work.Coverage]) });
                        reading = reading.WithFacts(rf => rf with { QuestRoster = QuestCapacity.QuestRosterOf(reading.Reading.Facts.ForecastObserved.WorkPawns, reading.Reading.Facts.MoodPawns, work) });
                        reading = reading.WithFacts(rf => rf with { WorkHelp = work.Help });
                    }
                }
            }
        }
        reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Rooms = reading.Reading.Facts.Upkeep.Rooms } });
        Lap("d_work");
        p.Current(ct, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }
        // Manual cancels ctx before waiting for this gate, then invalidates any completed review before
        // returning. Never hold the local stop mutex for SQL.
        if (ClearanceCensus)
        {
            if (Native is not IClearanceSource clearanceSource)
            {
                throw new ControlException("step: !ok");
            }
            var ground = PlannedGround(reading.Reading);
            var census = await clearanceSource.ReadClearanceAsync(expected, true, ground, ct).ConfigureAwait(false);
            var blueprints = HomeUpkeepReader.GroundBlueprints(reading.Reading);
            var (others, player) = ClearanceGroundRules.SplitGroundRows(ClearanceReads.Targets(census).Concat(blueprints ?? []), reading.Reading.Facts.Preferences);
            reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Clearance = new ClearanceCensus([.. others]) } });
            reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { RoomObstructions = RecoveryRules.RoomObstructionIDs(others, ground) } });
            // Retired ground is clear only once its blueprints are known gone too (#3742).
            if (blueprints is not null)
            {
                reading = reading.WithReading(DropClearedRetiredGround(state.Snapshot, reading.Reading, player, census.Floors, ct));
            }
            reading = reading.WithColonySection(c => c with { Facts = c.Facts with { LayoutPlan = reading.Reading.Facts.LayoutPlan } });
        }
        Lap("d_clear");
        fine.Mark();
        reading = reading.WithFacts(rf => rf with { AvailableNeeds = AvailableNeeds });
        // The admin/work-ledger Need reads the diff from the ledger's memory (LedgerOwedIn).
        var ledgerOwed = await ReviewLedgerAsync(state.Snapshot, expected, reading.Reading, ct, fine).ConfigureAwait(false);
        lock (Ledger.Mu)
        {
            Ledger.Owed = ledgerOwed;
        }
        fine.Lap("derive/ledger");
        // The ledger's declared batches are read after the declaration above.
        var vOpenBills = await OpenBillsAsync(state.Snapshot, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { OpenBills = [.. vOpenBills], ProductionOrders = WorkLedgerView(), ProductionCalibration = WorkLedgerCalibration() });
        var construction = ConstructionStalls.Observe(ConstructionMemory.Get(state.Snapshot), reading.Reading.Facts.ForecastObserved.Coupled?.Construction);
        if (construction is ConstructionCalibration.Observed observedConstruction) ConstructionMemory.Set(state.Snapshot, observedConstruction.Windows);
        var care = CareStalls.Observe(CareMemory.Get(state.Snapshot), reading.Reading.Facts.ForecastObserved.Coupled);
        if (care is CareCalibration.Observed observedCare) CareMemory.Set(state.Snapshot, observedCare.Windows);
        reading = reading.WithFacts(rf => rf with { ConstructionCalibration = construction, CareCalibration = care });
        fine.Lap("derive/bills");
        reading = reading.WithFacts(rf => rf with { UrgentSites = UrgentBuilds.Covered });
        // The harm term's creepjoiners and the pawn values their leaving prices (#3844), read before the coupled context's harm inputs.
        reading = reading.WithFacts(rf => rf with { CreepJoiners = CreepJoiner.CreepJoinerHarmOf(reading.Reading, reading.Frame, reading.Catalog), PreviousPawnValues = PawnValueMemory.Get(state.Snapshot) });
        var settlementOptions = await SettlementForecastOptionsAsync(state.Snapshot, reading.Reading, reading.Catalog, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { StockArrival = (rf.StockArrival with { Missions = settlementOptions,
            Visits = ForecastTradeVisits.Options(rf.Traders, rf.Census.ColonistCount) })
                .WithMedicineClaim(rf.Items, medicine, rf.Census.ColonistCount, Policy.MedicalReserve) });
        var layoutDemand = ResourceRules.ResourceDemandOf(reading.Reading.Facts, Policy, new Latches { MedicalReserve = medicine.Active, Dash = reading.Reading.Facts.Dash });
        var roomSleep = await RoomSleepDisturbanceAsync(state.Snapshot, reading.Reading,
            reading.Catalog ?? throw new RequiredFactException("definition_catalog"), layoutDemand, ct).ConfigureAwait(false);
        reading = reading.WithFacts(f => f with { RoomSleepDisturbance = roomSleep });
        var reconsideration = ForecastIncumbentPreparation.PrepareCoupledIncumbents(reading.Reading.Facts, batches, workActions);
        reading = reading.WithFacts(_ => reconsideration.Facts with { PreviousCoupledSelection = CoupledMemory.Get(state.Snapshot).Selected, PreviousPlannedShelters = CoupledMemory.Get(state.Snapshot).Rooms });
        fine.Lap("derive/incumbents");
        // Tier 2 (#3531): once per review, before the comparison, so the tasks of research and sowing carry their credit.
        IReadOnlyDictionary<string, int> goalEtas = new Dictionary<string, int>();
        if (reading.Reading.Facts.CoupledAlternatives is { } coupled)
        {
            var model = ReviewColonyModel.Review(reading.Reading, coupled.Context, goalFacts);
            goalEtas = model.Etas;
            var materials = await MaterialValuesAsync(state.Snapshot, coupled.Context, reading.Reading.Facts.Stations, reading.Reading.Facts.Items, ct).ConfigureAwait(false);
            // A pawn's bed (#3825, V6), which its value charges unless one stands vacant.
            var bed = await PawnBedAsync(state.Snapshot, reading.Reading, ct).ConfigureAwait(false);
            // The food's hauls over the planned layout (#3849), which a pawn's food upkeep prices.
            var foodHaul = layoutPlan is null ? null : FoodHaulRules.Legs(layoutPlan, reading.Reading.Facts.FoodWork);
            var occupantFindings = new SortedSet<string>(coupled.Context.Findings, StringComparer.Ordinal);
            var (occupants, royal) = SleepingUpkeepPlanner.PlannedOccupantsOf(reading.Reading, occupantFindings);
            // The open planned rooms, whose rings close first in the ring-closure projection (#3744, #3754): started and not
            // done by their latest reconcile, of the plan the layout review read (a later retired-ground drop leaves its rooms as they are).
            var done = DoneRooms.Get(state.Snapshot);
            var started = layoutPlan is null ? []
                : layoutPlan.Rooms.Where(r => BuildingPlanner.PlannedRoomStartedOf(reading.Reading, layoutPlan, r) == true).Select(r => r.Id).ToList();
            var open = started.Where(id => !done.Contains(id)).ToHashSet();
            // The blocked pieces the rooms owe, by the reconcile's one predicate: they delay no service day (#3827).
            var blocked = BuildingPlanner.BlockedRoomItems(reading.Reading, coupled.Context);
            if (layoutPlan is not null)
            {
                // The colony review's open-rings threshold reads this row (#3751).
                ClockEvents.Event("routine", LayoutProgressTelemetry.Kind, "layout progress", [
                    (LayoutProgressTelemetry.PlannedKey, layoutPlan.Rooms.Count),
                    (LayoutProgressTelemetry.StartedKey, started.Count),
                    (LayoutProgressTelemetry.BuiltKey, layoutPlan.Rooms.Count(r => done.Contains(r.Id))),
                    (LayoutProgressTelemetry.OpenKey, open.Order().ToArray()),
                    (LayoutProgressTelemetry.BuildersKey, coupled.Context.PlannedBuilders),
                ]);
            }
            // A goal tier 2 priced is paid at its marginal value, not the flat share (#3505); the clothing runway demands the stuff it chose (#3623);
            // a joiner is valued against the work an outcome of these roots leaves short (#3805).
            reading = reading.WithFacts(rf => rf with
            {
                CoupledAlternatives = coupled with { Context = coupled.Context with { ModelBasis = model.Basis, Joiners = new() { Roots = [.. coupled.Alternatives, .. coupled.Incumbents], Work = reading.Reading.Facts.WorkSpeeds, Food = reading.Reading.Facts.FoodWork }, Investment = model.Marginals.PerUnit, SkillRaises = model.Marginals.SkillRaises, MaterialValues = materials, InvestmentHorizonDays = model.HorizonDays, Tending = model.Tending, Bed = bed, FoodHaul = foodHaul, BedroomOccupants = occupants, RoyalRooms = royal, DeathrestFurniture = SleepingUpkeepPlanner.DeathrestFurnitureOf(reading.Reading), ContainmentCells = SleepingUpkeepPlanner.ContainmentCellsOf(reading.Reading), CreepJoiners = CreepJoiner.CreepJoinerForecastOf(reading.Reading, reading.Frame, reading.Catalog), OpenRooms = open, BlockedItems = blocked, Findings = EquatableArray.From(occupantFindings.ToArray()), GoalValues = GoalRules.Priced(coupled.Context.GoalValues, model.Marginals.Goals) } },
                ClothingStuff = model.ClothingStuff,
            });
            ClockEvents.Event("routine", ColonyModelTelemetry.Kind, "colony model ran", [.. model.Attrs()]);
        }
        fine.Lap("derive/model");
        // Replan geometry is compared with the completed model before the authoritative review chooses or admits work.
        if (await ReviewCompletedLayoutReplanAsync(state.Snapshot, reading.Reading, new Latches { MedicalReserve = medicine.Active, Dash = reading.Reading.Facts.Dash },
            goalFacts, stockpileRequest, batches, workActions, ct).ConfigureAwait(false) is { } replanned)
        {
            reading = reading.WithReading(replanned.Colony);
            goalEtas = replanned.Model.Etas;
            layoutPlan = replanned.Plan;
            reading = reading.WithColonySection(c => c with { Facts = c.Facts with { LayoutPlan = layoutPlan } });
            Census.RememberLayout(reading.Reading.Identity, layoutPlan);
        }
        // Acquisition uses the review's completed home staffing and defense facts.
        Census.Retain(reading);
        Lap("derive");
        stages.Lap("acquisition");
        var result = await p.Journal.ReviewAsync(new ReviewRequest { Revision = previous.Revision, Current = state.Snapshot, Tick = reading.Reading.Identity.Tick, Enabled = true, Policy = Policy, Facts = reading.Reading.Facts }, ct).ConfigureAwait(false);
        stages.Lap("review");
        p.ForecastEvidence.Review(state.Snapshot, reading.Reading.Facts.Tick.Value, reading.Reading.Facts.ForecastObserved.Needs, reading.Reading.Facts.CoupledAlternatives?.Context,
            reading.Reading.Facts.CombinedFoodSupply);
        Demand.Set(result.Review.Snapshot, result.Needs.ResourceDemand);
        TradeMemory.Set(result.Review.Snapshot, result.Needs.Trade);
        WorkAssignments.Set(result.Review.Snapshot, result.Needs.WorkAssignments);
        NoteGearChoices(GearChoices, result.Review.Snapshot, reading.Reading.Facts.CoupledAlternatives, result.Needs.CoupledSelection);
        if (result.Needs.PawnValues.Count > 0) PawnValueMemory.Set(result.Review.Snapshot, result.Needs.PawnValues);
        if (result.Needs.CoupledSelection is { Selection.Selected.Length: > 0 } chosen) CoupledMemory.Set(result.Review.Snapshot, (EquatableArray.From(chosen.Selection.Selected), reading.Reading.Facts.PlannedShelters));
        RoomSelection.Set(result.Review.Snapshot, PlannedSelectionReviewed(RoomSelection.Get(result.Review.Snapshot), reading.Reading, result.Needs.CoupledSelection));
        GoalEta.Set(result.Review.Snapshot, goalEtas);
        if (result.Needs.CoupledSelection is { } compared) GoalInfeasible.Set(result.Review.Snapshot, GoalRules.InfeasibleOf(compared));
        // The first plan reads the ledger and the demand this review derived (#3652).
        await ReviewFirstPlanAsync(state.Snapshot, result.Review.Snapshot, reading.Reading, result.Needs.Latches, goalFacts, stockpileRequest, batches, workActions, ct).ConfigureAwait(false);
        stages.Lap("first_plan");
        AdmitCoupledSelection(result, reading.Reading.Facts, reconsideration.Incumbents, state.Snapshot,
            InterventionPurposes.ReviewOutcomes(workActions, reading.Reading.Facts.WorkView?.Evidence.WorkResources ?? []), ct, epoch);
        result = result with { FirstPlanUnread = FirstPlanUnread };
        stages.Lap("admit");
        var attrs = new List<(string Key, object? Value)>
        {
            ("revision", result.Review.Revision), ("previous_revision", previous.Revision), ("tick", reading.Reading.Identity.Tick.Value),
            // The policy inspection inside the review stage (the coupled search dominates it): its wall and the
            // search's expansions against the search bound (#3556).
            ("inspect_ms", result.Needs.InspectMs),
            // The phase that found the chosen subset and whether the previous selection seeded it (#3563).
            ("selected_phase", result.Needs.CoupledSelection?.SelectedPhase), ("warm_start", result.Needs.CoupledSelection?.WarmStart), ("warm_translated", result.Needs.CoupledWarmStartTranslated),
        };
        // The search's accounting (#3722) and per-phase expansions and profile (#3563, #3556).
        attrs.AddRange(ReviewSearchAttrs(result.Needs.CoupledSelection));
        attrs.AddRange(PawnValueAttrs(result.Needs));
        // Per lap, the properties of the reading it entered on that differ from the previous review's; the laps
        // that entered on the same reading; what the comparison cost (#3564).
        if (LapInputs.Changed() is { } lapsChanged)
        {
            attrs.Add(("laps_changed", lapsChanged));
            attrs.Add(("laps_same", LapInputs.Same));
            attrs.Add(("laps_compare_ms", LapInputs.CompareMs));
        }
        attrs.AddRange(FoodAttrs(reading.Reading.Facts, Seasonal(reading.Reading.Facts)));
        attrs.Add(("hunt_sources", reading.Reading.Facts.Acquisition.Count(s => s.Hunt && s.Food)));
        if (reading.Reading.Facts.HuntHolds.Count > 0)
        {
            attrs.Add(("hunt_holds", reading.Reading.Facts.HuntHolds.Select(h => h.ID + ":" + h.Reason + (h.Detail.Count == 0 ? "" : "(" + string.Join(";", h.Detail) + ")")).ToArray()));
        }
        if (reading.Reading.Facts.ForecastObserved.Coupled?.Hunt is { } huntRead)
        {
            attrs.Add((ReviewTelemetry.HuntReadKey, $"hunters={huntRead.Hunters.Count} prey={huntRead.Prey.Count} findings=[{string.Join(";", huntRead.Findings)}]"));
            attrs.Add((ReviewTelemetry.HuntChoiceKey, Census.HuntNote));
        }
        ClockEvents.Event("routine", ReviewTelemetry.Kind, "rounds ran", [.. attrs]);
        if (Needs.Contains(MilitaryFamily.Equipment))
        {
            LogGearSearch(GearRules.ExhaustedGearSearches(reading.Reading.Facts.Gear), ct);
        }
        RecordReviewSnapshot(state.Snapshot, reading.Reading.Identity.Tick, result, reading.Reading, ct);
        await DrawSafetyOverlayAsync(state.Snapshot, reading.Reading, reading.Emergency, ct).ConfigureAwait(false);
        await DrawStockOverlayAsync(state.Snapshot, reading.Reading, result, ct).ConfigureAwait(false);
        await DrawSpotOverlayAsync(state.Snapshot, reading.Reading, ct).ConfigureAwait(false);
        stages.Lap("record");
        return result;
    }

    /// <summary>
    /// Journals a gear_search row for each pawn whose loadout search spent policy.GearSearchBudget: its target
    /// is the best ensemble found within the budget, not a proven optimum.
    /// </summary>
    internal static void LogGearSearch(IEnumerable<PawnId> pawns, CancellationToken ct)
    {
        foreach (var pawn in pawns)
        {
            Tel.Decide(new Decision
            {
                Kind = "gear_search",
                Component = "equipment",
                Level = TelemetryLevel.Warn,
                Verdict = "ok",
                Reason = "best_found_within_budget",
                Target = pawn.Value,
                Attrs = new Dictionary<string, object?> { ["budget_nodes"] = GearLoadoutRules.GearSearchBudget },
            });
        }
    }
}

/// <summary>
/// The records are immutable, so a review step
/// holds one <see cref="ReviewReading"/> and replaces it with an edited copy at each assignment.
/// </summary>
internal static class ReadingEdits
{
    public static ReviewReading WithReading(this ReviewReading r, ColonyReading projection) =>
        r with { Reading = projection };

    public static ReviewReading WithFacts(this ReviewReading r, Func<ReviewFacts, ReviewFacts> edit) =>
        r.WithReading(r.Reading with { Facts = edit(r.Reading.Facts) });

    /// <summary>Edits the colony section the step files (the reading's own copy of the projection); a reading without one is unchanged.</summary>
    public static ReviewReading WithColonySection(this ReviewReading r, Func<ColonyReading, ColonyReading> edit) =>
        r.Sections.Colony is { } held ? r with { Sections = r.Sections with { Colony = held with { Value = edit(held.Value) } } } : r;
}

/// <summary>The ColonyReview's configured capabilities: the composed Needs and the optional native writers.</summary>
public sealed record Capabilities
{
    /// <summary>The migrated families' Needs the runtime composes; a Need has no registry row to validate against.</summary>
    public IReadOnlyList<NeedId> Needs { get; init; } = [];

    /// <summary>Draws the layout plan as a native overlay layer (serve --layout-overlay).</summary>
    public bool LayoutOverlay { get; init; }

    /// <summary>
    /// Reads the clearance census each review (#3253): the recovery queue, the room obstructions and the retired
    /// ground drop rest on it. Set when the home-upkeep family runs clearance.
    /// </summary>
    public bool ClearanceCensus { get; init; }

    /// <summary>Reads the Wall and Door definitions the jail shell is planned from (#3262). Set when the prisoners family stages the jail.</summary>
    public bool JailShell { get; init; }

    /// <summary>Turns the player's goals into stock floors, research, a colonist head count and herd targets each review (#3494). Set when the goals family runs.</summary>
    public bool Goals { get; init; }

}

internal readonly record struct QuestSkipKey(QuestId Quest, QuestSkipReason Reason);

partial class ColonyReview
{
    /// <summary>The routine_skip row of an Odyssey offer the colony does not accept: target the quest, reason the skip reason, WARN.</summary>
    internal static Decision QuestSkipDecision(QuestSkip skip) => new()
    {
        Kind = "routine_skip",
        Component = "routine",
        Level = TelemetryLevel.Warn,
        Verdict = "skipped",
        Reason = skip.Reason.ToString(),
        Target = skip.Quest.Value,
        Attrs = new Dictionary<string, object?> { ["quest"] = skip.Quest.Value, ["script"] = skip.ScriptDef, ["detail"] = skip.Detail },
    };

    /// <summary>
    /// The one planner_step row of a ColonyReview step: ok when the review ran, failed with the error as an attr
    /// (and reason control_lost for ControlException) when it did not.
    /// </summary>
    internal static Decision StepDecision(Exception? err, TimeSpan took, bool isPartial, StepStages? stages = null, StepStages? fine = null)
    {
        var attrs = new Dictionary<string, object?> { ["partial"] = isPartial };
        stages?.Publish(attrs);
        fine?.Publish(attrs, "fine_ms", "fine_alloc_kb");
        var d = new Decision
        {
            Kind = Decision.PlannerStepKind,
            Component = "clock-scheduler",
            Target = "rounds",
            Verdict = "ok",
            Reason = "reviewed",
            Dur = took,
        };
        switch (err)
        {
            case OperationCanceledException:
                // An authority change cancelled the round: it stops, it did not fail.
                d = d with { Reason = "cancelled" };
                break;
            case not null:
                d = d with { Level = TelemetryLevel.Warn };
                d = d with { Verdict = "failed" };
                d = d with { Reason = err is ControlException ? "control_lost" : "error" };
                attrs["error"] = err;
                break;
        }
        return d with { Attrs = attrs };
    }

    /// <summary>
    /// The review row's stored-food attrs: the food runway the review read and the seasonal thresholds it held it
    /// to, with the calendar they came from. An unknown fact leaves its attr out.
    /// </summary>
    internal static List<(string Key, object? Value)> FoodAttrs(ReviewFacts f, PlannerSettings seasonal)
    {
        var attrs = new List<(string Key, object? Value)> { ("food_min_days", seasonal.FoodMinDays), ("food_target_days", seasonal.FoodTargetDays) };
        if (f.FoodReserve is FoodReserveState.Reviewed { Review: var reserve })
        {
            attrs.AddRange([("food_reserve_days", seasonal.FoodReserveDays), ("food_reserve_target", reserve.TargetNutrition),
                ("food_reserve_stock", reserve.StockNutrition), ("food_reserve_deficit", reserve.DeficitNutrition), ("food_reserve_emergency", reserve.Emergency)]);
        }
        if (f.FoodPlan is { } plan)
        {
            attrs.AddRange([(ReviewTelemetry.FoodPlanKey, plan.Explain()), ("food_gap_per_day", plan.GapPerDay)]);
        }
        if (f.FoodDays is FoodRunway.Days { Value: var days })
        {
            attrs.Add(("food_days", days));
        }
        var c = f.Calendar;
        attrs.AddRange([("season", c.Season), ("day_of_year", c.DayOfYear), ("growing_days_remaining", c.GrowingDaysRemaining), ("growing_days_until", c.GrowingDaysUntil), ("non_growing_days", c.NonGrowingDays)]);
        return attrs;
    }

    /// <summary>Renders the needs that suspended the review's goals for an event attr.</summary>

    /// <summary>
    /// Writes the review's colony snapshot when snapshot.DirEnv names a directory; a failed write is logged,
    /// never the review's error.
    /// </summary>
    internal static void RecordReviewSnapshot(GenerationSnapshot current, Tick tick, ReviewResult result, ColonyReading reading, CancellationToken ct)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotRecorder.DirEnv) ?? "";
        if (dir == "")
        {
            return;
        }
        SnapshotLaterReplay.Later(() =>
        {
            if (SnapshotReviewReplay.FromReview(current, tick, result, reading) is { } recorded)
            {
                try
                {
                    SnapshotRecorder.Record(dir, recorded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Tel.Decide(SnapshotSkip.SnapshotSkipDecision("routine", "colony", ex, tick));
                }
            }
        }, "colony", tick);
    }

    /// <summary>
    /// The ground of the recorded plan's rooms not yet standing; none while the plan or the room census is
    /// unknown, so nothing of the colony's comes down on a guess. The reviewer's upkeep reading and the
    /// clearance planner share it.
    /// </summary>
    internal static List<Rectangle> PlannedGround(ColonyReading colony)
    {
        if (colony.Facts.LayoutPlan is not { } plan)
        {
            return [];
        }
        var ground = BuildingPlanner.ColonyGround(colony);
        // The native census takes rectangles: a non-rect ground is asked for by its bounds.
        return [.. ClearanceGroundRules.PlannedGround(plan, ground).Select(g => g.Bounds())];
    }

    /// <summary>
    /// What the whole plan still owes, per room id: the reconciler's owed operations over the plan (PlannedOwedCost), priced from the def catalog with
    /// the stuff the shell chooser takes (ShellSharedStuff) for the ring and the bulk chooser's for pieces; null while the plan or the construction census is unread.
    /// A roofed-set cell still open to the sky is owed a roof (RoofIn) once the ring stands; a roof costs no material.
    /// </summary>
    internal static IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>? PlannedConstructionOf(ColonyReading colony, FlooringPolicy flooring) =>
        PlannedConstructionOf(colony, PlannedOwedOf(colony, flooring));

    /// <summary>PlannedConstructionOf over an owed diff already taken (PlannedOwedOf), so one review's readers share it; null without one.</summary>
    internal static IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>? PlannedConstructionOf(ColonyReading colony, PlannedOwed? owed)
    {
        if (owed is null)
        {
            return null;
        }
        var wall = AnimalContainment.AnimalContainmentDefinition(colony.Facts.Definitions, ContainmentCellRules.ShellWallDefinition);
        var door = AnimalContainment.AnimalContainmentDefinition(colony.Facts.Definitions, ContainmentCellRules.ShellDoorDefinition);
        var shell = wall is not null && door is not null ? AnimalContainment.ShellSharedStuff(colony, wall, door) : null;
        IReadOnlyList<Amount>? Price(string name)
        {
            var def = AnimalContainment.AnimalContainmentDefinition(colony.Facts.Definitions, name);
            if (def is null)
            {
                return null;
            }
            if (def.StuffOptions.Count == 0)
            {
                var costs = (def.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).Costs;
                return costs;
            }
            var stuff = shell is not null && def.MakeableFrom(shell) ? shell : null;
            foreach (var option in def.StuffOptions)
            {
                if (option.Stuff == stuff)
                {
                    return option.Costs;
                }
            }
            return null;
        }
        IReadOnlyList<Amount>? PiecePrice(string name) => PlannedPiecePrice(colony, name)?.Costs;
        return PlannedRules.PlannedOwedCost(owed.Rooms, Price, PiecePrice, owed.FloorPrice, ClimateRules.ClimateDefs().VentDef, owed.Placed)
            .ToDictionary(e => e.Key, e => (IReadOnlyDictionary<Resource, long>)e.Value);
    }

    /// <summary>
    /// The plan's owed operations per built room, the placed sites the census owes instead and the floor prices. Sited is
    /// what the planned sites build per room (PlannedSitedOps): Rooms with the roof and vents owed once the ring stands, and
    /// a priced room's standing walls as owed swaps (#3657). WallStuff is the stuff each standing wall is built in.
    /// </summary>
    internal sealed record PlannedOwed(IReadOnlyList<(PlannedRoom Room, IReadOnlyList<Operation> Owed)> Rooms, IReadOnlySet<(string Def, Cell Cell)> Placed,
        Func<string, IReadOnlyList<Amount>?> FloorPrice)
    {
        public IReadOnlyList<(PlannedRoom Room, IReadOnlyList<Operation> Owed)> Sited { get; init; } = Rooms;

        public IReadOnlyDictionary<Cell, string> WallStuff { get; init; } = new Dictionary<Cell, string>();
    }

    /// <summary>
    /// The reconciler's owed diff over every built room of the plan, read against the colony's standing buildings and floors
    /// (PlannedConstructionOf, PlannedSitesAsync); null while the plan or the construction census is unknown.
    /// </summary>
    internal static PlannedOwed? PlannedOwedOf(ColonyReading colony, FlooringPolicy flooring)
    {
        if (colony.Facts.LayoutPlan is not { } plan)
        {
            return null;
        }
        var colonyGround = BuildingPlanner.ColonyGround(colony);
        var flooringFacts = BuildingPlanner.FlooringFactsOf(colony) with { Stock = null };
        IReadOnlyList<Amount>? FloorPrice(string name) =>
            flooringFacts.Definitions.TryGetValue(name, out var floor) ? floor.Costs : null;
        // The colony's standing buildings and floors stand in for the clearance census: the reconcile matches the
        // template against them, so a piece or floor that stands is not owed. Nothing is packed, so every other
        // template piece is a build.
        var rows = new List<ClearanceTarget>();
        var placed = new HashSet<(string, Cell)>();
        var census = colony.Facts.CurrentConstruction;
        foreach (var site in census.Sites.Where(s => s.Building is not null))
        {
            _ = placed.Add((site.Building!.Definition, site.Building.Cell));
        }
        foreach (var b in census.Buildings.Where(b => b.Cells.Count > 0))
        {
            rows.Add(new ClearanceTarget
            {
                EntityId = b.Id,
                DefName = b.Building?.Definition ?? "",
                Minimum = new Cell(b.Cells.Min(c => c.X), b.Cells.Min(c => c.Z)),
                Maximum = new Cell(b.Cells.Max(c => c.X), b.Cells.Max(c => c.Z)),
                Player = true,
            });
        }
        var floors = new List<ClearanceFloor>();
        if (flooringFacts.Observation is { } observed)
        {
            foreach (var cell in observed.Rooms.SelectMany(r => r.Cells))
            {
                floors.Add(new ClearanceFloor { Cell = cell.Cell, DefName = cell.Pending ?? cell.Terrain, Designated = false });
            }
        }
        var owed = new List<(PlannedRoom, IReadOnlyList<Operation>)>();
        var sited = new List<(PlannedRoom, IReadOnlyList<Operation>)>();
        foreach (var room in PlannedRules.PlannedBuiltRooms(plan))
        {
            var input = BuildingPlanner.RoomRingInput(colony, plan, room);
            // The ring input's open-sky cells stay, so the roof is owed (#3275).
            input = input with
            {
                Cells = null,
                Rows = rows,
                Floors = floors,
                Furniture = plan.Pieces.TryGetValue(room.Id, out var pieces) ? [.. pieces.Select(p => p.Wanted())] : [],
                WantedFloor = WantedFloorRules.WantedFloors(room, room.Role == PlannedRole.Worship ? colony.Facts.Worship.FloorTags : [], flooringFacts, flooring),
            };
            var now = ReconcileRules.Reconcile(input).Owed;
            owed.Add((room, now));
            // The roof and vents the ring brings, owed once it stands (#3276): the sites build them, pricing does not.
            var stands = ReconcileRules.Reconcile(input with { Ground = PlannedRules.RingStandsGround(input.Ground, now) }).Owed;
            // Once a priced room's ring stands, its walls are owed as swaps (#3657): the sites offer each other ring stuff
            // as a wall upgrade, which the selection prices against the room's value. An unpriced room is offered none.
            IReadOnlyList<Operation> swaps = PlannedRoomCredits.Shipped.ContainsKey(room.Role)
                ? [.. ReconcileRules.Reconcile(input with { WallUpgrade = _ => true }).Owed.Where(o => o.Kind == OpKind.WallUp)]
                : [];
            sited.Add((room, [.. PlannedRules.PlannedSitedOps(now, stands), .. swaps]));
        }
        return new PlannedOwed(owed, placed, FloorPrice) { Sited = sited, WallStuff = colonyGround.Stuff };
    }

    /// <summary>The stuff a planned piece is built from (the bulk chooser's, "" for a def made from no stuff) and its cost list; null where the def is unavailable.</summary>
    internal static (string Stuff, IReadOnlyList<Amount> Costs)? PlannedPiecePrice(ColonyReading colony, string name)
    {
        if (AnimalContainment.AnimalContainmentDefinition(colony.Facts.Definitions, name) is not { } def)
        {
            return null;
        }
        if (def.StuffOptions.Count == 0)
        {
            var costs = (def.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).Costs;
            return ("", costs);
        }
        var stuff = ColonyPlanningRules.BulkBuildStuff(colony.Facts, ResourceStockRules.Count(colony.Snapshot.Resources), name, 1);
        return def.StuffOptions.FirstOrDefault(o => o.Stuff == stuff) is { } option ? (stuff, option.Costs) : null;
    }

    /// <summary>The recorded plan's retired ground with its kept rooms' walls; empty while the plan is unknown.</summary>
    internal static RetiredGround RetiredGroundOfColony(ColonyReading colony)
    {
        var plan = colony.Facts.LayoutPlan;
        return ClearanceGroundRules.RetiredGroundOf(plan ?? new LayoutPlan());
    }
}

public sealed partial class ColonyReview
{
    /// <summary>
    /// Records the plan without the retired ground the census found clear; the projection carries the recorded plan.
    /// </summary>
    internal ColonyReading DropClearedRetiredGround(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<ClearanceTarget> player, IReadOnlyList<ClearanceFloor> floors, CancellationToken ct)
    {
        if (projection.Facts.LayoutPlan is not { } plan || plan.RetiredGround.Count == 0)
        {
            return projection;
        }
        var done = ClearanceGroundRules.RetiredGroundDone(plan, player, floors);
        if (done.Count == 0)
        {
            return projection;
        }
        var next = plan.WithoutRetiredGround(done);
        Player.Journal.RecordLayoutPlan(snapshot, projection.Identity.Tick, next, ct);
        return projection with { Facts = projection.Facts with { LayoutPlan = next } };
    }
}
