package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"os"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// Rounder observes and journals needs under Player's existing gate.
// It neither acquires authority nor creates methods or game orders.
// itemFacts are the load's catalog item facts for a planner that holds no
// frame; none when the reviewer's source serves no definitions.
func (r *Rounder) itemFacts(ctx context.Context, snapshot domain.GenerationSnapshot) (policy.ItemFacts, error) {
	source, ok := r.native.(observation.DefinitionSource)
	if !ok {
		return policy.ItemFacts{}, nil
	}
	catalog, err := source.DefinitionCatalog(ctx, boundary.Identity(snapshot))
	if err != nil {
		return policy.ItemFacts{}, err
	}
	return catalog.ItemFacts()
}

// combatDrug is the preferred combat drug of the load's catalog (#1540), ""
// when it has none or the reviewer's source serves no definitions.
func (r *Rounder) combatDrug(ctx context.Context, snapshot domain.GenerationSnapshot) (string, error) {
	items, err := r.itemFacts(ctx, snapshot)
	if err != nil {
		return "", err
	}
	if drugs := items.CombatDrugs(); len(drugs) > 0 {
		return string(drugs[0].Def), nil
	}
	return "", nil
}

type Rounder struct {
	methods domain.Fact[[]policy.ConcernID]
	player  *Player
	native  observation.RoundsSource
	clock   observation.Clock
	policy  policy.RoundsPolicy
	// resourceNative is the native surface of the resource planner, set when
	// it is composed: the Round's resource supply plan reads mine sources and
	// bench bills through it. Nil plans only the census kinds (chop, harvest, hunt).
	resourceNative RoundsResourceSource
	maxAge         time.Duration
	// undraft sends the undraft sweep's intents; nil never undrafts.
	undraft boundary.ActionsWriter
	// census retains the latest review reading for the planners of the same
	// tick; see roundsCensus.
	census roundsCensusStore
	// store receives each review's decoded sections (#354); the scheduler
	// that steps this reviewer sets it, a standalone reviewer files nowhere.
	store *facts.Store
	// skipsLogged are the Odyssey quest skips already logged (#1717).
	skipsLogged map[odysseySkipKey]bool
	// buildTier is the last build tier logged (#604): the flight recorder
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
	// creepJoiners is ManageCreepJoiners's review memory (#1740), set when
	// its planner is composed.
	creepJoiners *creepJoinerMemory
	// moodCasts is set when the mood relief planner is composed: it casts
	// mood psycasts (#1612), so the review reads the royalty facts.
	moodCasts bool
	// stage is the colony stage of the review the last step loaded (#630):
	// the stage the store holds that step's review to, so the planners'
	// targets (staged) agree with the review's. Foothold before any
	// review filed one.
	stage policy.ColonyStage
	// stageLogged is the last stage record logged; the flight recorder records
	// a change once, not every review.
	stageLogged domain.Fact[policy.ColonyStageRecord]
	// foodGapZero is the acceptance fault that pins the food plan's gap to
	// zero (Faults.FoodGapZero); the scheduler sets it.
	foodGapZero bool
	// foodCredit is the delivery credit's factors (#2157), in memory only.
	foodCredit policy.DeliveryCredit
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
	// noRoomLogged is the last unplaced-rooms text logged as layout_plan
	// refused/no_room, so a full map logs it once.
	noRoomLogged string
	// layoutOverlay draws the layout plan as a native overlay (#817); the
	// overlay fields record the last draw. See drawLayoutOverlay.
	layoutOverlay  bool
	overlayKey     string
	overlayDrawn   domain.Tick
	overlayCleared bool
	heatDrawn      domain.Tick
	heatCleared    bool
	// safety is the last safety layer sent (#824); see drawSafetyOverlay.
	safety overlayState
	// spots is the last work-spot layer sent; see drawSpotOverlay.
	spots overlayState
	// stock is the last stock layer sent (#825); see drawStockOverlay.
	stock overlayState
}

// staged is the configured policy with its goal budgets set by the colony
// stage (policy.StageRoundsPolicy): the development-project limit, the
// research ladder's pace and the stall deadline.
func (r *Rounder) staged() policy.RoundsPolicy {
	return policy.StageRoundsPolicy(r.policy, r.stage)
}

