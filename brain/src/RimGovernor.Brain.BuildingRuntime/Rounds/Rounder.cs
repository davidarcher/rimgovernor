using System.Diagnostics;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Rounder observes and journals needs under Player's existing gate.
/// It neither acquires authority nor creates methods or game orders.
/// </summary>
public sealed partial class Rounder
{
    internal Fact<EquatableArray<ConcernId>> Methods;
    internal Player Player;
    internal IRoundsSource Native;
    internal RimGovernor.Brain.Observation.IClock Clock;
    internal RoundsPolicy Policy;

    /// <summary>
    /// The native surface of the resource planner, set when it is composed: the Round's resource supply plan
    /// reads mine sources and bench bills through it. Null plans only the census kinds (chop, harvest, hunt).
    /// </summary>
    internal IRoundsResourceSource? ResourceNative;

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
    internal ResourceDemandMemory Demand = new();

    /// <summary>The per-world first-seen record of humanlike pawns: derived, in memory only, empty after a restart.</summary>
    internal FirstSeenRecord FirstSeen = new();

    /// <summary>Since when each room has had unfinished blueprints or frames; see noteStall.</summary>
    internal Dictionary<string, RoomStall> RoomStalls = [];

    /// <summary>The quest skips already logged.</summary>
    internal HashSet<QuestSkipKey> SkipsLogged = [];

    /// <summary>The last tech tier logged: the OTLP log ring records a change once, not every review.</summary>
    internal Fact<TechTier> TechTierLogged;

    /// <summary>The realized-consumption ledger of the current load, in memory only.</summary>
    internal ConsumptionMemory Consumption = new();

    /// <summary>MaintainShelter's Safe area memory.</summary>
    internal SafeAreaMemory SafeArea = new();

    /// <summary>MaintainFirebreak's review memory, set when its planner is composed.</summary>
    internal FirebreakMemory? Firebreak;

    /// <summary>ManageCreepJoiners's review memory, set when its planner is composed.</summary>
    internal CreepJoinerMemory? CreepJoiners;

    /// <summary>Set when the mood relief planner is composed: it casts mood psycasts, so the review reads the royalty facts.</summary>
    internal bool MoodCasts;

    /// <summary>
    /// The colony stage of the review the last step loaded: the stage the store holds that step's review to, so
    /// the planners' targets (staged) agree with the review's. Foothold before any review filed one.
    /// </summary>
    internal ColonyStage Stage;

    /// <summary>The last stage record logged; the OTLP log ring records a change once, not every review.</summary>
    internal Fact<ColonyStageRecord> StageLogged;

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
    /// The configured policy with its goal budgets set by the colony stage (policy.StageRoundsPolicy): the
    /// development-project limit, the research ladder's pace and the stall deadline.
    /// </summary>
    internal RoundsPolicy Staged() => PolicyApi.StageRoundsPolicy(Policy, Stage);

