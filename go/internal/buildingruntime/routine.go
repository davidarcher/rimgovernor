package buildingruntime

import (
	"context"
	"fmt"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineReviewer observes and journals needs under Player's existing gate.
// It neither acquires authority nor creates methods or game orders.
type RoutineReviewer struct {
	methods domain.Fact[[]policy.GoalID]
	player  *Player
	native  observation.RoutineSource
	clock   observation.Clock
	policy  policy.RoutinePolicy
	maxAge  time.Duration
	// undraft sends the undraft sweep's intents; nil never undrafts.
	undraft boundary.ActionsWriter
	// census retains the latest review reading for the planners of the same
	// tick; see routineCensus.
	census routineCensusStore
	// store receives each review's decoded sections (#354); the scheduler
	// that steps this reviewer sets it, a standalone reviewer files nowhere.
	store *facts.Store
	// buildTier is the last build tier logged (#604): the service log
	// records a change once, not every review.
	buildTier domain.Fact[policy.BuildTier]
	// stockpiles remembers since when each owned stockpile sat mostly
	// empty (#725); see stockpileMemory.
	stockpiles stockpileMemory
	// tunnels remembers the buried-ore corridor each resource last sited
	// until a stage is admitted under it (#1124); see tunnelMemory.
	tunnels tunnelMemory
	// safeArea is MaintainShelter's Safe area memory (#1325).
	safeArea safeAreaMemory
	// firebreak is MaintainFirebreak's review memory (#1548), set when its
	// planner is composed.
	firebreak *firebreakMemory
	// psylink is MaintainPsylink's review memory (#1609), set when its
	// planner is composed.
	psylink *psylinkMemory
	// stage is the colony stage of the review the last step loaded (#630):
	// the stage the store holds that step's review to, so the planners'
	// targets (staged) agree with the review's. Foothold before any
	// review filed one.
	stage policy.ColonyStage
	// stageLogged is the last stage record logged; the service log records
	// a change once, not every review.
	stageLogged domain.Fact[policy.ColonyStageRecord]
	// foodGapZero is the acceptance fault that pins the food plan's gap to
	// zero (Faults.FoodGapZero); the scheduler sets it.
	foodGapZero bool
	// bids is MaintainResource's joint ranking across its two planners
	// (#728); see acquisitionBoard.
	bids acquisitionBoard
	// planChecked is the tick of the last layout survey this process read;
	// planSurveyed is set once any survey was read. See reviewLayoutPlan.
	planChecked  domain.Tick
	planSurveyed bool
	// planGrownFor is the tier and finished research the saved plan was
	// last grown for (#1290), in memory only: a restart replans once.
	planGrownFor string
	planPawns    int
	// planInputs is the last replan's survey and inputs; an hourly check
	// that finds them unchanged skips the replan (#1290).
	planInputs layoutInputs
	// suiteClaimsLogged is the last suite claim set logged (#1257).
	suiteClaimsLogged string
	// layoutInvalidLogged: an invalid saved layout plan is logged once.
	layoutInvalidLogged bool
	// layoutOverlay draws the layout plan as a native overlay (#817); the
	// overlay fields record the last draw. See drawLayoutOverlay.
	layoutOverlay  bool
	overlayKey     string
	overlayDrawn   domain.Tick
	overlayCleared bool
	heatDrawn      domain.Tick
	heatCleared    bool
	// strip is the last status strip sent (#823); see drawStatusStrip.
	strip statusStripState
	// safety is the last safety layer sent (#824); see drawSafetyOverlay.
	safety statusStripState
	// stock is the last stock layer sent (#825); see drawStockOverlay.
	stock statusStripState
	// pause is who stopped the clock the scheduler's step read (#847),
	// set before the review; see clockPause.
	pause policy.ClockPause
	// requests are the panel's player requests the poll handed over
	// (#957); proposal is the layout replan awaiting Apply or Discard,
	// note the last layout request's outcome and proposalDrawn the
	// proposal layer last drawn. See servePlayerRequests.
	requests      playerRequests
	proposal      *layoutProposal
	note          layoutNote
	proposalDrawn string
}

// staged is the configured policy with its goal budgets set by the colony
// stage (policy.StageRoutinePolicy): the development-project limit, the
// research ladder's pace and the stall deadline.
func (r *RoutineReviewer) staged() policy.RoutinePolicy {
	return policy.StageRoutinePolicy(r.policy, r.stage)
}

// logColonyStage records a review's stage in the service log and timeline
// once per change of stage or blocker: `[routine] colony stage Reserves
// (settling: production clear 1.0 of 2.0 days)`.
func (r *RoutineReviewer) logColonyStage(ctx context.Context, review store.RoutineReview) {
	if review.Stage == nil {
		return
	}
	stage := *review.Stage
	if last, logged := r.stageLogged.Value(); logged && last.Stage == stage.Stage && last.Blocker == stage.Blocker && last.Held == stage.Held {
		return
	}
	r.stageLogged = domain.Known(stage)
	message := "colony stage " + stage.Stage.String()
	if stage.Reason != "" {
		message += " (" + string(stage.Blocker) + ": " + stage.Reason + ")"
	}
	clockEvent(ctx, "routine", "colony_stage", message, "stage", stage.Stage.String(), "since", int64(stage.Since), "blocker", string(stage.Blocker), "reason", stage.Reason, "held", stage.Held)
}

// logBuildTier records the reading's build tier in the service log once per
// change: `[layout] build tier Masonry (Stonecutting)`. An unknown tier
// (no research census) is not a change.
func (r *RoutineReviewer) logBuildTier(ctx context.Context, projection observation.ColonyProjection) {
	tier, known := projection.BuildTier.Value()
	if !known {
		return
	}
	if last, logged := r.buildTier.Value(); logged && last == tier {
		return
	}
	r.buildTier = projection.BuildTier
	evidence := policy.BuildTierEvidence(observation.FinishedResearch(projection.Facts.Research), projection.PlayerTechLevel)
	message := "build tier " + tier.String()
	if evidence != "" {
		message += " (" + evidence + ")"
	}
	clockEvent(ctx, "layout", "build_tier", message, "tier", tier.String(), "evidence", evidence)
}

// seasonal is the configured policy with its food and wood targets widened
// by the calendar the reading carries (policy.RoutinePolicy.Seasonal): the
// same targets DetectRoutine measures the latches against, so a planner's
// deficit, field budget and butcher gate agree with the review.
func (r *RoutineReviewer) seasonal(facts policy.RoutineFacts) policy.RoutinePolicy {
	return r.staged().Seasonal(facts.Calendar, facts.DisasterConditions)
}

// publishFrame puts the review frame's colony facts, pawn and things
// tables into the colony mirror (#795), which recordings and planners serve. A
// section the frame lacks keeps the table the mirror holds, and its tick
// (#1347).
func (r *RoutineReviewer) publishFrame(expected observation.Identity, frame bridge.RoutineFrame) {
	if r.store == nil {
		r.census.rememberColony(nil)
		return
	}
	generation, _ := expected.NativeGeneration.Value()
	scope := facts.Scope{Load: string(expected.Load), Map: int32(expected.Map), Generation: uint64(generation)}
	if frame.Tables.Pawns != nil {
		publishPawns(r.store, scope, frame.Tables.Pawns, frame.Context.GetTick())
	}
	if frame.Tables.Things != nil {
		publishThings(r.store, scope, frame.Tables.Things, frame.Context.GetTick())
	}
	if frame.Colony != nil {
		r.census.rememberColony(publishColony(r.store, scope, frame.Colony))
	}
}

// RoutineCapabilities is the runtime's complete configured method set. Omitting
// it leaves availability unspecified for callers that compose methods themselves.
type RoutineCapabilities struct {
	Methods []policy.GoalID
	// LayoutOverlay draws the layout plan as a native overlay layer
	// (#726, serve --layout-overlay).
	LayoutOverlay bool
	// Undraft sends the undraft sweep's Draft intents (#939); nil never
	// undrafts.
	Undraft boundary.ActionsWriter
}

func NewRoutineReviewer(player *Player, native observation.RoutineSource, clock observation.Clock, thresholds policy.RoutinePolicy, maxAge time.Duration, capabilities ...RoutineCapabilities) (*RoutineReviewer, error) {
	if player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > time.Minute {
		return nil, fmt.Errorf("%w: NewRoutineReviewer: player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > t", ErrControl)
	}
	methods := domain.Unknown[[]policy.GoalID]()
	reviewerOverlay := false
	var undraftWriter boundary.ActionsWriter
	if len(capabilities) > 1 {
		return nil, fmt.Errorf("%w: NewRoutineReviewer: len(capabilities) > 1", ErrControl)
	}
	if len(capabilities) == 1 {
		reviewerOverlay, undraftWriter = capabilities[0].LayoutOverlay, capabilities[0].Undraft
		methods = domain.Known(append([]policy.GoalID{}, capabilities[0].Methods...))
		if _, err := policy.DetectRoutine(policy.RoutineFacts{AvailableMethods: methods}, policy.RoutineLatches{}, thresholds); err != nil {
			return nil, err
		}
	}
	reviewer := &RoutineReviewer{methods: methods, player: player, native: native, clock: clock, policy: thresholds, maxAge: maxAge, layoutOverlay: reviewerOverlay, undraft: undraftWriter}
	return reviewer, nil
}

func (r *RoutineReviewer) Step(ctx context.Context) (store.RoutineReviewResult, error) {
	call, epoch, done, err := r.player.enter(ctx, "routine_review", false)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter(), false)
}

