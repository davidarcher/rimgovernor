using System.Diagnostics;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Rounder observes and journals needs under Player's existing gate.
/// It neither acquires authority nor creates methods or game orders.
/// </summary>
public sealed partial class Rounder
{
    /// <summary>Whether the Rounder was composed with its capabilities; a bare Rounder knows no Needs.</summary>
    private bool _composed;

    /// <summary>The migrated families' Needs the runtime composes (#3280); the review reads their facts as it read their Concerns'.</summary>
    internal IReadOnlySet<NeedId> Needs = new HashSet<NeedId>();

    /// <summary>The composed Needs as the review's facts carry them: known whenever the capabilities are.</summary>
    internal Fact<EquatableArray<NeedId>> AvailableNeeds =>
        _composed ? Known<EquatableArray<NeedId>>([.. Needs.OrderBy(n => n.Value, StringComparer.Ordinal)]) : Unknown<EquatableArray<NeedId>>();
    internal Player Player;
    internal IRoundsSource Native;
    internal IClock Clock;
    internal RoundsPolicy Policy;

    /// <summary>
    /// The native surface of the resource planner, set when it is composed: the Round's resource supply plan
    /// reads mine sources and bench bills through it. Null plans only the census kinds (chop, harvest, hunt).
    /// </summary>
    internal FacadeNativeClient? ResourceNative;

    internal TimeSpan MaxAge;

    /// <summary>The undraft sweep's intents; null never undrafts.</summary>
    internal IActionsWriter? Undraft;

    /// <summary>The latest review reading for the planners of the same tick; see roundsCensus.</summary>
    internal RoundsCensusStore Census = new();

    /// <summary>
    /// Receives each review's decoded sections; the scheduler that steps this reviewer sets it, a standalone
    /// reviewer files nowhere.
    /// </summary>
    internal FactStore? Store;

    /// <summary>The latest review's derived resource demand (derived state, empty until the first review after a restart).</summary>
    internal ReviewMemory<DerivedDemand> Demand = new(new());

    /// <summary>The per-pawn outfit filter and insulation floor the latest review with a coupled selection chose (GearChoices; derived state, none until the first such review after a restart).</summary>
    internal ReviewMemory<EquatableArray<GearChoice>> GearChoices = new([]);

    /// <summary>
    /// The latest known coupled comparison's planned-room selection (#3278; derived state, empty until the first comparison
    /// after a restart): the room reconcile holds an assessed unstarted room it did not select and places only the sites it did.
    /// </summary>
    internal ReviewMemory<PlannedSelection> RoomSelection = new(new());

    /// <summary>Since when each room has had unfinished blueprints or frames; see noteStall.</summary>
    internal Dictionary<string, RoomStall> RoomStalls = [];

    /// <summary>The quest skips already logged.</summary>
    internal HashSet<QuestSkipKey> SkipsLogged = [];

    /// <summary>The last tech tier logged: the OTLP log ring records a change once, not every review.</summary>
    internal Fact<TechTier> TechTierLogged;

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

    /// <summary>The asynchronous hunt roster estimates (#3130); null means no roster has an estimate.</summary>
    internal IHuntEstimates? HuntEstimates;

    /// <summary>The tick of the last layout survey this process read; see reviewLayoutPlan.</summary>
    internal Tick PlanChecked;

    internal bool PlanSurveyed;

    /// <summary>The tier and finished research the saved plan was last grown for, in memory only: a restart replans once.</summary>
    internal string PlanGrownFor = "";

    internal int PlanPawns;

    /// <summary>The last replan's survey and inputs; an hourly check that finds them unchanged skips the replan.</summary>
    internal LayoutInputs PlanInputs = LayoutInputs.Empty;

    /// <summary>The inputs of the last station chain run.</summary>
    internal LayoutInputs StationInputs = LayoutInputs.Empty;

    /// <summary>The advice of the last station chain run.</summary>
    internal StationAdvice StationLast = new();

    /// <summary>Whether the last station chain run made advice.</summary>
    internal bool StationHeld;

    /// <summary>The facts of that run, which the replan tiles (policy.ReplanLayout).</summary>
    internal StationFacts StationFacts = new();

    /// <summary>An invalid saved layout plan is logged once.</summary>
    internal bool LayoutInvalidLogged;

    /// <summary>Draws the layout plan as a native overlay; the overlay fields record the last draw.</summary>
    internal bool LayoutOverlay;

    /// <summary>Whether the review reads the clearance census (RoundsCapabilities.ClearanceCensus).</summary>
    internal bool ClearanceCensus;

    /// <summary>Whether the review reads the jail shell definitions (RoundsCapabilities.JailShell).</summary>
    internal bool JailShell;

    internal string OverlayKey = "";
    internal Tick OverlayDrawn;
    internal bool OverlayCleared;

    /// <summary>The defense tier nodes the defense round last cut (#2973), drawn with the layout plan.</summary>
    internal readonly AtomicPointer<TierOverlay> Tiers = new();

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
        return catalog is null ? new ItemFacts() : catalog.ItemFacts();
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
    /// (Stonecutting)`. An unknown tier (no research census) is not a change.
    /// </summary>
    internal void LogTechTier(ColonyProjection projection, CancellationToken ct)
    {
        if (!projection.TechTier.TryGetValue(out var tier))
        {
            return;
        }
        if (TechTierLogged.TryGetValue(out var last) && last == tier)
        {
            return;
        }
        TechTierLogged = projection.TechTier;
        var evidence = TechTierRules.TechTierEvidence(RoundsReads.FinishedResearch(projection.Facts.Research), projection.PlayerTechLevel);
        var message = "tech tier " + tier;
        if (evidence != "")
        {
            message += " (" + evidence + ")";
        }
        ClockEvents.Event("layout", "tech_tier", message, ("tier", tier.ToString()), ("evidence", evidence));
    }

    /// <summary>Records each quest offer the colony does not accept (policy.QuestSkips) once per quest and reason.</summary>
    internal void LogQuestSkips(RoundsFacts facts, CancellationToken ct)
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
    /// (policy.RoundsPolicy.Seasonal): the same targets DetectRounds measures the latches against.
    /// </summary>
    internal RoundsPolicy Seasonal(RoundsFacts facts) => Policy.Seasonal(facts.Calendar, facts.DisasterConditions);