    /// <summary>
    /// Records a review's stage in the OTLP log ring and timeline once per change of stage or blocker:
    /// `[routine] colony stage Reserves (settling: production clear 1.0 of 2.0 days)`.
    /// </summary>
    internal void LogColonyStage(RoundsReview review, CancellationToken ct)
    {
        if (review.Stage is null)
        {
            return;
        }
        var stage = review.Stage;
        if (StageLogged.TryGetValue(out var last) && last.Stage == stage.Stage && last.Blocker == stage.Blocker && last.Held == stage.Held)
        {
            return;
        }
        StageLogged = Known(stage);
        var message = "colony stage " + stage.Stage;
        if (stage.Reason != "")
        {
            message += " (" + stage.Blocker + ": " + stage.Reason + ")";
        }
        ClockEvents.Event("routine", "colony_stage", message, ("stage", stage.Stage.ToString()), ("since", (long)stage.Since.Value), ("blocker", stage.Blocker.ToString()), ("reason", stage.Reason), ("held", stage.Held));
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
        var evidence = PolicyApi.TechTierEvidence(ObservationApi.FinishedResearch(projection.Facts.Research), projection.PlayerTechLevel);
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
        foreach (var skip in PolicyApi.QuestSkips(facts))
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
    internal RoundsPolicy Seasonal(RoundsFacts facts) => Staged().Seasonal(facts.Calendar, facts.DisasterConditions);

    /// <summary>
    /// Puts the review frame's colony facts, pawn and things tables into the colony mirror, which recordings and
    /// planners serve. A section the frame lacks keeps the table the mirror holds, and its tick.
    /// </summary>
    internal void PublishFrame(Identity expected, RoundsFrame frame)
    {
        var seenAt = expected with { Tick = new Tick(frame.Context?.Tick ?? 0) };
        FirstSeen.Observe(seenAt, frame.Tables.Pawns);
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
    public Rounder(Player player, IRoundsSource native, RimGovernor.Brain.Observation.IClock clock, RoundsPolicy thresholds, TimeSpan maxAge, params RoundsCapabilities[] capabilities)
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
        var methods = Unknown<EquatableArray<ConcernId>>();
        var reviewerOverlay = false;
        IActionsWriter? undraftWriter = null;
        if (capabilities.Length > 1)
        {
            throw new ControlException("NewRounder: len(capabilities) > 1");
        }
        if (capabilities.Length == 1)
        {
            reviewerOverlay = capabilities[0].LayoutOverlay;
            undraftWriter = capabilities[0].Undraft;
            methods = Known<EquatableArray<ConcernId>>([.. capabilities[0].Methods]);
            PolicyApi.InspectRounds(new RoundsFacts { AvailableMethods = methods }, new RoundsLatches(), thresholds);
        }
        Methods = methods;
        Player = player;
        Native = native;
        Clock = clock;
        Policy = thresholds;
        MaxAge = maxAge;
        LayoutOverlay = reviewerOverlay;
        Undraft = undraftWriter;
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
        Stage = ColonyStage.Foothold;
        if (previous.Stage is not null)
        {
            Stage = previous.Stage.Stage;
        }
        var expected = await StepScopeAsync(Native, ct).ConfigureAwait(false);
        if (expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map || !expected.NativeGeneration.TryGetValue(out var native) || native != state.Snapshot.Native)
        {
            throw new ControlException("step: expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.S");
        }
        var plans = await p.Journal.LoadPlansAsync(ct).ConfigureAwait(false);
        var playerPlans = await p.Journal.PlayerPlansAsync(Player.PlayerWorld(state.Snapshot), ct).ConfigureAwait(false);
        var definitions = RoundsProjectDefinitions(plans, state.Snapshot, playerPlans);
        var readDefinitions = definitions;
        if (MethodEnabled(PolicyApi.EnsureCooking))
        {
            readDefinitions = [.. readDefinitions, "NutrientPasteDispenser", "Hopper"];
        }
        if (RoomsEnabled())
        {
            readDefinitions = [.. readDefinitions, .. PolicyApi.RoomBeautyDefinitions()];
        }
        if (MethodEnabled(PolicyApi.MaintainFlooring))
        {
            // The traffic tier prices its floor in the review.
            readDefinitions = [.. readDefinitions, .. Policy.Flooring.Floors];
        }
        if (MethodEnabled(PolicyApi.MaintainAnimalContainment))
        {
            readDefinitions = [.. readDefinitions, .. HerdDefinitions];
        }
        if (MethodEnabled(PolicyConstants.MaintainIncineration) || MethodEnabled(PolicyApi.MaintainBurial))
        {
            readDefinitions = [.. readDefinitions, .. BurialDefinitions];
        }
        if (MethodEnabled(PolicyApi.MaintainTraining))
        {
            readDefinitions = [.. readDefinitions, .. RoundsTrainingPlanner.TrainingDefinitions];
        }
        if (MethodEnabled(PolicyApi.EnsureCooking) || MethodEnabled(PolicyApi.MaintainRefrigeration) || MethodEnabled(PolicyConstants.MaintainPopulation))
        {
            // The planned kitchen, freezer and jail shells; the jail bed is a furniture rule's, read with every catalog.
            readDefinitions = [.. readDefinitions, "Wall", "Door"];
        }
        if (MethodEnabled(PolicyApi.MaintainStockpiles))
        {
            // The warehouse shelf upgrade needs the Shelf's availability.
            readDefinitions = [.. readDefinitions, PolicyApi.ShelfDefinition];
        }
        var claims = await p.Journal.ConstructionClaimsAsync(state.Snapshot, expected.Tick, ct).ConfigureAwait(false);
        using var window = ClockPlanning.StandaloneWindow(Native);
        var reading = RoomsEnabled()
            ? await ObservationApi.ObserveRoundsRooms(Native, Clock, expected, MaxAge, claims, readDefinitions, ct).ConfigureAwait(false)
            : await ObservationApi.ObserveRoundsOwned(Native, Clock, expected, MaxAge, claims, readDefinitions, ct).ConfigureAwait(false);
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
        reading = reading.WithColonySection(c => c with { Facts = c.Facts with { ConstructionClaims = reading.Projection.Facts.ConstructionClaims } });
        ReviewRoyalty(state.Snapshot, reading, ct);
        // A creepjoiner held apart owes the isolation room, so the census is read before the child rooms and the layout.
        CreepJoinerRecord creepRecord = new();
        if (CreepJoiners is not null)
        {
            creepRecord = CreepJoinerRecordOf(p.Journal, ct);
            reading = reading.WithProjection(reading.Projection with { Isolation = CreepJoinerIsolation(reading.Projection, reading.Frame, reading.Catalog, creepRecord) });
        }
        reading = ReviewChildRooms(reading);
        reading = reading.WithProjection(ReviewStrangers(state.Snapshot, reading.Projection, ct));
        await ReviewLayoutPlanAsync(state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
        reading = reading.WithColonySection(c => c with { LayoutPlan = reading.Projection.LayoutPlan });
        if (reading.Projection.LayoutPlan.TryGetValue(out var layoutPlan))
        {
            if (reading.Projection.Rooms.TryGetValue(out var rooms))
            {
                reading = reading.WithProjection(reading.Projection with { Rooms = Known(PolicyApi.MarkEnemyDoors(rooms, layoutPlan.KillboxCells())) });
            }
        }
        Census.RememberLayout(reading.Projection.Identity, reading.Projection.LayoutPlan);
        reading = reading.WithFacts(rf => rf with { TitleClaimQuests = [.. TitleClaimQuests(reading.Projection)] });
        reading = reading.WithFacts(rf => rf with { BedroomsOwed = BedroomsOwedOf(reading.Projection, Stage) });
        reading = reading.WithFacts(rf => rf with { NoSoil = PolicyApi.NoSoil(reading.Projection.Cells) });
        reading = reading.WithFacts(rf => rf with { BurialOwed = BurialOwed(reading.Projection) });
        reading = reading.WithFacts(rf => rf with { IncinerationOwed = IncinerationOwed(reading.Projection) });
        // The herd furniture is read only when the containment method is on (readDefinitions above); a review without it owes no herd step.
        if (MethodEnabled(PolicyApi.MaintainAnimalContainment))
        {
            reading = reading.WithFacts(rf => rf with { HerdRoomsOwed = HerdRoomsOwed(reading.Projection) });
        }
        reading = reading.WithFacts(rf => rf with { WarmRooms = WarmCoolingRooms(reading.Projection) });
        reading = reading.WithFacts(rf => rf with { PlanLit = RoundsBuildingPlanner.PlanLitRooms(reading.Projection) });
        if (MethodEnabled(PolicyApi.MaintainClimateEnvelope))
        {
            reading = reading.WithFacts(rf => rf with { EnvelopeOwed = EnvelopeOwed(reading.Projection) });
        }
        reading = reading.WithFacts(rf => rf with { MealClosetOwed = MealClosetOwed(reading.Projection) });
        reading = reading.WithFacts(rf => rf with { CampfireRetireOwed = CampfireRetireOwed(reading.Projection) });
        var vCraftingSpotRetireOwed = await CraftingSpotRetireOwedAsync(state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { CraftingSpotRetireOwed = vCraftingSpotRetireOwed });
        reading = reading.WithFacts(rf => rf with { TemperatureOwed = TemperatureOwed(reading.Projection) });
        if (MethodEnabled(PolicyApi.EnsureComfort))
        {
            reading = reading.WithFacts(rf => rf with { PartySpotOwed = PartySpotOwed(previous, reading.Projection, ct) });
        }
        var targets = await ShellTargetsAsync(Native, p.Journal, state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { ShellsShort = PolicyApi.ShellsShort(targets, reading.Projection.Resources) });
        if (MethodEnabled(PolicyApi.MaintainShelter))
        {
            reading = reading.WithFacts(rf => rf with { SafeAreaOwed = SafeArea.Review(StockpileWorld(state.Snapshot), reading.Projection) });
        }
        // The review's PlanSheltering raises RecoverDisasterServices, so it reads the same draft set as the
        // recovery planner: unknown, a threat shelters no colonist and the planner never runs.
        reading = reading.WithFacts(rf => rf with { ShelterCombatants = RoundsRecoveryPlanner.ShelterCombatants(p.Journal, new StoreWorld(state.Snapshot.Colony, state.Snapshot.Load, state.Snapshot.Map), ct) });
        if (MethodEnabled(PolicyApi.MaintainArt))
        {
            reading = reading.WithFacts(rf => rf with { SculptureRoomsOwed = SculptureRoomsOwedOf(reading.Projection, Stage) });
        }
        reading = reading.WithFacts(rf => rf with { ExportProducts = [.. Exports.Products(state.Snapshot)] });
        var vSaleArt = await ReviewSaleArtAsync(Native, Boundary.Identity(state.Snapshot), reading.Projection, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { SaleArt = vSaleArt });
        var (parts, benches) = await SurgeryPartDemandAsync(Native, Boundary.Identity(state.Snapshot), reading.Projection.Facts.MedicalPawns, reading.Projection.SurgeryContext(), ct).ConfigureAwait(false);
        if (parts.Count > 0)
        {
            reading = reading.WithFacts(rf => rf with { FabricableParts = PolicyApi.FabricableParts(benches) });
        }
        CensusStockpileZones(state.Snapshot, reading.Projection, ct);
        await ReviewStockpilesAsync(state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
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
        if (MethodEnabled(PolicyApi.MaintainFlooring))
        {
            reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Flooring = TrafficFlooringFacts(reading.Projection, Policy.Flooring) } });
            reading = reading.WithColonySection(c => c with { Facts = c.Facts with { Upkeep = c.Facts.Upkeep with { Flooring = reading.Projection.Facts.Upkeep.Flooring } } });
        }
        if (Firebreak is not null)
        {
            var vFirebreakOwed = await Firebreak.ReviewAsync(Boundary.Identity(state.Snapshot), state.Snapshot, reading.Projection, Stage, FirebreakBusy(plans, state.Snapshot, playerPlans), ct).ConfigureAwait(false);
            reading = reading.WithFacts(rf => rf with { FirebreakOwed = vFirebreakOwed });
        }
        Fact<EquatableArray<PawnId>> psylinkCandidates = default;
        if (Psylink is not null)
        {
            var (candidates, psylinkOwed) = await Psylink.ReviewAsync(Boundary.Identity(state.Snapshot), state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
            psylinkCandidates = candidates;
            reading = reading.WithFacts(rf => rf with { PsylinkOwed = psylinkOwed });
        }
        if (CreepJoiners is not null)
        {
            reading = reading.WithFacts(rf => rf with { CreepJoinerOwed = CreepJoiners.Review(state.Snapshot, reading.Projection.Identity.Tick, reading.Projection, reading.Frame, reading.Catalog, creepRecord) });
        }
        if (MethodEnabled(PolicyConstants.MaintainIdeoRoles))
        {
            reading = reading.WithFacts(rf => rf with { RolesOwed = PolicyApi.RolesOwed(reading.Projection.Facts.Ideology, reading.Projection.WorkPawns) });
        }
        if (MethodEnabled(PolicyApi.MaintainRituals))
        {
            reading = ReviewRituals(reading, state.Snapshot);
        }
        if (MethodEnabled(PolicyApi.HoldGatherings))
        {
            reading = ReviewGathering(reading, state.Snapshot);
        }
        if (MethodEnabled(PolicyConstants.ImproveIdeoligion))
        {
            ReviewIdeoligion(ref reading, state.Snapshot, ct);
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
        reading = reading.WithFacts(rf => rf with { QuestColonyCalm = PolicyApi.QuestCalmColony(reading.Projection.Facts, PolicyApi.EvaluateEmergency(emergency, state.Snapshot, expected.Tick), emergency.PodsPending) });
        reading = reading.WithFacts(rf => rf with { DangerSeeds = DangerSeedFact(reading.Projection.Facts.Hostiles, reading.Emergency.Threats) });
        reading.Projection.Facts.DangerSeeds.TryGetValue(out var seeds);
        reading = reading.WithFacts(rf => rf with { DangerWindow = SafeArea.DangerWindow(StockpileWorld(state.Snapshot), reading.Projection.Facts.Hostiles, seeds, expected.Tick) });
        reading = reading.WithFacts(rf => rf with { DangerHaulers = PolicyApi.DangerHaulers(reading.Projection.WorkPawns) });
        reading = reading.WithFacts(rf => rf with { Tick = expected.Tick });
        reading = reading.WithFacts(rf => rf with { Dash = PolicyApi.ReviewDash(previous.Latches.Dash, reading.Projection.Facts, reading.Projection.WorkPawns, expected.Tick, PolicyApi.PublishedHypothermiaTuning) });
        reading = reading.WithFacts(rf => rf with { UrgentPatients = PolicyApi.UrgentPatients(emergency, state.Snapshot, expected.Tick) });
        var (amputationCritical, amputationUrgent) = PolicyApi.AmputationNeeds(emergency, reading.Projection.Facts.MedicalPawns, reading.Projection.Facts.CriticalPatients, reading.Projection.Facts.UrgentPatients);
        reading = reading.WithFacts(rf => rf with { CriticalPatients = amputationCritical, UrgentPatients = amputationUrgent });
        // RestoreWorkers stands while a drafted colonist no live plan needs waits for the undraft sweep. A draft
        // its plan still holds is working, not stranded.
        var needed = await DraftNeeds.PlannedDraftsAsync(p.Journal, ct).ConfigureAwait(false);
        var stray = DraftNeeds.IdleDrafts(state, reading.Emergency, reading.Frame.Pawns, needed);
        reading = reading.WithFacts(rf => rf with { CleanupPawns = Known(stray.Count > 0) });
        reading = reading.WithFacts(rf => rf with { HostilityOwed = PolicyApi.HostilityOwed(HostilityPawns(reading.Projection.WorkPawns), reading.Emergency.Threats) });
        reading = reading.WithFacts(rf => rf with { MedicalCareOwed = PolicyApi.MedicalCareOwed(reading.Projection.Facts, Policy.MedicalReserve) });
        reading = reading.WithFacts(rf => rf with { SelfTendOwed = PolicyApi.SelfTendOwed(SelfTendPawns(reading.Projection.WorkPawns)) });
        reading = reading.WithFacts(rf => rf with { NamesOwed = PolicyApi.NamesOwed(reading.Projection.Facts.OwnedNames) });
        reading.Projection.ApplyFieldBudget(Seasonal(reading.Projection.Facts).FoodTargetDays);
        // The workshop ladder's recorded research rung is the derived EnsureResearch target; it is journal
        // evidence, not a native read, so a review costs no extra call for it.
        var needs = RoundsResearchNeeds(p.Journal, Policy, reading.Projection.Facts.Items, state.Snapshot, ct);
        needs = CensusResearchNeeds(needs);
        reading = reading.WithFacts(rf => rf with { ResearchNeeds = [.. needs] });
        reading = reading.WithFacts(rf => rf with { DefensiveLayoutStanding = RoundsDefensiveLayoutStanding(p.Journal, Policy, state.Snapshot, ct) });
        var medicine = PolicyApi.ReviewMedicalReserve(reading.Projection.Facts.MedicalReserve, previous.Snapshot == state.Snapshot && previous.Latches.MedicalReserve, Policy.MedicalReserve);
        reading = reading.WithFacts(rf => rf with { MedicineCarryOwed = PolicyApi.MedicineCarryOwed(reading.Projection.WorkPawns, medicine) });
        reading = reading.WithFacts(rf => rf with { EatTriggerOwed = PolicyApi.EatTriggerOwed(reading.Projection.WorkPawns, EatTriggerColony(reading.Projection.Facts, Seasonal(reading.Projection.Facts))) });
        // A prisoner surgery blocked only by the herbal care limit.
        reading = reading.WithFacts(rf => rf with { ResourceNeeds = PolicyApi.PrisonerHerbalNeeds(reading.Projection.Facts.ResourceNeeds ?? new Dictionary<Resource, long>(), reading.Projection.Facts, PolicyApi.RoundsSilverShort(reading.Projection.Facts, Policy, medicine.Active)) });
        // A willing colonist's psylink neuroformer, bought or made by the resource ladder.
        reading = reading.WithFacts(rf => rf with { ResourceNeeds = PolicyApi.NeuroformerNeeds(reading.Projection.Facts.ResourceNeeds ?? new Dictionary<Resource, long>(), reading.Projection.Royalty, psylinkCandidates) });
        // The work goal's coverage reads the demand the last review derived: the value the work planner dispatches on.
        var resourceTargets = ResourceTargets(state.Snapshot);
        if (reading.Projection.WorkPawns.TryGetValue(out var pawns))
        {
            reading = reading.WithFacts(rf => rf with { Workers = PolicyApi.RoundsWorkers(pawns) });
            reading = reading.WithFacts(rf => rf with { Labor = PolicyApi.RoundsLabor(pawns) });
            reading = reading.WithFacts(rf => rf with { WorkProfiles = Known<EquatableArray<PawnProfile>>([.. PolicyApi.Profiles(pawns)]) });
            var requiredFact = RoundsProjectWork(definitions, reading.Projection.Definitions);
            var known = requiredFact.TryGetValue(out var required);
            if (known)
            {
                // Bench work (open bills, a deficit's standing benches) counts toward coverage here so the work
                // goal assesses a deficit the planner then covers; a failed census leaves coverage unknown.
                var nativeBench = (IRoundsWorkBenchSource)Native;
                var benchSource = BenchSource(nativeBench, expected, true);
                var recovered = PolicyApi.ResourceTargetNeed(resourceTargets, reading.Projection.Facts.Resources).Recovered;
                var deficitKnown = recovered.TryGetValue(out var deficit);
                var deficitTargets = RoundsDeficitTargets(resourceTargets, deficitKnown && !deficit, reading.Projection.Facts.Gear);
                Fact<IReadOnlyList<WorkRequirement>> benchWork = default;
                try
                {
                    benchWork = await RoundsBenchWorkAsync(benchSource, state.Snapshot, plans, playerPlans, deficitTargets, deficitTargets.Count > 0, reading.Projection.Facts.Items.Wort, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Go ignored this error: a failed bench census leaves coverage unknown.
                }
                known = benchWork.TryGetValue(out var rows);
                required = MergeWorkRequirements([.. required ?? []], rows ?? []);
                required = MergeWorkRequirements([.. required], RoundsResearchWork(PolicyApi.ResearchPolicy(Policy, previous.Latches.Soldiers || PolicyApi.GearSoldierPresent(reading.Projection.Facts.Gear), previous.Latches.FoodFirst || PolicyApi.FoodFirstOwed(reading.Projection.Facts)), needs, reading.Projection.Facts.Research));
                required = MergeWorkRequirements([.. required], FishingWork(reading.Projection));
                var studyKnown = StudyWork(reading.Projection, ct).TryGetValue(out var study);
                required = MergeWorkRequirements([.. required], study);
                known = known && studyKnown;
            }
            if (known)
            {
                var demand = RoundsDiseaseDemand(reading.Projection, definitions, previous, state.Snapshot);
                try
                {
                    var work = PolicyApi.PlanWork(pawns, required!, demand);
                    reading = reading.WithFacts(rf => rf with { WorkCoverage = work.Matches });
                    if (PolicyApi.ConstructionSkillChoices(reading.Projection.Facts.CurrentConstruction, reading.Projection.WorkPawns).Count > 0)
                    {
                        reading = reading.WithFacts(rf => rf with { WorkCoverage = Known(false) });
                    }
                    // A timetable behind its role template is a work deficit the same review corrects.
                    if (work.Matches.TryGetValue(out var matches) && matches)
                    {
                        reading.Projection.MeditateAvailable.TryGetValue(out var meditate);
                        foreach (var row in PolicyApi.PlanSchedulesHeld(pawns, reading.Projection.Facts.Comfort, meditate, PolicyApi.HeldOffSleep(reading.Projection.Facts).ToDictionary(id => id, _ => true)).Schedules)
                        {
                            if (!row.Matches)
                            {
                                reading = reading.WithFacts(rf => rf with { WorkCoverage = Known(false) });
                            }
                        }
                    }
                    if (work.Capacity.IsKnown)
                    {
                        reading = reading.WithFacts(rf => rf with { WorkRoster = Known<EquatableArray<WorkCoverage>>([.. work.Coverage]) });
                        reading = reading.WithFacts(rf => rf with { QuestSparePawns = PolicyApi.QuestSpareColonists(reading.Projection.WorkPawns, reading.Projection.Facts.MoodPawns, work) });
                        reading = reading.WithFacts(rf => rf with { QuestHomeFloor = PolicyApi.QuestHomeFloor(reading.Projection.WorkPawns, reading.Projection.Facts.MoodPawns, work) });
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
        if (MethodEnabled(PolicyApi.MaintainMechs))
        {
            if ((await MechGestationAsync(Native, Boundary.Identity(state.Snapshot), reading.Projection, ct).ConfigureAwait(false)) is { } gestation)
            {
                reading = reading.WithFacts(rf => rf with { MechGestationOwed = PolicyApi.MechGestationOwed(gestation) });
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
        if (MethodEnabled(PolicyApi.ClearHomeObstructions))
        {
            if (Native is not IClearanceSource clearanceSource)
            {
                throw new ControlException("step: !ok");
            }
            var ground = PlannedGround(reading.Projection);
            var clearance = await ObservationApi.ObserveClearanceCensusOnGround(clearanceSource, expected, true, ground, ct).ConfigureAwait(false);
            if (clearance.TryGetValue(out var census))
            {
                var (others, player) = PolicyApi.SplitGroundRows(census.Targets);
                reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Clearance = Known<EquatableArray<ClearanceTarget>>([.. others]) } });
                reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { RoomObstructions = PolicyApi.RoomObstructionIDs(others, ground) } });
                reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Chunks = Known(census.Chunks) } });
                reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Ground = Known<EquatableArray<string>>([.. PlannedGroundWork(reading.Projection, player, census.Floors, ClearFloors(reading.Projection))]) } });
                DropClearedRetiredGround(state.Snapshot, reading.Projection, player, census.Floors, ct);
            }
        }
        if (MethodEnabled(PolicyApi.ClearAncientShrine))
        {
            if (Native is not IShrineSource shrineSource)
            {
                throw new ControlException("step: !ok");
            }
            var vShrines = await ObservationApi.ObserveShrines(shrineSource, expected, ct).ConfigureAwait(false);
            reading = reading.WithFacts(rf => rf with { Upkeep = rf.Upkeep with { Shrines = vShrines } });
            // The breach judgement is journalled beside the review so the hold reason and the chosen wall are
            // readable; the planner re-reads before drafting anyone. The reads only happen for a sealed shrine with a breach wall.
            var (shrineHolds, shrinePolicy) = await RoundsShrineHoldsAsync(Native, state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
            reading = reading.WithFacts(rf => rf with { ShrineHolds = [.. shrineHolds], Upkeep = rf.Upkeep with { ShrinePolicy = shrinePolicy } });
        }
        reading = reading.WithFacts(rf => rf with { AvailableMethods = Methods });
        var vLedgerOwed = await ReviewLedgerAsync(state.Snapshot, expected, reading.Projection, ct).ConfigureAwait(false);
        reading = reading.WithFacts(rf => rf with { LedgerOwed = vLedgerOwed });
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
        reading = reading.WithFacts(rf => rf with { UnmetThroughput = [.. LedgerUnmet()] });
        // Acquisition uses the review's completed home staffing and defense facts.
        Census.Retain(reading, RoomsEnabled(), claims);
        await PlanFoodAcquisitionAsync(state.Snapshot, reading.Projection, ct).ConfigureAwait(false);
        var result = await p.Journal.ReviewRoundsAsync(new RoundsRequest { Revision = previous.Revision, Current = state.Snapshot, Tick = reading.Projection.Identity.Tick, Enabled = true, Policy = Policy, Facts = reading.Projection.Facts }, ct).ConfigureAwait(false);
        Demand.Set(result.Review.Snapshot, result.Needs.ResourceDemand);
        var attrs = new List<(string Key, object? Value)> { ("revision", result.Review.Revision), ("previous_revision", previous.Revision), ("tick", reading.Projection.Identity.Tick.Value), ("concerns", result.Standards.Count), ("emergency", RoundsEmergencyNames(result.Emergency)) };
        attrs.AddRange(RoundsStageAttrs(result.Review.Stage));
        attrs.AddRange(RoundsFoodAttrs(reading.Projection.Facts, Seasonal(reading.Projection.Facts)));
        ClockEvents.Event("routine", "rounds_review", "rounds ran", [.. attrs]);
        LogColonyStage(result.Review, ct);
        LogGearSearch(result.Needs.GearBudgetPawns, ct);
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

/// <summary>The Rounder's configured capabilities: the runtime's complete configured method set.</summary>
public sealed record RoundsCapabilities
{
    public IReadOnlyList<ConcernId> Methods { get; init; } = [];

    /// <summary>Draws the layout plan as a native overlay layer (serve --layout-overlay).</summary>
    public bool LayoutOverlay { get; init; }

    /// <summary>Sends the undraft sweep's Draft intents; null never undrafts.</summary>
    public IActionsWriter? Undraft { get; init; }
}

internal readonly record struct QuestSkipKey(QuestId Quest, QuestSkipReason Reason);

internal static partial class BuildingRuntimePackage
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
            Kind = "planner_step",
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

    /// <summary>The review row's colony stage attrs: the stage the review derived and the first unmet condition of the next.</summary>
    internal static List<(string Key, object? Value)> RoundsStageAttrs(ColonyStageRecord? stage)
    {
        if (stage is null)
        {
            return [];
        }
        return [("stage", stage.Stage.ToString()), ("stage_blocker", stage.Blocker.ToString()), ("stage_reason", stage.Reason), ("stage_held", stage.Held)];
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
    internal static List<string> RoundsEmergencyNames(IEnumerable<ConcernId> ids) => [.. ids.Select(id => id.Value)];

    /// <summary>
    /// Writes the review's colony snapshot when snapshot.DirEnv names a directory; a failed write is logged,
    /// never the review's error.
    /// </summary>
    internal static void RecordRoundsSnapshot(GenerationSnapshot current, Tick tick, RoundsResult result, ColonyProjection reading, CancellationToken ct)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotApi.DirEnv) ?? "";
        if (dir == "")
        {
            return;
        }
        SnapshotApi.Later(() =>
        {
            if (SnapshotApi.FromReview(current, tick, result, reading) is { } recorded)
            {
                try
                {
                    SnapshotApi.Record(dir, recorded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Tel.Decide(SnapshotSkipDecision("routine", "colony", ex, tick));
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
        var (ground, groundKnown) = ColonyGround(colony);
        if (!groundKnown)
        {
            return [];
        }
        // The native census takes rectangles: a non-rect ground is asked for by its bounds.
        return [.. PolicyApi.PlannedGround(plan, ground).Select(g => g.Bounds())];
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
    internal void DropClearedRetiredGround(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<ClearanceTarget> player, IReadOnlyList<ClearanceFloor> floors, CancellationToken ct)
    {
        if (!projection.LayoutPlan.TryGetValue(out var plan) || plan.RetiredGround.Count == 0)
        {
            return;
        }
        var done = PolicyApi.RetiredGroundDone(plan, player, floors);
        if (done.Count == 0)
        {
            return;
        }
        var next = plan.WithoutRetiredGround(done);
        Player.Journal.RecordLayoutPlan(snapshot, projection.Identity.Tick, next, ct);
        projection = projection with { LayoutPlan = Known(next) };
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