// step is also usable by a scheduler already holding the player gate;
// partial is set when only a subset of the planners follows the review.
func (r *RoutineReviewer) step(ctx, epoch context.Context, arbiter *stepArbiter, partial bool) (store.RoutineReviewResult, error) {
	p := r.player
	state := p.session.State()
	if !state.Enabled {
		clockSchedulerLog("routine.step: state not enabled -> stopRoutine")
		return p.stopRoutine(ctx)
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		clockSchedulerLog("routine.step: ErrControl observationKnown=%v snapshotValidate=%v native=%d", state.ObservationKnown, state.Snapshot.Validate(), state.Snapshot.Native)
		return store.RoutineReviewResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	previous, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		clockSchedulerLog("routine.step: LoadRoutineReview err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	r.stage = policy.StageFoothold
	if previous.Stage != nil {
		r.stage = previous.Stage.Stage
	}
	expected, err := stepScope(ctx, r.native)
	if err != nil {
		clockSchedulerLog("routine.step: stepScope err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	native, known := expected.NativeGeneration.Value()
	if expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map || !known || native != state.Snapshot.Native {
		clockSchedulerLog("routine.step: ErrControl identity mismatch expectedColony=%v stateColony=%v expectedLoad=%v stateLoad=%v expectedMap=%v stateMap=%v known=%v native=%d stateNative=%d",
			expected.Colony, state.Snapshot.Colony, expected.Load, state.Snapshot.Load, expected.Map, state.Snapshot.Map, known, native, state.Snapshot.Native)
		return store.RoutineReviewResult{}, fmt.Errorf("%w: step: expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.S", ErrControl)
	}
	plans, err := p.journal.LoadPlans(ctx, 256)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(ctx, playerWorld(state.Snapshot))
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	definitions := routineProjectDefinitions(plans, state.Snapshot, playerPlans)
	readDefinitions := definitions
	if r.methodEnabled(policy.EnsureCooking) {
		readDefinitions = append(append([]string(nil), definitions...), "NutrientPasteDispenser", "Hopper")
	}
	if r.roomsEnabled() {
		readDefinitions = append(append([]string(nil), readDefinitions...), policy.RoomUpgradeDefinitions...)
	}
	if r.methodEnabled(policy.MaintainFlooring) {
		// The traffic tier prices its floor in the review (#950).
		readDefinitions = append(append([]string(nil), readDefinitions...), r.policy.Flooring.Floors...)
	}
	if r.methodEnabled(policy.MaintainHousing) {
		// Bed's availability decides which beds are suitable (#1181).
		readDefinitions = append(append([]string(nil), readDefinitions...), policy.SleepingBedDefinitions[0])
	}
	if r.methodEnabled(policy.MaintainWaste) {
		readDefinitions = append(append([]string(nil), readDefinitions...), wasteDefinitions...)
	}
	if r.methodEnabled(policy.EnsureCooking) || r.methodEnabled(policy.MaintainRefrigeration) || r.methodEnabled(policy.MaintainPopulation) {
		// The planned kitchen, freezer and jail shells (#835) and jail beds (#880).
		readDefinitions = append(append([]string(nil), readDefinitions...), "Wall", "Door", policy.JailBedDefinition)
	}
	claims, err := p.journal.ConstructionClaims(ctx, state.Snapshot, expected.Tick)
	if err != nil {
		clockSchedulerLog("routine.step: ConstructionClaims err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	observe := observation.ObserveRoutineOwned
	if r.roomsEnabled() {
		observe = observation.ObserveRoutineRooms
	}
	reading, err := observe(standaloneWindow(ctx, r.native), r.native, r.clock, expected, r.maxAge, claims, readDefinitions...)
	if err == nil {
		r.publishFrame(expected, reading.Frame)
	}
	if err != nil {
		clockSchedulerLog("routine.step: observe err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	if err = releaseBreakWork(ctx, p.journal, state.Snapshot, reading.Emergency, plans); err != nil {
		return store.RoutineReviewResult{}, err
	}
	r.logBuildTier(ctx, reading.Projection)
	reading.Projection.Facts.FoodPlan = r.planFood(reading.Projection)
	reading.Projection.Facts.ConstructionClaims = claims
	reading.Sections.Colony.Value.Facts.ConstructionClaims = reading.Projection.Facts.ConstructionClaims
	r.reviewRoyalty(ctx, state.Snapshot, &reading)
	if err = r.reviewLayoutPlan(ctx, state.Snapshot, &reading.Projection); err != nil {
		clockSchedulerLog("routine.step: layout plan err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Sections.Colony.Value.LayoutPlan = reading.Projection.LayoutPlan
	if plan, ok := reading.Projection.LayoutPlan.Value(); ok {
		if rooms, rk := reading.Projection.Rooms.Value(); rk {
			reading.Projection.Rooms = domain.Known(policy.MarkEnemyDoors(rooms, plan.KillboxCells()))
		}
	}
	r.census.rememberLayout(reading.Projection.Identity, reading.Projection.LayoutPlan)
	reading.Projection.Facts.TitleClaimQuests = titleClaimQuests(reading.Projection)
	reading.Projection.Facts.BedroomsOwed = bedroomsOwed(reading.Projection)
	reading.Projection.Facts.CorpsesOwed = corpsesOwed(reading.Projection)
	reading.Projection.Facts.TombsWarm = warmTombs(reading.Projection)
	reading.Projection.Facts.MealClosetOwed = mealClosetOwed(reading.Projection)
	reading.Projection.Facts.CampfireRetireOwed = campfireRetireOwed(reading.Projection)
	reading.Projection.Facts.TemperatureOwed = temperatureOwed(reading.Projection)
	if targets, err := shellTargets(ctx, p.journal, state.Snapshot, reading.Projection); err != nil {
		return store.RoutineReviewResult{}, err
	} else {
		reading.Projection.Facts.ShellsShort = policy.ShellsShort(targets, reading.Projection.Resources)
	}
	if r.methodEnabled(policy.MaintainShelter) {
		if reading.Projection.Facts.SafeAreaOwed, err = r.safeArea.review(stockpileWorld(state.Snapshot), reading.Projection); err != nil {
			return store.RoutineReviewResult{}, err
		}
	}
	// The review's PlanSheltering raises RecoverDisasterServices, so it reads
	// the same draft set as the recovery planner: unknown, a threat shelters
	// no colonist and the planner never runs (#1560).
	if reading.Projection.Facts.ShelterCombatants, err = shelterCombatants(ctx, p.journal, store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}); err != nil {
		return store.RoutineReviewResult{}, err
	}
	if r.methodEnabled(policy.MaintainArt) {
		reading.Projection.Facts.SculptureRoomsOwed = sculptureRoomsOwed(reading.Projection)
	}
	if reading.Projection.Facts.SaleArt, err = reviewSaleArt(ctx, r.native, boundary.Identity(state.Snapshot), reading.Projection); err != nil {
		clockSchedulerLog("routine.step: sale art err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	if parts, benches, err := surgeryPartDemand(ctx, r.native, boundary.Identity(state.Snapshot), reading.Projection.Facts.MedicalPawns); err != nil {
		clockSchedulerLog("routine.step: surgery parts err=%v", err)
		return store.RoutineReviewResult{}, err
	} else if len(parts) > 0 {
		reading.Projection.Facts.FabricableParts = policy.FabricableParts(benches)
	}
	if err = r.reviewTidy(ctx, state.Snapshot, &reading.Projection, tidyBusy(definitions, plans, state.Snapshot, playerPlans)); err != nil {
		clockSchedulerLog("routine.step: tidy err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	if err = r.reviewStockpiles(ctx, state.Snapshot, &reading.Projection); err != nil {
		clockSchedulerLog("routine.step: stockpiles err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.ResourceSurfaceOre = r.resourceSurfaceOre(ctx, state.Snapshot)
	r.reviewMeals(&reading.Projection)
	r.reviewReserve(&reading.Projection)
	if r.methodEnabled(policy.MaintainFlooring) {
		reading.Projection.Facts.Upkeep.Flooring = trafficFlooringFacts(reading.Projection, r.policy.Flooring)
		reading.Sections.Colony.Value.Facts.Upkeep.Flooring = reading.Projection.Facts.Upkeep.Flooring
	}
	if r.firebreak != nil {
		if reading.Projection.Facts.FirebreakOwed, err = r.firebreak.review(ctx, boundary.Identity(state.Snapshot), state.Snapshot, reading.Projection, r.stage, r.policy.Flooring, firebreakBusy(plans, state.Snapshot, playerPlans)); err != nil {
			clockSchedulerLog("routine.step: firebreak err=%v", err)
			return store.RoutineReviewResult{}, err
		}
	}
	var psylinkCandidates domain.Fact[[]policy.PawnID]
	if r.psylink != nil {
		psylinkCandidates, reading.Projection.Facts.PsylinkOwed = r.psylink.review(ctx, boundary.Identity(state.Snapshot), state.Snapshot, reading.Projection)
	}
	if plan, known := reading.Projection.Facts.FoodPlan.Value(); known {
		reading.Projection.Facts.AnimalUpkeep.Forecast = domain.Known(plan.Forecast)
	}
	reading.Sections.Colony.Value.Facts.FoodPlan = reading.Projection.Facts.FoodPlan
	reading.Sections.Colony.Value.Facts.FoodReserve = reading.Projection.Facts.FoodReserve
	r.census.retain(reading, r.roomsEnabled(), claims)
	reading.Sections.File(r.store, facts.Scope{Load: string(expected.Load), Map: int32(expected.Map), Generation: uint64(native)})
	// A served native section may have gained a fresh derived food review.
	if r.store != nil && reading.Sections.Colony.Source != "" {
		facts.Put(r.store, facts.Scope{Load: string(expected.Load), Map: int32(expected.Map), Generation: uint64(native)}, facts.Colony, reading.Sections.Colony)
	}
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency)
	if err != nil {
		clockSchedulerLog("routine.step: NewEmergencySnapshot err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.Hostiles, reading.Projection.Facts.CriticalPatients = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	reading.Projection.Facts.KillboxWindow = r.safeArea.killboxWindow(stockpileWorld(state.Snapshot), reading.Projection.Facts.Hostiles, expected.Tick)
	reading.Projection.Facts.KillboxHaulers = policy.KillboxHaulers(reading.Projection.WorkPawns)
	reading.Projection.Facts.UrgentPatients = policy.UrgentPatients(emergency, state.Snapshot, expected.Tick)
	reading.Projection.Facts.CriticalPatients, reading.Projection.Facts.UrgentPatients = policy.AmputationNeeds(emergency, reading.Projection.Facts.MedicalPawns, reading.Projection.Facts.CriticalPatients, reading.Projection.Facts.UrgentPatients)
	// RestoreWorkers stands while a drafted colonist no live plan needs
	// waits for the undraft sweep (#939). A draft its plan still holds is
	// working, not stranded (#679).
	needed, err := plannedDrafts(ctx, p.journal)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	stray, err := idleDrafts(state, reading.Emergency, reading.Frame.Pawns, needed)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.CleanupPawns = domain.Known(len(stray) > 0)
	reading.Projection.Facts.HostilityOwed = policy.HostilityOwed(hostilityPawns(reading.Projection.WorkPawns), reading.Emergency.Threats)
	reading.Projection.Facts.MedicalCareOwed = policy.MedicalCareOwed(reading.Projection.Facts, r.policy.MedicalReserve)
	reading.Projection.Facts.SelfTendOwed = policy.SelfTendOwed(selfTendPawns(reading.Projection.WorkPawns))
	reading.Projection.Facts.NamesOwed = policy.NamesOwed(reading.Projection.Facts.OwnedNames)
	reading.Projection.ApplyFieldBudget(r.seasonal(reading.Projection.Facts).FoodTargetDays)
	// The workshop ladder's recorded research rung is the derived
	// EnsureResearch target; it is journal evidence, not a native read, so
	// a review costs no extra call for it.
	needs, err := routineResearchNeeds(ctx, p.journal, r.policy, state.Snapshot)
	if err != nil {
		clockSchedulerLog("routine.step: LoadProductionLadder err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	needs = r.censusResearchNeeds(needs)
	reading.Projection.Facts.ResearchNeeds = needs
	reading.Projection.Facts.DefensiveLayoutStanding, reading.Projection.Facts.ResourceNeeds, err = routineDefensiveLayoutStanding(ctx, p.journal, r.policy, state.Snapshot)
	if err != nil {
		clockSchedulerLog("routine.step: LoadDefenseLayout err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.ResourceNeeds = policy.ResourceGoalTargets(reading.Projection.Facts.ResourceNeeds, policy.SocialDrugTargets(reading.Projection.Facts.Research))
	medicine, err := policy.ReviewMedicalReserve(reading.Projection.Facts.MedicalReserve, previous.Snapshot == state.Snapshot && previous.Latches.MedicalReserve, r.policy.MedicalReserve)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.MedicineCarryOwed = policy.MedicineCarryOwed(reading.Projection.WorkPawns, medicine)
	reading.Projection.Facts.ResourceNeeds = policy.MedicineResourceNeeds(reading.Projection.Facts.ResourceNeeds, r.policy.MedicineReserveTarget(reading.Projection.Facts.Colonists, medicine.Active))
	// A prisoner surgery blocked only by the herbal care limit (#1239).
	reading.Projection.Facts.ResourceNeeds = policy.PrisonerHerbalNeeds(reading.Projection.Facts.ResourceNeeds, reading.Projection.Facts, policy.RoutineSilverShort(reading.Projection.Facts, r.policy, medicine.Active))
	// A willing colonist's psylink neuroformer, bought or made by the resource ladder (#1609).
	reading.Projection.Facts.ResourceNeeds = policy.NeuroformerNeeds(reading.Projection.Facts.ResourceNeeds, reading.Projection.Royalty, psylinkCandidates)
	resourceTargets, err := r.policy.EffectiveResourceTargets(reading.Projection.Facts.Resources, reading.Projection.Facts.ResourceNeeds)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	if pawns, known := reading.Projection.WorkPawns.Value(); known {
		reading.Projection.Facts.Workers = policy.RoutineWorkers(pawns)
		reading.Projection.Facts.Labor = policy.RoutineLabor(pawns)
		reading.Projection.Facts.LaborUse = policy.RoutineLaborUse(pawns)
		reading.Projection.Facts.WorkerCensus = policy.DevelopmentCensus(pawns)
		reading.Projection.Facts.WorkProfiles = domain.Known(policy.Profiles(pawns))
		required, known := routineProjectWork(definitions, reading.Projection.Definitions).Value()
		if known {
			// Bench work (open bills, a deficit's standing benches) counts
			// toward coverage here so the work goal assesses a deficit the
			// planner then covers; a failed census leaves coverage unknown.
			native, _ := r.native.(RoutineWorkBenchSource)
			benches := r.benchSource(native, expected, true)
			recovered, _ := policy.ResourceTargetNeed(resourceTargets, reading.Projection.Facts.Resources)
			deficit, deficitKnown := recovered.Value()
			targets := routineDeficitTargets(resourceTargets, deficitKnown && !deficit, reading.Projection.Facts.Gear)
			benchWork, err := routineBenchWork(ctx, benches, state.Snapshot, plans, playerPlans, targets, len(targets) > 0)
			if err != nil {
				clockSchedulerLog("routine.step: bench work err=%v", err)
			}
			var rows []policy.WorkRequirement
			rows, known = benchWork.Value()
			required = mergeWorkRequirements(required, rows)
			required = mergeWorkRequirements(required, routineResearchWork(policy.ArmorResearchPolicy(r.policy, previous.Latches.Soldiers || policy.GearSoldierPresent(reading.Projection.Facts.Gear)), needs, reading.Projection.Facts.Research))
			required = mergeWorkRequirements(required, fishingWork(reading.Projection))
		}
		if known {
			demand, err := routineDiseaseDemand(reading.Projection, definitions, previous, state.Snapshot)
			if err != nil {
				return store.RoutineReviewResult{}, err
			}
			work, err := policy.PlanWork(pawns, required, demand)
			if err == nil {
				reading.Projection.Facts.WorkCoverage = work.Matches
				// A timetable behind its role template is a work
				// deficit the same review corrects (#417).
				if matches, ok := work.Matches.Value(); ok && matches {
					meditate, _ := reading.Projection.MeditateAvailable.Value()
					for _, row := range policy.PlanSchedulesHeld(pawns, reading.Projection.Facts.Comfort, meditate, policy.CeremonyHoldOf(reading.Projection.Facts.Royalty)).Schedules {
						if !row.Matches {
							reading.Projection.Facts.WorkCoverage = domain.Known(false)
						}
					}
				}
				if _, ok := work.Capacity.Value(); ok {
					reading.Projection.Facts.WorkRoster = domain.Known(work.Coverage)
					reading.Projection.Facts.WorkDecaying = domain.Known(work.Decaying)
					reading.Projection.Facts.WorkHelp = work.Help
				}
			}
		}
	}
	reading.Projection.Facts.Upkeep.Rooms = reading.Projection.Rooms
	reading.Projection.Facts.CleaningContext(reading.Projection.Identity.Tick)
	if err = p.current(ctx, epoch); err != nil {
		clockSchedulerLog("routine.step: p.current err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	if p.session.State() != state {
		clockSchedulerLog("routine.step: ErrControl state changed under us")
		return store.RoutineReviewResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	// Manual cancels ctx before waiting for this gate, then invalidates any
	// completed review before returning. Never hold the local stop mutex for SQL.
	if r.methodEnabled(policy.ClearHomeObstructions) {
		source, ok := r.native.(observation.ClearanceSource)
		if !ok {
			return store.RoutineReviewResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
		}
		ground := plannedGround(reading.Projection)
		clearance, err := observation.ObserveClearanceCensusOnGround(ctx, source, expected, true, ground)
		if err != nil {
			return store.RoutineReviewResult{}, err
		}
		if census, known := clearance.Value(); known {
			others, player := policy.SplitGroundRows(census.Targets)
			reading.Projection.Facts.Upkeep.Clearance = domain.Known(others)
			reading.Projection.Facts.Upkeep.Chunks = domain.Known(census.Chunks)
			reading.Projection.Facts.Upkeep.Ground = domain.Known(policy.PlannedGroundWork(player, census.Floors, ground, plannedDoors(reading.Projection)))
		}
	}
	if r.methodEnabled(policy.ClearAncientShrine) {
		source, ok := r.native.(observation.ShrineSource)
		if !ok {
			return store.RoutineReviewResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
		}
		if reading.Projection.Facts.Upkeep.Shrines, err = observation.ObserveShrines(ctx, source, expected); err != nil {
			return store.RoutineReviewResult{}, err
		}
		// The breach judgement (#457) is journalled beside the review so the
		// hold reason and the chosen wall are readable; the planner re-reads
		// before drafting anyone. The reads only happen for a sealed shrine
		// with a breach wall.
		if reading.Projection.Facts.ShrineHolds, reading.Projection.Facts.Upkeep.ShrinePolicy, err = routineShrineHolds(ctx, r.native, state.Snapshot, reading.Projection); err != nil {
			return store.RoutineReviewResult{}, err
		}
	}
	reading.Projection.Facts.AvailableMethods = r.methods
	result, err := p.journal.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: previous.Revision, Current: state.Snapshot, Tick: reading.Projection.Identity.Tick, Enabled: true, Policy: r.policy, Facts: reading.Projection.Facts, PartialPlanners: partial})
	if err != nil {
		clockSchedulerLog("routine.step: ReviewRoutine err=%v", err)
	} else {
		clockEvent(ctx, "routine", "routine_review", "routine reviewed", append(append([]any{"revision", result.Review.Revision, "previous_revision", previous.Revision, "tick", int64(reading.Projection.Identity.Tick), "goals", len(result.Goals), "emergency", routineEmergencyNames(result.Emergency)}, routineStageAttrs(result.Review.Stage)...), routineFoodAttrs(reading.Projection.Facts, r.seasonal(reading.Projection.Facts))...)...)
		r.logColonyStage(ctx, result.Review)
		recordRoutineSnapshot(ctx, state.Snapshot, reading.Projection.Identity.Tick, result, reading.Projection)
		r.drawStatusStrip(ctx, state.Snapshot, &reading.Projection, result)
		r.drawSafetyOverlay(ctx, state.Snapshot, &reading.Projection, reading.Emergency)
		r.drawStockOverlay(ctx, state.Snapshot, &reading.Projection, result)
	}
	return result, err
}

// routineStageAttrs are the review row's colony stage attrs (#630): the
// stage the review derived and the first unmet condition of the next.
func routineStageAttrs(stage *policy.ColonyStageRecord) []any {
	if stage == nil {
		return nil
	}
	return []any{"stage", stage.Stage.String(), "stage_blocker", string(stage.Blocker), "stage_reason", stage.Reason, "stage_held", stage.Held}
}

// routineFoodAttrs are the review row's stored-food attrs: the food runway
// the review read and the seasonal thresholds it held it to, with the
// calendar they came from (#229). An unknown fact leaves its attr out, so
// a sustained run's samples are exactly the reviews that had one.
func routineFoodAttrs(f policy.RoutineFacts, seasonal policy.RoutinePolicy) []any {
	attrs := []any{"food_min_days", seasonal.FoodMinDays, "food_target_days", seasonal.FoodTargetDays}
	if reserve, known := f.FoodReserve.Value(); known {
		attrs = append(attrs, "food_reserve_days", seasonal.FoodReserveDays, "food_reserve_target", reserve.TargetNutrition,
			"food_reserve_stock", reserve.StockNutrition, "food_reserve_deficit", reserve.DeficitNutrition, "food_reserve_emergency", reserve.Emergency)
	}
	if plan, known := f.FoodPlan.Value(); known {
		attrs = append(attrs, "food_plan", plan.Explain(), "food_gap_per_day", plan.GapPerDay)
	}
	if days, known := f.FoodDays.Value(); known {
		attrs = append(attrs, "food_days", days)
	}
	if c, known := f.Calendar.Value(); known {
		attrs = append(attrs, "season", c.Season, "day_of_year", c.DayOfYear, "growing_days_remaining", c.GrowingDaysRemaining, "growing_days_until", c.GrowingDaysUntil, "non_growing_days", c.NonGrowingDays)
	}
	return attrs
}

// Caller holds the player gate. Invalidating existing work needs no native read,
// including when a stale browser request stops a different observed world.
// routineEmergencyNames renders the needs that suspended the review's
// goals for an event attr.
func routineEmergencyNames(ids []policy.GoalID) []string {
	out := make([]string, 0, len(ids))
	for _, id := range ids {
		out = append(out, string(id))
	}
	return out
}

func (p *Player) stopRoutine(ctx context.Context) (store.RoutineReviewResult, error) {
	previous, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	if previous.Revision == 0 || !previous.Enabled {
		return store.RoutineReviewResult{Review: previous}, nil
	}
	return p.journal.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: previous.Revision, Current: previous.Snapshot, Tick: previous.Tick, Enabled: false})
}

// recordRoutineSnapshot writes the review's colony snapshot when
// snapshot.DirEnv names a directory (#742); a failed write is logged, never
// the review's error.
func recordRoutineSnapshot(ctx context.Context, current domain.GenerationSnapshot, tick domain.Tick, result store.RoutineReviewResult, reading observation.ColonyProjection) {
	dir := os.Getenv(snapshot.DirEnv)
	if dir == "" {
		return
	}
	snapshot.Later(func() {
		if recorded, ok := snapshot.FromReview(current, tick, result, reading); ok {
			if err := snapshot.Record(dir, recorded); err != nil {
				clockEvent(ctx, "routine", "snapshot", "colony snapshot not recorded: "+err.Error(), "tick", int64(tick))
			}
		}
	})
}

// plannedGround is the ground of the recorded plan's rooms not yet standing
// (#1245); none while the plan or the room census is unknown, so nothing of
// the colony's comes down on a guess. The reviewer's upkeep reading and the
// clearance planner share it.
func plannedGround(colony observation.ColonyProjection) []policy.Rectangle {
	plan, known := colony.LayoutPlan.Value()
	if !known {
		return nil
	}
	rooms, known := colony.Rooms.Value()
	if !known {
		return nil
	}
	return policy.PlannedGround(plan, rooms)
}

// plannedDoors is the recorded plan's door cells; none while it is unknown
// (then there is no planned ground either).
func plannedDoors(colony observation.ColonyProjection) map[domain.Cell]bool {
	plan, _ := colony.LayoutPlan.Value()
	return policy.PlannedDoors(plan)
}