    /// <summary>
    /// Puts the review frame's colony facts, pawn and things tables into the colony mirror, which recordings and
    /// planners serve. A section the frame lacks keeps the table the mirror holds, and its tick.
    /// </summary>
    internal void PublishFrame(Identity expected, RoundsFrame frame)
    {
        if (Store is null)
        {
            Census.RememberColony(null);
            return;
        }
        expected.NativeGeneration.TryGetValue(out var generation);
        var scope = new FactScope(expected.Load.Value, expected.Map.Value, generation.Value);
        ClockMirror.PublishPawns(Store, scope, frame.Tables.Pawns, frame.Context?.Tick ?? 0);
        ClockMirror.PublishThings(Store, scope, frame.Tables.Things, frame.Context?.Tick ?? 0);
        if (frame.Colony is not null)
        {
            Census.RememberColony(ColonyMirror.PublishColony(Store, scope, frame.Colony));
        }
    }

    /// <summary>
    /// Constructs a reviewer. Throws <see cref="ControlException"/> when the arguments cannot review.
    /// </summary>
    public Rounder(Player player, IRoundsSource native, IClock clock, RoundsPolicy thresholds, TimeSpan maxAge, params RoundsCapabilities[] capabilities)
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
        if (native is not IRoundsWorkBenchSource)
        {
            throw new ControlException("NewRounder: native lacks the bench census read");
        }
        var reviewerOverlay = false;
        IActionsWriter? undraftWriter = null;
        var clearanceCensus = false;
        var jailShell = false;
        if (capabilities.Length > 1)
        {
            throw new ControlException("NewRounder: len(capabilities) > 1");
        }
        if (capabilities.Length == 1)
        {
            reviewerOverlay = capabilities[0].LayoutOverlay;
            undraftWriter = capabilities[0].Undraft;
            clearanceCensus = capabilities[0].ClearanceCensus;
            jailShell = capabilities[0].JailShell;
            Needs = capabilities[0].Needs.ToHashSet();
            _composed = true;
        }
        Player = player;
        Native = native;
        Clock = clock;
        Policy = thresholds;
        MaxAge = maxAge;
        LayoutOverlay = reviewerOverlay;
        Undraft = undraftWriter;
        ClearanceCensus = clearanceCensus;
        JailShell = jailShell;
    }

    public async Task<RoundsResult> StepAsync(CancellationToken ct)
    {
        using var entry = await Player.EnterAsync("rounds_review", false, ct).ConfigureAwait(false);
        return await StepAsync(new StepArbiter(), false, entry.Call, entry.Epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Also usable by a scheduler already holding the player gate; <paramref name="isPartial"/> is set when only
    /// a subset of the planners follows the review.
    /// </summary>
    internal async Task<RoundsResult> StepAsync(StepArbiter arbiter, bool isPartial, CancellationToken ct, CancellationToken epoch)
    {
        var timer = Stopwatch.StartNew();
        var span = Tel.StartSpan("buildingruntime", "rounds.review");
        Exception? failure = null;
        try
        {
            return await ReviewStepAsync(arbiter, isPartial, ct, epoch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            Tel.Decide(RoundsStepDecision(failure, timer.Elapsed, isPartial));
            Tel.EndSpan(span, failure);
        }
    }

    internal async Task<RoundsResult> ReviewStepAsync(StepArbiter arbiter, bool isPartial, CancellationToken ct, CancellationToken epoch)
    {
        var p = Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return await p.StopRoundsAsync(ct).ConfigureAwait(false);
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native.Value == 0");
        }
        var previous = await p.Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(Native, ct).ConfigureAwait(false);
        if (expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map || !expected.NativeGeneration.TryGetValue(out var native) || native != state.Snapshot.Native)
        {
            throw new ControlException("step: expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.S");
        }
        var plans = await p.Journal.LoadPlansAsync(ct).ConfigureAwait(false);
        var playerPlans = await p.Journal.PlayerPlansAsync(Player.PlayerWorld(state.Snapshot), ct).ConfigureAwait(false);
        var definitions = RoundsProjectBill.RoundsProjectDefinitions(plans, state.Snapshot, playerPlans);
        var readDefinitions = definitions;
        if (Needs.Contains(PolicyApi.CookingNeed))
        {
            readDefinitions = [.. readDefinitions, "NutrientPasteDispenser", "Hopper"];
        }
        if (RoomsEnabled())
        {
            readDefinitions = [.. readDefinitions, .. RoomQualityRules.RoomBeautyDefinitions()];
        }
        if (Needs.Contains(RoomsFamily.Flooring))
        {
            // The traffic tier prices its floor in the review.
            readDefinitions = [.. readDefinitions, .. Policy.Flooring.Floors];
        }
        if (Needs.Contains(AdminFamily.Burial))
        {
            readDefinitions = [.. readDefinitions, .. RoundsBurialFacts.BurialDefinitions];
        }
        if (Needs.Contains(MilitaryFamily.Training))
        {
            readDefinitions = [.. readDefinitions, .. MilitaryRounds.TrainingDefinitions];
        }
        if (Needs.Contains(PolicyApi.CookingNeed) || Needs.Contains(PolicyApi.RefrigerationNeed) || JailShell)
        {
            // The planned kitchen, freezer and jail shells; the jail bed is a furniture rule's, read with every catalog.
            readDefinitions = [.. readDefinitions, "Wall", "Door"];
        }
        if (Needs.Contains(AdminFamily.Stockpiles))
        {
            // The warehouse shelf upgrade needs the Shelf's availability.
            readDefinitions = [.. readDefinitions, StorageRules.ShelfDefinition];
        }
        var claims = await p.Journal.ConstructionClaimsAsync(state.Snapshot, expected.Tick, ct).ConfigureAwait(false);
        using var window = ClockPlanning.StandaloneWindow(Native);
        var reading = await ObserveRoundsAsync(Native, expected, RoomsEnabled(), claims, readDefinitions, ct).ConfigureAwait(false);
        PublishFrame(expected, reading.Frame);
        if (await p.ExpeditionHandoffAsync(state, reading, ct, epoch).ConfigureAwait(false))
        {
            return await p.StopRoundsAsync(ct).ConfigureAwait(false);
        }
        await BreakDispatch.ReleaseBreakWorkAsync(p.Journal, state.Snapshot, reading.Emergency, plans, ct).ConfigureAwait(false);
        LogTechTier(reading.Projection, ct);
        LogQuestSkips(reading.Projection.Facts, ct);
        var foodProjection = reading.Projection;
        PlanFood(ref foodProjection);
        reading = reading.WithProjection(foodProjection);
        reading = reading.WithFacts(rf => rf with { ConstructionClaims = claims });
        reading = reading.WithFacts(rf => rf with
        {
            WorkAdmission = new()
            {
                Buildings = reading.Catalog is { } buildingCatalog ? Known(buildingCatalog.WorkBuildingCosts()) : default,
                Recipes = reading.Catalog is { } batchCatalog ? Known(batchCatalog.ProductionRecipeInputs(
                reading.Frame.Tables.Buildings.Table.Values().Select(b => b.Building?.DefName ?? ""))) : default,
                Bills = ConstructionReads.ProductionBatchStates(reading.Frame.Bills),
            }
        });
        var batches = await p.Journal.ProductionBatchesAsync(state.Snapshot, reading.Projection.Identity.Tick, ct).ConfigureAwait(false);
        var workActions = p.Journal.WorkActions(state.Snapshot, ct);
        reading = reading.WithFacts(rf => WorkRules.ReviewWorkResources(rf, batches, workActions));
        reading = reading.WithColonySection(c => c with { Facts = c.Facts with { ConstructionClaims = reading.Projection.Facts.ConstructionClaims } });
        ReviewRoyalty(state.Snapshot, reading, ct);
        // A creepjoiner held apart owes the isolation room, so the census is read before the child rooms and the layout.
        if (CreepJoinerIsolation)
        {
            var creepRecord = await p.Journal.LoadCreepJoinersAsync(Player.PlayerWorld(state.Snapshot), ct).ConfigureAwait(false);
            reading = reading.WithProjection(reading.Projection with { Isolation = RoundsCreepJoiner.CreepJoinerIsolation(reading.Projection, reading.Frame, reading.Catalog, creepRecord) });
        }
        reading = ReviewChildRooms(reading);
        reading = reading.WithProjection(await ReviewLayoutPlanAsync(state.Snapshot, reading.Projection, ct).ConfigureAwait(false));
        reading = reading.WithColonySection(c => c with { LayoutPlan = reading.Projection.LayoutPlan });
        if (reading.Projection.LayoutPlan.TryGetValue(out var layoutPlan))
        {
            if (reading.Projection.Rooms.TryGetValue(out var rooms))
            {
                reading = reading.WithProjection(reading.Projection with { Rooms = Known(RoomsRules.MarkEnemyDoors(rooms, layoutPlan.KillboxCells())) });
            }
        }
        Census.RememberLayout(reading.Projection.Identity, reading.Projection.LayoutPlan);
        reading = reading.WithFacts(rf => rf with { TitleClaimQuests = [.. RoundsSleepingUpkeepPlanner.TitleClaimQuests(reading.Projection)] });
        reading = reading.WithFacts(rf => rf with { BedroomsOwed = RoundsSleepingUpkeepPlanner.BedroomsOwedOf(reading.Projection) });
        reading = reading.WithFacts(rf => rf with { ButcherTableAffordable = RoundsBuildingPlanner.ButcherTableAffordable(reading.Projection) });
        reading = reading.WithFacts(rf => rf with { WarmRooms = RoundsBurialFacts.WarmCoolingRooms(reading.Projection) });
        reading = reading.WithFacts(rf => rf with { MealClosetOwed = RoundsBurialFacts.MealClosetOwed(reading.Projection) });
        reading = reading.WithFacts(rf => rf with { CampfireRetireOwed = RoundsBuildingPlanner.CampfireRetireOwed(reading.Projection) });
        var vCraftingSpotRetireOwed = await CraftingSpotRetireOwedAsync(state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { CraftingSpotRetireOwed = vCraftingSpotRetireOwed });
        reading = reading.WithFacts(rf => rf with { TemperatureOwed = RoundsBuildingPlanner.TemperatureOwed(reading.Projection) });
        var targets = await RoundsArmoryShells.ShellTargetsAsync(Native, p.Journal, state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { ShellsShort = PolicyApi.ShellsShort(targets, reading.Projection.Resources) });
        if (Needs.Contains(ShelterFamily.SafeArea))
        {
            reading = reading.WithFacts(rf => rf with { SafeAreaOwed = SafeArea.Review(RoundsStockpilePlanner.StockpileWorld(state.Snapshot), reading.Projection) });
        }
        if (Needs.Contains(RoomsFamily.Art))
        {
            reading = reading.WithFacts(rf => rf with { SculptureRoomsOwed = RoundsSleepingUpkeepPlanner.SculptureRoomsOwedOf(reading.Projection) });
        }
        reading = reading.WithFacts(rf => rf with { ExportProducts = [.. Exports.Products(state.Snapshot)] });
        var vSaleArt = await RoundsSleepingUpkeepPlanner.ReviewSaleArtAsync(Native, Boundary.Identity(state.Snapshot), reading.Projection, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { SaleArt = vSaleArt });
        var (parts, benches) = await SurgeryPartDemandAsync(Native, Boundary.Identity(state.Snapshot), reading.Projection.Facts.MedicalPawns, reading.Projection.SurgeryContext(), ct).ConfigureAwait(false);
        if (parts.Count > 0)
        {
            reading = reading.WithFacts(rf => rf with { FabricableParts = SurgeryRules.FabricableParts(benches) });
        }
        CensusStockpileZones(state.Snapshot, reading.Projection, ct);
        // The review's shelf candidates become planned sites below.
        var shelfCandidates = await ReviewStockpilesAsync(state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
        var vResourceConsumption = await ResourceConsumptionAsync(state.Snapshot, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { ResourceConsumption = vResourceConsumption });
        var vResourceSurfaceOre = await ResourceSurfaceOreAsync(state.Snapshot, reading.Projection.Facts.Items, reading.Projection.Facts.ResourceConsumption, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { ResourceSurfaceOre = vResourceSurfaceOre });
        if (reading.Projection.Policies.TryGetValue(out var policies))
        {
            var entries = new List<OwnedDrugPolicy>(policies.Drug.Count);
            foreach (var e in policies.Drug)
            {
                entries.Add(new OwnedDrugPolicy(e.ID, e.Label, [.. e.Pawns], [.. e.Drugs]));
            }
            reading = reading.WithFacts(rf => rf with { DrugUsers = PolicyApi.DrugUsers(reading.Projection.Facts.Items, entries) });
        }
        reading = reading.WithProjection(ReviewMeals(reading.Projection));
        var reserveProjection = reading.Projection;
        ReviewReserve(ref reserveProjection);
        reading = reading.WithProjection(reserveProjection);
        reading = reading.WithProjection(ReviewBabyFeeding(reading.Projection));
        if (Needs.Contains(RoomsFamily.Flooring))
        {
            reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Flooring = RoundsBuildingPlanner.TrafficFlooringFacts(reading.Projection, Policy.Flooring) } });
            reading = reading.WithColonySection(c => c with { Facts = c.Facts with { Upkeep = c.Facts.Upkeep with { Flooring = reading.Projection.Facts.Upkeep.Flooring } } });
        }
        reading = reading.WithFacts(rf => rf with
        {
            PlannedConstruction = PlannedConstructionOf(reading.Projection, Policy.Flooring),
            PlannedShelters = reading.Projection.LayoutPlan.TryGetValue(out var layout) ? EquatableArray.From(layout.Rooms.Where(r => !r.Outdoor)) : []
        });
        var vPlannedSites = await PlannedSitesAsync(state.Snapshot, reading.Projection, Policy.Flooring, shelfCandidates, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { PlannedSites = vPlannedSites });
        var (vPlannedThermal, vRoomPredictions) = await PlannedShelterReadsAsync(state.Snapshot, reading.Projection, reading.Projection.Facts.PlannedShelters, vPlannedSites, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { PlannedThermal = vPlannedThermal, PlannedRoomPredictions = vRoomPredictions });
        if (Firebreak is not null)
        {
            // The ring plan's dwell clock advances every review; the layout family reads the work it leaves.
            _ = await Firebreak.ReviewAsync(Boundary.Identity(state.Snapshot), state.Snapshot, reading.Projection, FirebreakPlan.FirebreakBusy(plans, state.Snapshot, playerPlans), ct).ConfigureAwait(false);
        }
        Fact<EquatableArray<PawnId>> psylinkCandidates = default;
        if (Needs.Contains(RoyaltyFamily.Psylink))
        {
            psylinkCandidates = PsylinkRules.PsylinkCandidates(reading.Projection.Royalty, reading.Projection.WorkPawns);
        }
        if (reading.Projection.Facts.FoodPlan.TryGetValue(out var foodPlan))
        {
            reading = reading.WithFacts(rf => rf with { AnimalUpkeep = rf.AnimalUpkeep with { Forecast = Known(foodPlan.Forecast) } });
        }
        reading = reading.WithColonySection(c => c with { Facts = c.Facts with { FoodPlan = reading.Projection.Facts.FoodPlan, FoodReserve = reading.Projection.Facts.FoodReserve } });
        Census.Retain(reading, RoomsEnabled(), claims);
        var filedScope = new FactScope(expected.Load.Value, expected.Map.Value, native.Value);
        reading.Sections.File(Store, filedScope);
        // A served native section may have gained a fresh derived food review.
        if (Store is not null && reading.Sections.Colony is { Source: not "" } colonyHeld)
        {
            FactStore.Put(Store, filedScope, FactSection.Colony, colonyHeld);
        }
        var emergency = EmergencySnapshot.New(state.Snapshot, expected.Tick, reading.Emergency);
        var (hostiles, criticalPatients) = PolicyApi.EmergencyNeeds(emergency, state.Snapshot, expected.Tick);
        reading = reading.WithFacts(rf => rf with { Hostiles = hostiles, CriticalPatients = criticalPatients });
        reading = reading.WithFacts(rf => rf with { QuestColonyCalm = QuestCapacity.QuestCalmColony(reading.Projection.Facts, PolicyApi.EvaluateEmergency(emergency, state.Snapshot, expected.Tick), emergency.PodsPending) });
        reading = reading.WithFacts(rf => rf with { DangerSeeds = SafeAreaMemory.DangerSeedFact(reading.Projection.Facts.Hostiles, reading.Emergency.Threats) });
        reading.Projection.Facts.DangerSeeds.TryGetValue(out var seeds);
        reading = reading.WithFacts(rf => rf with { DangerWindow = SafeArea.DangerWindow(RoundsStockpilePlanner.StockpileWorld(state.Snapshot), reading.Projection.Facts.Hostiles, seeds, expected.Tick) });
        reading = reading.WithFacts(rf => rf with { DangerHaulers = ShelterRules.DangerHaulers(reading.Projection.WorkPawns) });
        reading = reading.WithFacts(rf => rf with { CoupledWorkActive = plans.Any(plan => !plan.Retired
            && plan.SupersededBy.IsEmpty
            && plan.Progress.Any(a => a.Action.PawnActivities is { Tasks.Count: > 0 } batch
                && batch.Orders.Any(o => !o.Release && o.Method.StartsWith("coupled-", StringComparison.Ordinal))
                && a.View.Stage is not (Stage.Unsuccessful or Stage.Cancelled))) });
        reading = reading.WithFacts(rf => rf with { Dash = PolicyApi.ReviewDash(previous.Latches.Dash, reading.Projection.Facts, reading.Projection.WorkPawns, expected.Tick, PolicyApi.PublishedHypothermiaTuning) });
        reading = reading.WithFacts(rf => rf with { UrgentPatients = PolicyApi.UrgentPatients(emergency, state.Snapshot, expected.Tick) });
        var (amputationCritical, amputationUrgent) = PolicyApi.AmputationNeeds(emergency, reading.Projection.Facts.MedicalPawns, reading.Projection.Facts.CriticalPatients, reading.Projection.Facts.UrgentPatients);
        reading = reading.WithFacts(rf => rf with { CriticalPatients = amputationCritical, UrgentPatients = amputationUrgent });
        // The admin/work-assignments Need is owed while a drafted colonist no live plan needs waits for the undraft sweep. A draft
        // its plan still holds is working, not stranded.
        var needed = await DraftNeeds.PlannedDraftsAsync(p.Journal, ct).ConfigureAwait(false);
        var stray = DraftNeeds.IdleDrafts(state, reading.Emergency, reading.Frame.Pawns, needed);
        reading = reading.WithFacts(rf => rf with { CleanupPawns = Known(stray.Count > 0) });
        reading = reading.WithFacts(rf => rf with { HostilityOwed = PolicyApi.HostilityOwed(RoundsWorkPlanner.HostilityPawns(reading.Projection.WorkPawns), reading.Emergency.Threats) });
        reading = reading.WithFacts(rf => rf with { MedicalCareOwed = MedicalRules.MedicalCareOwed(reading.Projection.Facts, Policy.MedicalReserve) });
        reading = reading.WithFacts(rf => rf with { NamesOwed = PawnRules.NamesOwed(reading.Projection.Facts.OwnedNames) });
        reading.Projection.ApplyFieldBudget(Seasonal(reading.Projection.Facts).FoodTargetDays);
        // The workshop ladder's recorded research rung is the derived economy/research target; it is journal
        // evidence, not a native read, so a review costs no extra call for it.
        NoteEquipment(state.Snapshot, reading.Projection.Facts);
        var needs = RoundsProjectBill.RoundsResearchNeeds(p.Journal, Policy, reading.Projection.Facts.Items, state.Snapshot, EquipmentOwed(state.Snapshot), ct);
        needs = CensusResearchNeeds(needs);
        reading = reading.WithFacts(rf => rf with { ResearchNeeds = [.. needs] });
        var medicine = MedicalRules.ReviewMedicalReserve(reading.Projection.Facts.MedicalReserve, previous.Snapshot == state.Snapshot && previous.Latches.MedicalReserve, Policy.MedicalReserve);
        reading = reading.WithFacts(rf => rf with { MedicineCarryOwed = MedicineCarryRules.MedicineCarryOwed(reading.Projection.WorkPawns, medicine) });
        // A prisoner surgery blocked only by the herbal care limit.
        reading = reading.WithFacts(rf => rf with { ResourceNeeds = PrisonerMedicineRules.PrisonerHerbalNeeds(reading.Projection.Facts.ResourceNeeds, reading.Projection.Facts, SilverGapRules.RoundsSilverShort(reading.Projection.Facts, Policy, medicine.Active)) });
        // A willing colonist's psylink neuroformer, bought or made by the resource ladder.
        reading = reading.WithFacts(rf => rf with { ResourceNeeds = PsylinkRules.NeuroformerNeeds(reading.Projection.Facts.ResourceNeeds, reading.Projection.Royalty, psylinkCandidates) });
        // The work goal's coverage reads the demand the last review derived: the value the work planner dispatches on.
        var resourceTargets = ResourceTargets(state.Snapshot);
        if (reading.Projection.WorkPawns.TryGetValue(out var pawns))
        {
            reading = reading.WithFacts(rf => rf with { Labor = PolicyApi.RoundsLabor(pawns) });
            reading = reading.WithFacts(rf => rf with { WorkProfiles = Known<EquatableArray<PawnProfile>>([.. PawnRules.Profiles(pawns)]) });
            var requiredFact = RoundsProjectBill.RoundsProjectWork(definitions, reading.Projection.Definitions);
            var known = requiredFact.TryGetValue(out var required);
            if (known)
            {
                // Bench work (open bills, a deficit's standing benches) counts toward coverage here so the work
                // goal assesses a deficit the planner then covers; a failed census leaves coverage unknown.
                var nativeBench = (IRoundsWorkBenchSource)Native;
                var benchSource = BenchSource(nativeBench, expected, true);
                var recovered = ResearchRules.ResourceTargetNeed(resourceTargets, reading.Projection.Facts.Resources).Recovered;
                var deficitKnown = recovered.TryGetValue(out var deficit);
                var deficitTargets = RoundsProjectBill.RoundsDeficitTargets(resourceTargets, deficitKnown && !deficit, reading.Projection.Facts.Gear);
                Fact<IReadOnlyList<WorkRequirement>> benchWork = default;
                try
                {
                    benchWork = await RoundsProjectBill.RoundsBenchWorkAsync(benchSource, state.Snapshot, plans, playerPlans, deficitTargets, deficitTargets.Count > 0, reading.Projection.Facts.Items.Wort, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Go ignored this error: a failed bench census leaves coverage unknown.
                }
                known = benchWork.TryGetValue(out var rows);
                required = RoundsProjectBill.MergeWorkRequirements([.. required ?? []], rows ?? []);
                required = RoundsProjectBill.MergeWorkRequirements([.. required], RoundsProjectBill.RoundsResearchWork(needs, reading.Projection.Facts.Research));
                required = RoundsProjectBill.MergeWorkRequirements([.. required], RoundsFieldPlanner.FishingWork(reading.Projection));
                var studyKnown = RoundsFieldPlanner.StudyWork(reading.Projection, ct).TryGetValue(out var study);
                required = RoundsProjectBill.MergeWorkRequirements([.. required], study);
                known = known && studyKnown;
            }
            if (known)
            {
                var demand = RoundsDisease.RoundsDiseaseDemand(reading.Projection, definitions, previous, state.Snapshot);
                try
                {
                    var work = WorkRules.PlanWork(pawns, required!, demand);
                    reading = reading.WithFacts(rf => rf with { WorkCoverage = work.Matches });
                    if (PolicyApi.ConstructionSkillChoices(reading.Projection.Facts.CurrentConstruction, reading.Projection.WorkPawns).Count > 0)
                    {
                        reading = reading.WithFacts(rf => rf with { WorkCoverage = Known(false) });
                    }
                    if (work.Capacity.IsKnown)
                    {
                        reading = reading.WithFacts(rf => rf with { WorkRoster = Known<EquatableArray<WorkCoverage>>([.. work.Coverage]) });
                        reading = reading.WithFacts(rf => rf with { QuestSparePawns = QuestCapacity.QuestSpareColonists(reading.Projection.WorkPawns, reading.Projection.Facts.MoodPawns, work) });
                        reading = reading.WithFacts(rf => rf with { QuestHomeFloor = QuestCapacity.QuestHomeFloor(reading.Projection.WorkPawns, reading.Projection.Facts.MoodPawns, work) });
                        reading = reading.WithFacts(rf => rf with { QuestDepartureWork = Known<EquatableArray<PawnWorkAssignment>>([.. work.Assignments]) });
                        reading = reading.WithFacts(rf => rf with { WorkDecaying = Known<EquatableArray<DecayingSkill>>([.. work.Decaying]) });
                        reading = reading.WithFacts(rf => rf with { WorkHelp = work.Help });
                    }
                }
                catch (DomainException)
                {
                    // Go: PlanWork's error leaves the coverage facts as the review set them.
                }
            }
        }
        reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Rooms = reading.Projection.Rooms } });
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
            var ground = PlannedGround(reading.Projection);
            var clearance = await ClearanceReads.ObserveClearanceCensusOnGround(clearanceSource, expected, true, ground, ct).ConfigureAwait(false);
            if (clearance.TryGetValue(out var census))
            {
                var (others, player) = PolicyApi.SplitGroundRows(census.Targets);
                reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Clearance = Known<EquatableArray<ClearanceTarget>>([.. others]) } });
                reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { RoomObstructions = RecoveryRules.RoomObstructionIDs(others, ground) } });
                reading = reading.WithProjection(DropClearedRetiredGround(state.Snapshot, reading.Projection, player, census.Floors, ct));
                reading = reading.WithColonySection(c => c with { LayoutPlan = reading.Projection.LayoutPlan });
            }
        }
        reading = reading.WithFacts(rf => rf with { AvailableNeeds = AvailableNeeds });
        // The admin/work-ledger Need reads the diff from the ledger's memory (LedgerOwedIn).
        var ledgerOwed = await ReviewLedgerAsync(state.Snapshot, expected, reading.Projection, ct).ConfigureAwait(false);
        lock (Ledger.Mu)
        {
            Ledger.Owed = ledgerOwed;
        }
        // The Grafana ledger dashboard reads a derived copy of the review's view.
        try
        {
            await p.Journal.ReplaceLedgerRowsAsync(reading.Projection.Identity.Tick.Value, WorkLedger(ct).Rows(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PlannerReasons.PlannerBookkeepingFailed("ledger_rows", ex);
        }
        // The ledger's declared batches are read after the declaration above.
        var vOpenBills = await OpenBillsAsync(state.Snapshot, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { OpenBills = [.. vOpenBills] });
        var reconsideration = ForecastIncumbentPreparation.PrepareCoupledIncumbents(reading.Projection.Facts, batches, workActions);
        reading = reading.WithFacts(_ => reconsideration.Facts);
        // Acquisition uses the review's completed home staffing and defense facts.
        Census.Retain(reading, RoomsEnabled(), claims);
        await PlanFoodAcquisitionAsync(state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
        var result = await p.Journal.ReviewRoundsAsync(new RoundsRequest { Revision = previous.Revision, Current = state.Snapshot, Tick = reading.Projection.Identity.Tick, Enabled = true, Policy = Policy, Facts = reading.Projection.Facts }, ct).ConfigureAwait(false);
        Demand.Set(result.Review.Snapshot, result.Needs.ResourceDemand);
        TradeMemory.Set(result.Review.Snapshot, result.Needs.Trade);
        WorkAssignments.Set(result.Review.Snapshot, result.Needs.WorkAssignments);
        NoteGearChoices(GearChoices, result.Review.Snapshot, reading.Projection.Facts.CoupledAlternatives, result.Needs.CoupledSelection);
        RoomSelection.Set(result.Review.Snapshot, PlannedSelectionReviewed(RoomSelection.Get(result.Review.Snapshot), reading.Projection, result.Needs.CoupledSelection));
        var taken = AdmitCoupledSelection(result, reading.Projection.Facts, reconsideration.Incumbents, state.Snapshot, ct, epoch);
        AdmitStandby(reading.Projection.Facts, workActions, taken, state.Snapshot, ct, epoch);
        var attrs = new List<(string Key, object? Value)> { ("revision", result.Review.Revision), ("previous_revision", previous.Revision), ("tick", reading.Projection.Identity.Tick.Value) };
        attrs.AddRange(RoundsFoodAttrs(reading.Projection.Facts, Seasonal(reading.Projection.Facts)));
        attrs.Add(("hunt_sources", reading.Projection.Acquisition.TryGetValue(out var offered) ? offered.Count(s => s.Hunt && s.Food) : -1));
        if (reading.Projection.HuntHolds.Count > 0)
        {
            attrs.Add(("hunt_holds", reading.Projection.HuntHolds.Select(h => h.ID + ":" + h.Reason + (h.Detail.Count == 0 ? "" : "(" + string.Join(";", h.Detail) + ")")).ToArray()));
        }
        if (reading.Projection.Facts.ForecastObserved.Coupled?.Hunt is { } huntRead)
        {
            attrs.Add(("hunt_read", $"known={huntRead.Known} hunters={huntRead.Hunters.Count} prey={huntRead.Prey.Count} findings=[{string.Join(";", huntRead.Findings)}]"));
            attrs.Add(("hunt_choice", Census.HuntNote));
            if (HuntEstimates is { } estimates)
            {
                var view = estimates.Current(huntRead, reading.Projection.Identity.Tick.Value);
                attrs.Add(("hunt_estimates", $"answered={view.Estimates.Count} pending={view.Pending.Count} refused=[{string.Join(";", view.Refusals.Select(r => r.Key + "=" + r.Value))}]"));
            }
        }
        ClockEvents.Event("routine", "rounds_review", "rounds ran", [.. attrs]);
        if (Needs.Contains(MilitaryFamily.Equipment))
        {
            LogGearSearch(PolicyApi.ReviewGear(reading.Projection.Facts.Gear).Loadouts.Where(l => l.SearchBudgetExhausted).Select(l => l.Pawn), ct);
        }
        RecordRoundsSnapshot(state.Snapshot, reading.Projection.Identity.Tick, result, reading.Projection, ct);
        await DrawSafetyOverlayAsync(state.Snapshot, reading.Projection, reading.Emergency, ct).ConfigureAwait(false);
        await DrawStockOverlayAsync(state.Snapshot, reading.Projection, result, ct).ConfigureAwait(false);
        await DrawSpotOverlayAsync(state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
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
                Attrs = new Dictionary<string, object?> { ["budget_nodes"] = PolicyApi.GearSearchBudget },
            });
        }
    }
}