// logColonyStage records a review's stage in the flight recorder and timeline
// once per change of stage or blocker: `[routine] colony stage Reserves
// (settling: production clear 1.0 of 2.0 days)`.
func (r *Rounder) logColonyStage(ctx context.Context, review store.Rounds) {
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

// logBuildTier records the reading's build tier in the flight recorder once per
// change: `[layout] build tier Masonry (Stonecutting)`. An unknown tier
// (no research census) is not a change.
func (r *Rounder) logBuildTier(ctx context.Context, projection observation.ColonyProjection) {
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

// odysseySkipDecision is the routine_skip row of an Odyssey offer the colony
// does not accept: target the quest, reason the skip reason, WARN.
func odysseySkipDecision(skip policy.OdysseySkip) telemetry.Decision {
	return telemetry.Decision{Kind: "routine_skip", Component: "routine", Level: slog.LevelWarn, Verdict: "skipped", Reason: string(skip.Reason), Target: string(skip.Quest),
		Attrs: map[string]any{"quest": string(skip.Quest), "script": skip.ScriptDef, "detail": skip.Detail}}
}

type odysseySkipKey struct {
	quest  domain.QuestID
	reason policy.OdysseySkipReason
}

// logOdysseySkips records each Odyssey offer the colony does not accept
// (policy.OdysseySkips) in the flight recorder once per quest and reason:
// `[routine] odyssey quest skipped Q12 OrbitalFugitive: ship_only (Orbit)`.
func (r *Rounder) logOdysseySkips(ctx context.Context, facts policy.RoundsFacts) {
	for _, skip := range policy.OdysseySkips(facts.QuestOffers) {
		key := odysseySkipKey{skip.Quest, skip.Reason}
		if r.skipsLogged[key] {
			continue
		}
		if r.skipsLogged == nil {
			r.skipsLogged = map[odysseySkipKey]bool{}
		}
		r.skipsLogged[key] = true
		telemetry.Decide(ctx, odysseySkipDecision(skip))
	}
}

// seasonal is the configured policy with its food and wood targets widened
// by the calendar the reading carries (policy.RoundsPolicy.Seasonal): the
// same targets DetectRounds measures the latches against, so a planner's
// deficit, field budget and butcher gate agree with the review.
func (r *Rounder) seasonal(facts policy.RoundsFacts) policy.RoundsPolicy {
	return r.staged().Seasonal(facts.Calendar, facts.DisasterConditions)
}

// publishFrame puts the review frame's colony facts, pawn and things
// tables into the colony mirror (#795), which recordings and planners serve. A
// section the frame lacks keeps the table the mirror holds, and its tick
// (#1347).
func (r *Rounder) publishFrame(expected observation.Identity, frame bridge.RoundsFrame) {
	if r.store == nil {
		r.census.rememberColony(nil)
		return
	}
	generation, _ := expected.NativeGeneration.Value()
	scope := facts.Scope{Load: string(expected.Load), Map: int32(expected.Map), Generation: uint64(generation)}
	publishPawns(r.store, scope, frame.Tables.Pawns, frame.Context.GetTick())
	publishThings(r.store, scope, frame.Tables.Things, frame.Context.GetTick())
	if frame.Colony != nil {
		r.census.rememberColony(publishColony(r.store, scope, frame.Colony))
	}
}

// RoundsCapabilities is the runtime's complete configured method set. Omitting
// it leaves availability unspecified for callers that compose methods themselves.
type RoundsCapabilities struct {
	Methods []policy.ConcernID
	// LayoutOverlay draws the layout plan as a native overlay layer
	// (#726, serve --layout-overlay).
	LayoutOverlay bool
	// Undraft sends the undraft sweep's Draft intents (#939); nil never
	// undrafts.
	Undraft boundary.ActionsWriter
}

func NewRounder(player *Player, native observation.RoundsSource, clock observation.Clock, thresholds policy.RoundsPolicy, maxAge time.Duration, capabilities ...RoundsCapabilities) (*Rounder, error) {
	if player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > time.Minute {
		return nil, fmt.Errorf("%w: NewRounder: player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > t", ErrControl)
	}
	if _, ok := native.(RoundsWorkBenchSource); !ok {
		return nil, fmt.Errorf("%w: NewRounder: native lacks the bench census read", ErrControl)
	}
	methods := domain.Unknown[[]policy.ConcernID]()
	reviewerOverlay := false
	var undraftWriter boundary.ActionsWriter
	if len(capabilities) > 1 {
		return nil, fmt.Errorf("%w: NewRounder: len(capabilities) > 1", ErrControl)
	}
	if len(capabilities) == 1 {
		reviewerOverlay, undraftWriter = capabilities[0].LayoutOverlay, capabilities[0].Undraft
		methods = domain.Known(append([]policy.ConcernID{}, capabilities[0].Methods...))
		if _, err := policy.InspectRounds(policy.RoundsFacts{AvailableMethods: methods}, policy.RoundsLatches{}, thresholds); err != nil {
			return nil, err
		}
	}
	reviewer := &Rounder{methods: methods, player: player, native: native, clock: clock, policy: thresholds, maxAge: maxAge, layoutOverlay: reviewerOverlay, undraft: undraftWriter}
	return reviewer, nil
}

func (r *Rounder) Step(ctx context.Context) (store.RoundsResult, error) {
	call, epoch, done, err := r.player.enter(ctx, "rounds_review", false)
	if err != nil {
		return store.RoundsResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter(), false)
}

// step is also usable by a scheduler already holding the player gate;
// partial is set when only a subset of the planners follows the review.
func (r *Rounder) step(ctx, epoch context.Context, arbiter *stepArbiter, partial bool) (store.RoundsResult, error) {
	began := time.Now()
	result, err := r.reviewStep(ctx, epoch, arbiter, partial)
	telemetry.Decide(ctx, roundsStepDecision(err, time.Since(began), partial))
	return result, err
}

// roundsStepDecision is the one planner_step row of a Rounder step: ok when
// the review ran, failed with the error as an attr (and reason control_lost
// for ErrControl) when it did not.
func roundsStepDecision(err error, took time.Duration, partial bool) telemetry.Decision {
	d := telemetry.Decision{Kind: "planner_step", Component: "clock-scheduler", Target: "rounds", Verdict: "ok", Reason: "reviewed", Dur: took, Attrs: map[string]any{"partial": partial}}
	if err != nil {
		d.Level, d.Verdict, d.Reason = slog.LevelWarn, "failed", "error"
		if errors.Is(err, ErrControl) {
			d.Reason = "control_lost"
		}
		d.Attrs["error"] = err
	}
	return d
}

func (r *Rounder) reviewStep(ctx, epoch context.Context, arbiter *stepArbiter, partial bool) (store.RoundsResult, error) {
	p := r.player
	state := p.session.State()
	if !state.Enabled {
		return p.stopRounds(ctx)
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return store.RoundsResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	previous, err := p.journal.LoadRounds(ctx)
	if err != nil {
		return store.RoundsResult{}, err
	}
	r.stage = policy.StageFoothold
	if previous.Stage != nil {
		r.stage = previous.Stage.Stage
	}
	expected, err := stepScope(ctx, r.native)
	if err != nil {
		return store.RoundsResult{}, err
	}
	native, known := expected.NativeGeneration.Value()
	if expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map || !known || native != state.Snapshot.Native {
		return store.RoundsResult{}, fmt.Errorf("%w: step: expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.S", ErrControl)
	}
	plans, err := p.journal.LoadPlans(ctx, 256)
	if err != nil {
		return store.RoundsResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(ctx, playerWorld(state.Snapshot))
	if err != nil {
		return store.RoundsResult{}, err
	}
	definitions := roundsProjectDefinitions(plans, state.Snapshot, playerPlans)
	readDefinitions := definitions
	if r.methodEnabled(policy.EnsureCooking) {
		readDefinitions = append(append([]string(nil), definitions...), "NutrientPasteDispenser", "Hopper")
	}
	if r.roomsEnabled() {
		readDefinitions = append(append([]string(nil), readDefinitions...), policy.RoomBeautyDefinitions()...)
	}
	if r.methodEnabled(policy.MaintainFlooring) {
		// The traffic tier prices its floor in the review (#950).
		readDefinitions = append(append([]string(nil), readDefinitions...), r.policy.Flooring.Floors...)
	}
	if r.methodEnabled(policy.MaintainAnimalContainment) {
		readDefinitions = append(append([]string(nil), readDefinitions...), herdDefinitions...)
	}
	if r.methodEnabled(policy.MaintainWaste) || r.methodEnabled(policy.MaintainIncineration) || r.methodEnabled(policy.MaintainBurial) {
		readDefinitions = append(append([]string(nil), readDefinitions...), burialDefinitions...)
	}
	if r.methodEnabled(policy.EnsureCooking) || r.methodEnabled(policy.MaintainRefrigeration) || r.methodEnabled(policy.MaintainPopulation) {
		// The planned kitchen, freezer and jail shells (#835); the jail bed is
		// a furniture rule's, read with every catalog (#880).
		readDefinitions = append(append([]string(nil), readDefinitions...), "Wall", "Door")
	}
	claims, err := p.journal.ConstructionClaims(ctx, state.Snapshot, expected.Tick)
	if err != nil {
		return store.RoundsResult{}, err
	}
	observe := observation.ObserveRoundsOwned
	if r.roomsEnabled() {
		observe = observation.ObserveRoundsRooms
	}
	reading, err := observe(standaloneWindow(ctx, r.native), r.native, r.clock, expected, r.maxAge, claims, readDefinitions...)
	if err == nil {
		r.publishFrame(expected, reading.Frame)
	}
	if err != nil {
		return store.RoundsResult{}, err
	}
	if err = releaseBreakWork(ctx, p.journal, state.Snapshot, reading.Emergency, plans); err != nil {
		return store.RoundsResult{}, err
	}
	r.logBuildTier(ctx, reading.Projection)
	r.logOdysseySkips(ctx, reading.Projection.Facts)
	reading.Projection.Facts.FoodPlan = r.planFood(reading.Projection)
	reading.Projection.Facts.ConstructionClaims = claims
	reading.Sections.Colony.Value.Facts.ConstructionClaims = reading.Projection.Facts.ConstructionClaims
	if err = r.reviewRoyalty(ctx, state.Snapshot, &reading); err != nil {
		return store.RoundsResult{}, err
	}
	// A creepjoiner held apart owes the isolation room, so the census is read
	// before the child rooms and the layout (#1740).
	var creepRecord policy.CreepJoinerRecord
	if r.creepJoiners != nil {
		if creepRecord, err = creepJoinerRecord(ctx, p.journal); err != nil {
			return store.RoundsResult{}, err
		}
		reading.Projection.Isolation = creepJoinerIsolation(reading.Projection, reading.Frame, creepRecord)
	}
	if err = r.reviewChildRooms(&reading); err != nil {
		return store.RoundsResult{}, err
	}
	if err = r.reviewLayoutPlan(ctx, state.Snapshot, &reading.Projection); err != nil {
		return store.RoundsResult{}, err
	}
	reading.Sections.Colony.Value.LayoutPlan = reading.Projection.LayoutPlan
	if plan, ok := reading.Projection.LayoutPlan.Value(); ok {
		if rooms, rk := reading.Projection.Rooms.Value(); rk {
			reading.Projection.Rooms = domain.Known(policy.MarkEnemyDoors(rooms, plan.KillboxCells()))
		}
	}
	r.census.rememberLayout(reading.Projection.Identity, reading.Projection.LayoutPlan)
	reading.Projection.Facts.TitleClaimQuests = titleClaimQuests(reading.Projection)
	reading.Projection.Facts.BedroomsOwed = bedroomsOwed(reading.Projection, r.stage)
	reading.Projection.Facts.BurialOwed = burialOwed(reading.Projection)
	reading.Projection.Facts.IncinerationOwed = incinerationOwed(reading.Projection)
	// The herd furniture is read only when the containment method is on
	// (readDefinitions above); a review without it owes no herd step.
	if r.methodEnabled(policy.MaintainAnimalContainment) {
		if reading.Projection.Facts.HerdRoomsOwed, err = herdRoomsOwed(reading.Projection); err != nil {
			return store.RoundsResult{}, err
		}
	}
	reading.Projection.Facts.WarmRooms = warmCoolingRooms(reading.Projection)
	reading.Projection.Facts.MealClosetOwed = mealClosetOwed(reading.Projection)
	reading.Projection.Facts.CampfireRetireOwed = campfireRetireOwed(reading.Projection)
	reading.Projection.Facts.TemperatureOwed = temperatureOwed(reading.Projection)
	if targets, err := shellTargets(ctx, r.native, p.journal, state.Snapshot, reading.Projection); err != nil {
		return store.RoundsResult{}, err
	} else {
		reading.Projection.Facts.ShellsShort = policy.ShellsShort(targets, reading.Projection.Resources)
	}
	if r.methodEnabled(policy.MaintainShelter) {
		if reading.Projection.Facts.SafeAreaOwed, err = r.safeArea.review(stockpileWorld(state.Snapshot), reading.Projection); err != nil {
			return store.RoundsResult{}, err
		}
	}
	// The review's PlanSheltering raises RecoverDisasterServices, so it reads
	// the same draft set as the recovery planner: unknown, a threat shelters
	// no colonist and the planner never runs (#1560).
	if reading.Projection.Facts.ShelterCombatants, err = shelterCombatants(ctx, p.journal, store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}); err != nil {
		return store.RoundsResult{}, err
	}
	if r.methodEnabled(policy.MaintainArt) {
		reading.Projection.Facts.SculptureRoomsOwed = sculptureRoomsOwed(reading.Projection, r.stage)
	}
	if reading.Projection.Facts.SaleArt, err = reviewSaleArt(ctx, r.native, boundary.Identity(state.Snapshot), reading.Projection); err != nil {
		return store.RoundsResult{}, err
	}
	if parts, benches, err := surgeryPartDemand(ctx, r.native, boundary.Identity(state.Snapshot), reading.Projection.Facts.MedicalPawns, reading.Projection.SurgeryContext()); err != nil {
		return store.RoundsResult{}, err
	} else if len(parts) > 0 {
		reading.Projection.Facts.FabricableParts = policy.FabricableParts(benches)
	}
	if err = r.reviewStockpiles(ctx, state.Snapshot, &reading.Projection); err != nil {
		return store.RoundsResult{}, err
	}
	reading.Projection.Facts.ResourceSurfaceOre = r.resourceSurfaceOre(ctx, state.Snapshot)
	r.reviewMeals(&reading.Projection)
	r.reviewReserve(&reading.Projection)
	r.reviewBabyFeeding(&reading.Projection)
	if r.methodEnabled(policy.MaintainFlooring) {
		reading.Projection.Facts.Upkeep.Flooring = trafficFlooringFacts(reading.Projection, r.policy.Flooring)
		reading.Sections.Colony.Value.Facts.Upkeep.Flooring = reading.Projection.Facts.Upkeep.Flooring
	}
	if r.firebreak != nil {
		if reading.Projection.Facts.FirebreakOwed, err = r.firebreak.review(ctx, boundary.Identity(state.Snapshot), state.Snapshot, reading.Projection, r.stage, r.policy.Flooring, firebreakBusy(plans, state.Snapshot, playerPlans)); err != nil {
			return store.RoundsResult{}, err
		}
	}
	var psylinkCandidates domain.Fact[[]policy.PawnID]
	if r.psylink != nil {
		psylinkCandidates, reading.Projection.Facts.PsylinkOwed = r.psylink.review(ctx, boundary.Identity(state.Snapshot), state.Snapshot, reading.Projection)
	}
	if r.creepJoiners != nil {
		reading.Projection.Facts.CreepJoinerOwed = r.creepJoiners.review(state.Snapshot, reading.Projection.Identity.Tick, reading.Projection, reading.Frame, creepRecord)
	}
	if r.methodEnabled(policy.MaintainIdeoRoles) {
		reading.Projection.Facts.RolesOwed = policy.RolesOwed(reading.Projection.Facts.Ideology, reading.Projection.WorkPawns)
	}
	if r.methodEnabled(policy.MaintainRituals) {
		r.reviewRituals(&reading, state.Snapshot)
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
		return store.RoundsResult{}, err
	}
	reading.Projection.Facts.Hostiles, reading.Projection.Facts.CriticalPatients = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	reading.Projection.Facts.DangerSeeds = dangerSeedFact(reading.Projection.Facts.Hostiles, reading.Emergency.Threats)
	seeds, _ := reading.Projection.Facts.DangerSeeds.Value()
	reading.Projection.Facts.DangerWindow = r.safeArea.dangerWindow(stockpileWorld(state.Snapshot), reading.Projection.Facts.Hostiles, seeds, expected.Tick)
	reading.Projection.Facts.DangerHaulers = policy.DangerHaulers(reading.Projection.WorkPawns)
	reading.Projection.Facts.UrgentPatients = policy.UrgentPatients(emergency, state.Snapshot, expected.Tick)
	reading.Projection.Facts.CriticalPatients, reading.Projection.Facts.UrgentPatients = policy.AmputationNeeds(emergency, reading.Projection.Facts.MedicalPawns, reading.Projection.Facts.CriticalPatients, reading.Projection.Facts.UrgentPatients)
	// RestoreWorkers stands while a drafted colonist no live plan needs
	// waits for the undraft sweep (#939). A draft its plan still holds is
	// working, not stranded (#679).
	needed, err := plannedDrafts(ctx, p.journal)
	if err != nil {
		return store.RoundsResult{}, err
	}
	stray, err := idleDrafts(state, reading.Emergency, reading.Frame.Pawns, needed)
	if err != nil {
		return store.RoundsResult{}, err
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
	needs, err := roundsResearchNeeds(ctx, p.journal, r.policy, reading.Projection.Facts.Items, state.Snapshot)
	if err != nil {
		return store.RoundsResult{}, err
	}
	needs = r.censusResearchNeeds(needs)
	reading.Projection.Facts.ResearchNeeds = needs
	reading.Projection.Facts.DefensiveLayoutStanding, reading.Projection.Facts.ResourceNeeds, err = roundsDefensiveLayoutStanding(ctx, p.journal, r.policy, state.Snapshot)
	if err != nil {
		return store.RoundsResult{}, err
	}
	reading.Projection.Facts.ResourceNeeds = policy.ResourceConcernTargets(reading.Projection.Facts.ResourceNeeds, policy.SocialDrugTargets(reading.Projection.Facts.Research))
	medicine, err := policy.ReviewMedicalReserve(reading.Projection.Facts.MedicalReserve, previous.Snapshot == state.Snapshot && previous.Latches.MedicalReserve, r.policy.MedicalReserve)
	if err != nil {
		return store.RoundsResult{}, err
	}
	reading.Projection.Facts.MedicineCarryOwed = policy.MedicineCarryOwed(reading.Projection.WorkPawns, medicine)
	reading.Projection.Facts.ResourceNeeds = policy.MedicineResourceNeeds(reading.Projection.Facts.Items, reading.Projection.Facts.ResourceNeeds, r.policy.MedicineReserveTarget(reading.Projection.Facts.Colonists, medicine.Active))
	// A prisoner surgery blocked only by the herbal care limit (#1239).
	reading.Projection.Facts.ResourceNeeds = policy.PrisonerHerbalNeeds(reading.Projection.Facts.ResourceNeeds, reading.Projection.Facts, policy.RoundsSilverShort(reading.Projection.Facts, r.policy, medicine.Active))
	// A willing colonist's psylink neuroformer, bought or made by the resource ladder (#1609).
	reading.Projection.Facts.ResourceNeeds = policy.NeuroformerNeeds(reading.Projection.Facts.ResourceNeeds, reading.Projection.Royalty, psylinkCandidates)
	resourceTargets, err := r.policy.EffectiveResourceTargets(reading.Projection.Facts.Resources, reading.Projection.Facts.ResourceNeeds)
	if err != nil {
		return store.RoundsResult{}, err
	}
	if pawns, known := reading.Projection.WorkPawns.Value(); known {
		reading.Projection.Facts.Workers = policy.RoundsWorkers(pawns)
		reading.Projection.Facts.Labor = policy.RoundsLabor(pawns)
		reading.Projection.Facts.LaborUse = policy.RoundsLaborUse(pawns)
		reading.Projection.Facts.WorkProfiles = domain.Known(policy.Profiles(pawns))
		required, known := roundsProjectWork(definitions, reading.Projection.Definitions).Value()
		if known {
			// Bench work (open bills, a deficit's standing benches) counts
			// toward coverage here so the work goal assesses a deficit the
			// planner then covers; a failed census leaves coverage unknown.
			native := r.native.(RoundsWorkBenchSource)
			benches := r.benchSource(native, expected, true)
			recovered, _ := policy.ResourceTargetNeed(resourceTargets, reading.Projection.Facts.Resources)
			deficit, deficitKnown := recovered.Value()
			targets, err := roundsDeficitTargets(resourceTargets, deficitKnown && !deficit, reading.Projection.Facts.Gear)
			if err != nil {
				return store.RoundsResult{}, err
			}
			benchWork, err := roundsBenchWork(ctx, benches, state.Snapshot, plans, playerPlans, targets, len(targets) > 0, reading.Projection.Facts.Items.Wort)
			if err != nil {
			}
			var rows []policy.WorkRequirement
			rows, known = benchWork.Value()
			required = mergeWorkRequirements(required, rows)
			required = mergeWorkRequirements(required, roundsResearchWork(policy.ArmorResearchPolicy(r.policy, previous.Latches.Soldiers || policy.GearSoldierPresent(reading.Projection.Facts.Gear)), needs, reading.Projection.Facts.Research))
			required = mergeWorkRequirements(required, fishingWork(reading.Projection))
			study, studyKnown := studyWork(ctx, reading.Projection).Value()
			required = mergeWorkRequirements(required, study)
			known = known && studyKnown
		}
		if known {
			demand, err := roundsDiseaseDemand(reading.Projection, definitions, previous, state.Snapshot)
			if err != nil {
				return store.RoundsResult{}, err
			}
			work, err := policy.PlanWork(pawns, required, demand)
			if err == nil {
				reading.Projection.Facts.WorkCoverage = work.Matches
				// A timetable behind its role template is a work
				// deficit the same review corrects (#417).
				if matches, ok := work.Matches.Value(); ok && matches {
					meditate, _ := reading.Projection.MeditateAvailable.Value()
					for _, row := range policy.PlanSchedulesHeld(pawns, reading.Projection.Facts.Comfort, meditate, policy.HeldOffSleep(reading.Projection.Facts)).Schedules {
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
	if r.methodEnabled(policy.MaintainMechs) {
		gestation, known, err := mechGestation(ctx, r.native, boundary.Identity(state.Snapshot), reading.Projection)
		if err != nil {
			return store.RoundsResult{}, err
		}
		if known {
			if reading.Projection.Facts.MechGestationOwed, err = policy.MechGestationOwed(gestation); err != nil {
				return store.RoundsResult{}, err
			}
		}
	}
	reading.Projection.Facts.Upkeep.Rooms = reading.Projection.Rooms
	reading.Projection.Facts.CleaningContext(reading.Projection.Identity.Tick)
	if err = p.current(ctx, epoch); err != nil {
		return store.RoundsResult{}, err
	}
	if p.session.State() != state {
		return store.RoundsResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	// Manual cancels ctx before waiting for this gate, then invalidates any
	// completed review before returning. Never hold the local stop mutex for SQL.
	if r.methodEnabled(policy.ClearHomeObstructions) {
		source, ok := r.native.(observation.ClearanceSource)
		if !ok {
			return store.RoundsResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
		}
		ground := plannedGround(reading.Projection)
		clearance, err := observation.ObserveClearanceCensusOnGround(ctx, source, expected, true, ground)
		if err != nil {
			return store.RoundsResult{}, err
		}
		if census, known := clearance.Value(); known {
			others, player := policy.SplitGroundRows(census.Targets)
			reading.Projection.Facts.Upkeep.Clearance = domain.Known(others)
			reading.Projection.Facts.Upkeep.Chunks = domain.Known(census.Chunks)
			reading.Projection.Facts.Upkeep.Ground = domain.Known(plannedGroundWork(reading.Projection, player, census.Floors, r.clearFloors(reading.Projection)))
			if err := r.dropClearedRetiredGround(ctx, state.Snapshot, &reading.Projection, player, census.Floors); err != nil {
				return store.RoundsResult{}, err
			}
		}
	}
	if r.methodEnabled(policy.ClearAncientShrine) {
		source, ok := r.native.(observation.ShrineSource)
		if !ok {
			return store.RoundsResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
		}
		if reading.Projection.Facts.Upkeep.Shrines, err = observation.ObserveShrines(ctx, source, expected); err != nil {
			return store.RoundsResult{}, err
		}
		// The breach judgement (#457) is journalled beside the review so the
		// hold reason and the chosen wall are readable; the planner re-reads
		// before drafting anyone. The reads only happen for a sealed shrine
		// with a breach wall.
		if reading.Projection.Facts.ShrineHolds, reading.Projection.Facts.Upkeep.ShrinePolicy, err = roundsShrineHolds(ctx, r.native, state.Snapshot, reading.Projection); err != nil {
			return store.RoundsResult{}, err
		}
	}
	reading.Projection.Facts.AvailableMethods = r.methods
	result, err := p.journal.ReviewRounds(ctx, store.RoundsRequest{Revision: previous.Revision, Current: state.Snapshot, Tick: reading.Projection.Identity.Tick, Enabled: true, Policy: r.policy, Facts: reading.Projection.Facts, PartialPlanners: partial})
	if err != nil {
	} else {
		clockEvent(ctx, "routine", "rounds_review", "rounds ran", append(append([]any{"revision", result.Review.Revision, "previous_revision", previous.Revision, "tick", int64(reading.Projection.Identity.Tick), "concerns", len(result.Standards) + len(result.Projects), "emergency", roundsEmergencyNames(result.Emergency)}, roundsStageAttrs(result.Review.Stage)...), append(roundsDevelopmentAttrs(result.Review.Development), roundsFoodAttrs(reading.Projection.Facts, r.seasonal(reading.Projection.Facts))...)...)...)
		r.logColonyStage(ctx, result.Review)
		recordRoundsSnapshot(ctx, state.Snapshot, reading.Projection.Identity.Tick, result, reading.Projection)
		r.drawSafetyOverlay(ctx, state.Snapshot, &reading.Projection, reading.Emergency)
		r.drawStockOverlay(ctx, state.Snapshot, &reading.Projection, result)
		r.drawSpotOverlay(ctx, state.Snapshot, &reading.Projection)
	}
	return result, err
}

// roundsStageAttrs are the review row's colony stage attrs (#630): the
// stage the review derived and the first unmet condition of the next.
func roundsStageAttrs(stage *policy.ColonyStageRecord) []any {
	if stage == nil {
		return nil
	}
	return []any{"stage", stage.Stage.String(), "stage_blocker", string(stage.Blocker), "stage_reason", stage.Reason, "stage_held", stage.Held}
}

// roundsFoodAttrs are the review row's stored-food attrs: the food runway
// the review read and the seasonal thresholds it held it to, with the
// calendar they came from (#229). An unknown fact leaves its attr out, so
// a sustained run's samples are exactly the reviews that had one.
func roundsFoodAttrs(f policy.RoundsFacts, seasonal policy.RoundsPolicy) []any {
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
// roundsEmergencyNames renders the needs that suspended the review's
// goals for an event attr.
func roundsEmergencyNames(ids []policy.ConcernID) []string {
	out := make([]string, 0, len(ids))
	for _, id := range ids {
		out = append(out, string(id))
	}
	return out
}

func (p *Player) stopRounds(ctx context.Context) (store.RoundsResult, error) {
	previous, err := p.journal.LoadRounds(ctx)
	if err != nil {
		return store.RoundsResult{}, err
	}
	if previous.Revision == 0 || !previous.Enabled {
		return store.RoundsResult{Review: previous}, nil
	}
	return p.journal.ReviewRounds(ctx, store.RoundsRequest{Revision: previous.Revision, Current: previous.Snapshot, Tick: previous.Tick, Enabled: false})
}

// recordRoundsSnapshot writes the review's colony snapshot when
// snapshot.DirEnv names a directory (#742); a failed write is logged, never
// the review's error.
func recordRoundsSnapshot(ctx context.Context, current domain.GenerationSnapshot, tick domain.Tick, result store.RoundsResult, reading observation.ColonyProjection) {
	dir := os.Getenv(snapshot.DirEnv)
	if dir == "" {
		return
	}
	snapshot.Later(func() {
		if recorded, ok := snapshot.FromReview(current, tick, result, reading); ok {
			if err := snapshot.Record(dir, recorded); err != nil {
				telemetry.Decide(ctx, snapshotSkipDecision("routine", "colony", err, tick))
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
	ground, known := colonyGround(colony)
	if !known {
		return nil
	}
	return policy.PlannedGround(plan, ground)
}

// retiredGround is the recorded plan's retired ground (#2075) with its kept
// rooms' walls; empty while the plan is unknown.
func retiredGround(colony observation.ColonyProjection) policy.RetiredGround {
	plan, _ := colony.LayoutPlan.Value()
	return policy.RetiredGroundOf(plan)
}

// dropClearedRetiredGround records the plan without the retired ground the
// census found clear (#2075); the projection carries the recorded plan.
func (r *Rounder) dropClearedRetiredGround(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, player []policy.ClearanceTarget, floors []policy.ClearanceFloor) error {
	plan, known := projection.LayoutPlan.Value()
	if !known || len(plan.RetiredGround) == 0 {
		return nil
	}
	done := policy.RetiredGroundDone(plan, player, floors)
	if len(done) == 0 {
		return nil
	}
	next := plan.WithoutRetiredGround(done)
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, projection.Identity.Tick, next); err != nil {
		return err
	}
	projection.LayoutPlan = domain.Known(next)
	return nil
}

// roundsDevelopmentAttrs is the review row's development ranking: each row's
// concern with the reason it holds no slot ("selected" when it has one), so a
// concern that never acts shows why at Info instead of only as a refused
// admission.
func roundsDevelopmentAttrs(d store.RoundsDevelopment) []any {
	rows := make([]string, 0, len(d.Rows))
	for _, row := range d.Rows {
		verdict := string(row.Reason)
		if row.Selected {
			verdict = "selected"
		}
		rows = append(rows, fmt.Sprintf("%s=%s", row.Concern, verdict))
	}
	return []any{"development", strings.Join(rows, " "), "development_limiting", string(d.Limiting)}
}