/// <summary>
/// Go mutated the review's projection and facts in place; the records are immutable here, so a review step
/// holds one <see cref="RoundsReading"/> and replaces it with an edited copy at each assignment.
/// </summary>
internal static class RoundsReadingEdits
{
    public static RoundsReading WithProjection(this RoundsReading r, ColonyProjection projection) =>
        r with { Reading = r.Reading with { Projection = projection } };

    public static RoundsReading WithFacts(this RoundsReading r, Func<RoundsFacts, RoundsFacts> edit) =>
        r.WithProjection(r.Projection with { Facts = edit(r.Projection.Facts) });

    /// <summary>Edits the colony section the step files (the reading's own copy of the projection); a reading without one is unchanged.</summary>
    public static RoundsReading WithColonySection(this RoundsReading r, Func<ColonyProjection, ColonyProjection> edit) =>
        r.Sections.Colony is { } held ? r with { Sections = r.Sections with { Colony = held with { Value = edit(held.Value) } } } : r;
}

/// <summary>The Rounder's configured capabilities: the composed Needs and the optional native writers.</summary>
public sealed record RoundsCapabilities
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

    /// <summary>Sends the undraft sweep's Draft intents; null never undrafts.</summary>
    public IActionsWriter? Undraft { get; init; }
}

internal readonly record struct QuestSkipKey(QuestId Quest, QuestSkipReason Reason);

partial class Rounder
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
    /// The one planner_step row of a Rounder step: ok when the review ran, failed with the error as an attr
    /// (and reason control_lost for ControlException) when it did not.
    /// </summary>
    internal static Decision RoundsStepDecision(Exception? err, TimeSpan took, bool isPartial)
    {
        var attrs = new Dictionary<string, object?> { ["partial"] = isPartial };
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
    internal static List<(string Key, object? Value)> RoundsFoodAttrs(RoundsFacts f, RoundsPolicy seasonal)
    {
        var attrs = new List<(string Key, object? Value)> { ("food_min_days", seasonal.FoodMinDays), ("food_target_days", seasonal.FoodTargetDays) };
        if (f.FoodReserve.TryGetValue(out var reserve))
        {
            attrs.AddRange([("food_reserve_days", seasonal.FoodReserveDays), ("food_reserve_target", reserve.TargetNutrition),
                ("food_reserve_stock", reserve.StockNutrition), ("food_reserve_deficit", reserve.DeficitNutrition), ("food_reserve_emergency", reserve.Emergency)]);
        }
        if (f.FoodPlan.TryGetValue(out var plan))
        {
            attrs.AddRange([("food_plan", plan.Explain()), ("food_gap_per_day", plan.GapPerDay)]);
        }
        if (f.FoodDays.TryGetValue(out var days))
        {
            attrs.Add(("food_days", days));
        }
        if (f.Calendar.TryGetValue(out var c))
        {
            attrs.AddRange([("season", c.Season), ("day_of_year", c.DayOfYear), ("growing_days_remaining", c.GrowingDaysRemaining), ("growing_days_until", c.GrowingDaysUntil), ("non_growing_days", c.NonGrowingDays)]);
        }
        return attrs;
    }

    /// <summary>Renders the needs that suspended the review's goals for an event attr.</summary>

    /// <summary>
    /// Writes the review's colony snapshot when snapshot.DirEnv names a directory; a failed write is logged,
    /// never the review's error.
    /// </summary>
    internal static void RecordRoundsSnapshot(GenerationSnapshot current, Tick tick, RoundsResult result, ColonyProjection reading, CancellationToken ct)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotRecorder.DirEnv) ?? "";
        if (dir == "")
        {
            return;
        }
        SnapshotLaterReplay.Later(() =>
        {
            if (SnapshotRoundsReplay.FromReview(current, tick, result, reading) is { } recorded)
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
        });
    }

    /// <summary>
    /// The ground of the recorded plan's rooms not yet standing; none while the plan or the room census is
    /// unknown, so nothing of the colony's comes down on a guess. The reviewer's upkeep reading and the
    /// clearance planner share it.
    /// </summary>
    internal static List<Rectangle> PlannedGround(ColonyProjection colony)
    {
        if (!colony.LayoutPlan.TryGetValue(out var plan))
        {
            return [];
        }
        var (ground, groundKnown) = RoundsBuildingPlanner.ColonyGround(colony);
        if (!groundKnown)
        {
            return [];
        }
        // The native census takes rectangles: a non-rect ground is asked for by its bounds.
        return [.. PolicyApi.PlannedGround(plan, ground).Select(g => g.Bounds())];
    }

    /// <summary>
    /// What the whole plan still owes, per room id: the reconciler's owed operations over the plan (PlannedOwedCost), priced from the def catalog with
    /// the stuff the shell chooser takes (ShellSharedStuff) for the ring and the bulk chooser's for pieces; unknown while the plan or the construction census is.
    /// A roofed-set cell still open to the sky is owed a roof (RoofIn) once the ring stands; a roof costs no material.
    /// </summary>
    internal static Fact<IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>> PlannedConstructionOf(ColonyProjection colony, FlooringPolicy flooring)
    {
        if (PlannedOwedOf(colony, flooring) is not { } owed)
        {
            return Unknown<IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>>();
        }
        var wall = RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, PolicyApi.ShellWallDefinition);
        var door = RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, PolicyApi.ShellDoorDefinition);
        var shell = wall is not null && door is not null ? RoundsAnimalContainment.ShellSharedStuff(colony, wall, door) : null;
        IReadOnlyList<Amount>? Price(string name)
        {
            var def = RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, name);
            if (def is null)
            {
                return null;
            }
            if (def.StuffOptions.Count == 0)
            {
                return def.Costs.TryGetValue(out var costs) ? costs : null;
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
        return Known<IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>>(
            PlannedRules.PlannedOwedCost(owed.Rooms, Price, PiecePrice, owed.FloorPrice, ClimateDefs().VentDef, owed.Placed)
                .ToDictionary(e => e.Key, e => (IReadOnlyDictionary<Resource, long>)e.Value));
    }

    /// <summary>
    /// The plan's owed operations per built room, the placed sites the census owes instead and the floor prices. Sited is
    /// what the planned sites build per room (PlannedSitedOps): Rooms with the roof and vents owed once the ring stands.
    /// </summary>
    internal sealed record PlannedOwed(IReadOnlyList<(PlannedRoom Room, IReadOnlyList<Operation> Owed)> Rooms, IReadOnlySet<(string Def, Cell Cell)> Placed,
        Func<string, IReadOnlyList<Amount>?> FloorPrice)
    {
        public IReadOnlyList<(PlannedRoom Room, IReadOnlyList<Operation> Owed)> Sited { get; init; } = Rooms;
    }

    /// <summary>
    /// The reconciler's owed diff over every built room of the plan, read against the colony's standing buildings and floors
    /// (PlannedConstructionOf, PlannedSitesAsync); null while the plan or the construction census is unknown.
    /// </summary>
    internal static PlannedOwed? PlannedOwedOf(ColonyProjection colony, FlooringPolicy flooring)
    {
        if (!colony.LayoutPlan.TryGetValue(out var plan))
        {
            return null;
        }
        var (_, groundKnown) = RoundsBuildingPlanner.ColonyGround(colony);
        if (!groundKnown)
        {
            return null;
        }
        var flooringFacts = RoundsBuildingPlanner.FlooringFactsOf(colony) with { Stock = Unknown<IReadOnlyDictionary<Resource, long>>() };
        IReadOnlyList<Amount>? FloorPrice(string name) =>
            flooringFacts.Definitions.TryGetValue(name, out var floor) && floor.Costs.TryGetValue(out var costs) ? costs : null;
        // The colony's standing buildings and floors stand in for the clearance census: the reconcile matches the
        // template against them, so a piece or floor that stands is not owed. Nothing is packed, so every other
        // template piece is a build.
        var rows = new List<ClearanceTarget>();
        var placed = new HashSet<(string, Cell)>();
        if (colony.Facts.CurrentConstruction.TryGetValue(out var census))
        {
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
        }
        var floors = new List<ClearanceFloor>();
        if (flooringFacts.Observation.TryGetValue(out var observed))
        {
            foreach (var cell in observed.Rooms.SelectMany(r => r.Cells))
            {
                floors.Add(new ClearanceFloor { Cell = cell.Cell, DefName = cell.Pending.Length > 0 ? cell.Pending : cell.Terrain });
            }
        }
        var owed = new List<(PlannedRoom, IReadOnlyList<Operation>)>();
        var sited = new List<(PlannedRoom, IReadOnlyList<Operation>)>();
        foreach (var room in PlannedRules.PlannedBuiltRooms(plan))
        {
            var (input, known) = RoundsBuildingPlanner.RoomRingInput(colony, plan, room);
            if (!known)
            {
                return null;
            }
            // The ring input's open-sky cells stay, so the roof is owed (#3275).
            input = input with
            {
                Cells = null,
                Rows = rows,
                Floors = floors,
                Furniture = plan.Pieces.TryGetValue(room.Id, out var pieces) ? [.. pieces.Select(p => p.Wanted())] : [],
                WantedFloor = WantedFloorRules.WantedFloors(room, [], flooringFacts, flooring),
            };
            var now = ReconcileRules.Reconcile(input).Owed;
            owed.Add((room, now));
            // The roof and vents the ring brings, owed once it stands (#3276): the sites build them, pricing does not.
            var stands = ReconcileRules.Reconcile(input with { Ground = PlannedRules.RingStandsGround(input.Ground, now) }).Owed;
            sited.Add((room, PlannedRules.PlannedSitedOps(now, stands)));
        }
        return new PlannedOwed(owed, placed, FloorPrice) { Sited = sited };
    }

    /// <summary>The stuff a planned piece is built from (the bulk chooser's, "" for a def made from no stuff) and its cost list; null where the def is unavailable.</summary>
    internal static (string Stuff, IReadOnlyList<Amount> Costs)? PlannedPiecePrice(ColonyProjection colony, string name)
    {
        if (RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, name) is not { } def)
        {
            return null;
        }
        if (def.StuffOptions.Count == 0)
        {
            return def.Costs.TryGetValue(out var costs) ? ("", costs) : null;
        }
        var stuff = colony.BulkBuildStuff(name, 1);
        return def.StuffOptions.FirstOrDefault(o => o.Stuff == stuff) is { } option ? (stuff, option.Costs) : null;
    }

    /// <summary>The recorded plan's retired ground with its kept rooms' walls; empty while the plan is unknown.</summary>
    internal static RetiredGround RetiredGroundOfColony(ColonyProjection colony)
    {
        colony.LayoutPlan.TryGetValue(out var plan);
        return PolicyApi.RetiredGroundOf(plan ?? new LayoutPlan());
    }
}

public sealed partial class Rounder
{
    /// <summary>
    /// Records the plan without the retired ground the census found clear; the projection carries the recorded plan.
    /// </summary>
    internal ColonyProjection DropClearedRetiredGround(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<ClearanceTarget> player, IReadOnlyList<ClearanceFloor> floors, CancellationToken ct)
    {
        if (!projection.LayoutPlan.TryGetValue(out var plan) || plan.RetiredGround.Count == 0)
        {
            return projection;
        }
        var done = PolicyApi.RetiredGroundDone(plan, player, floors);
        if (done.Count == 0)
        {
            return projection;
        }
        var next = plan.WithoutRetiredGround(done);
        Player.Journal.RecordLayoutPlan(snapshot, projection.Identity.Tick, next, ct);
        return projection with { LayoutPlan = Known(next) };
    }
}

/// <summary>Go's <c>atomic.Pointer[T]</c>.</summary>
internal sealed class AtomicPointer<T>
    where T : class
{
    private T? _value;

    public T? Load() => Volatile.Read(ref _value);

    public void Store(T? value) => Volatile.Write(ref _value, value);
}
