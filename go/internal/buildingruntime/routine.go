package buildingruntime

import (
	"context"
	"errors"
	"math"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineReviewer observes and journals needs under Player's existing gate.
// It neither acquires authority nor creates methods or game orders.
type RoutineReviewer struct {
	methods   domain.Fact[[]policy.GoalID]
	player    *Player
	native    observation.RoutineSource
	clock     observation.Clock
	policy    policy.RoutinePolicy
	maxAge    time.Duration
	rules     []policy.ResourceRule
	longitude domain.Fact[float64]
	// census retains the latest review reading for the planners of the same
	// tick; see routineCensus.
	census routineCensusStore
}

// seasonal is the configured policy with its food and wood targets widened
// by the calendar the reading carries (policy.RoutinePolicy.Seasonal): the
// same targets DetectRoutine measures the latches against, so a planner's
// deficit, field budget and butcher gate agree with the review.
func (r *RoutineReviewer) seasonal(facts policy.RoutineFacts) policy.RoutinePolicy {
	return r.policy.Seasonal(facts.Calendar, facts.DisasterConditions)
}

// RoutineCapabilities is the runtime's complete configured method set. Omitting
// it leaves availability unspecified for callers that compose methods themselves.
//
// Longitude is the colony's home map-tile longitude (bridge.WorldRead.Longitude),
// the map-local-hour ingredient boundary.HourOfDay/ExpectedScheduleDef need to
// fence EnsureMood-* relief dispatch against a pawn's current timetable
// assignment. It is resolved once by the caller (the colony's map tile never
// moves within a session) rather than re-read on every Step, unlike every
// other RoutineSource fact which is re-derived fresh each tick because it can
// genuinely change; Longitude cannot, so caching it here avoids two wasted
// native round trips (map lookup, then world-tile lookup) every review.
type RoutineCapabilities struct {
	Methods   []policy.GoalID
	Longitude domain.Fact[float64]
}

func NewRoutineReviewer(player *Player, native observation.RoutineSource, clock observation.Clock, thresholds policy.RoutinePolicy, maxAge time.Duration, capabilities ...RoutineCapabilities) (*RoutineReviewer, error) {
	if player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > time.Minute {
		return nil, ErrControl
	}
	rules := player.session.ResourceRules()
	if err := policy.ValidateResourceRules(rules); err != nil {
		return nil, err
	}
	methods := domain.Unknown[[]policy.GoalID]()
	longitude := domain.Unknown[float64]()
	if len(capabilities) > 1 {
		return nil, ErrControl
	}
	if len(capabilities) == 1 {
		methods = domain.Known(append([]policy.GoalID{}, capabilities[0].Methods...))
		if _, err := policy.DetectRoutine(policy.RoutineFacts{AvailableMethods: methods}, policy.RoutineLatches{}, thresholds); err != nil {
			return nil, err
		}
		if lon, known := capabilities[0].Longitude.Value(); known {
			if math.IsNaN(lon) || math.IsInf(lon, 0) || lon < -180 || lon > 180 {
				return nil, ErrControl
			}
			longitude = capabilities[0].Longitude
		}
	}
	reviewer := &RoutineReviewer{methods: methods, player: player, native: native, clock: clock, policy: thresholds, maxAge: maxAge, rules: append([]policy.ResourceRule(nil), rules...), longitude: longitude}
	if reviewer.roomsEnabled() {
		if _, ok := native.(observation.TemperatureSource); !ok {
			return nil, ErrControl
		}
	}
	return reviewer, nil
}

func (r *RoutineReviewer) Step(ctx context.Context) (store.RoutineReviewResult, error) {
	call, epoch, done, err := r.player.enter(ctx, false)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter(), false)
}

// step is also usable by a scheduler already holding the player gate;
// partial says only a wake's planners follow the review.
func (r *RoutineReviewer) step(ctx, epoch context.Context, arbiter *stepArbiter, partial bool) (store.RoutineReviewResult, error) {
	p := r.player
	state := p.session.State()
	if !state.Enabled {
		clockSchedulerLog("routine.step: state not enabled -> stopRoutine")
		return p.stopRoutine(ctx)
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		clockSchedulerLog("routine.step: ErrControl observationKnown=%v snapshotValidate=%v native=%d", state.ObservationKnown, state.Snapshot.Validate(), state.Snapshot.Native)
		return store.RoutineReviewResult{}, ErrControl
	}
	previous, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		clockSchedulerLog("routine.step: LoadRoutineReview err=%v", err)
		return store.RoutineReviewResult{}, err
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
		return store.RoutineReviewResult{}, ErrControl
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
	preferences, err := p.journal.LoadWorkPreferences(ctx, state.Snapshot.Plan)
	if errors.Is(err, store.ErrNotFound) {
		// Directly created plans have no player submission or saved overrides.
		preferences = store.WorkPreferences{Plan: state.Snapshot.Plan, World: store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}, Overrides: []policy.WorkOverride{}}
		err = nil
	}
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	if preferences.World != (store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}) {
		return store.RoutineReviewResult{}, ErrControl
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
	reading, err := observe(ctx, r.native, r.clock, expected, r.maxAge, claims, definitions...)
	if err != nil {
		clockSchedulerLog("routine.step: observe err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	r.census.retain(reading, r.roomsEnabled(), claims)
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency)
	if err != nil {
		clockSchedulerLog("routine.step: NewEmergencySnapshot err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.Hostiles, reading.Projection.Facts.CriticalPatients = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	reading.Projection.Facts.UrgentPatients = policy.UrgentPatients(emergency, state.Snapshot, expected.Tick)
	// Owned drafts belong to this persistent controller's shared journal.
	// Use the same complete catalog and cleanup predicate as the release sweep.
	cleanup := false
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			cleanup = cleanup || draftOutstanding(progress)
		}
	}
	reading.Projection.Facts.CleanupPawns = domain.Known(cleanup)
	reading.Projection.ApplyFieldBudget(r.seasonal(reading.Projection.Facts).FoodTargetDays)
	// The workshop ladder's recorded research rung is the derived
	// EnsureResearch target; it is journal evidence, not a native read, so
	// a review costs no extra call for it.
	needs, err := routineResearchNeeds(ctx, p.journal, r.policy, state.Snapshot)
	if err != nil {
		clockSchedulerLog("routine.step: LoadProductionLadder err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.ResearchNeeds = needs
	reading.Projection.Facts.DefensiveLayoutStanding, reading.Projection.Facts.ResourceNeeds, err = routineDefensiveLayoutStanding(ctx, p.journal, r.policy, state.Snapshot)
	if err != nil {
		clockSchedulerLog("routine.step: LoadDefenseLayout err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	resourceTargets, err := r.policy.EffectiveResourceTargets(reading.Projection.Facts.Resources, reading.Projection.Facts.ResourceNeeds)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	if pawns, known := reading.Projection.WorkPawns.Value(); known {
		reading.Projection.Facts.Workers = policy.RoutineWorkers(pawns)
		reading.Projection.Facts.Labor = policy.RoutineLabor(pawns)
		required, known := routineProjectWork(definitions, reading.Projection.Definitions).Value()
		if known {
			// Bench work (open bills, a deficit's standing benches) counts
			// toward coverage here so the work goal assesses a deficit the
			// planner then covers; a failed census leaves coverage unknown.
			benches, _ := r.native.(RoutineWorkBenchSource)
			recovered, _ := policy.ResourceTargetNeed(resourceTargets, reading.Projection.Facts.Resources)
			deficit, deficitKnown := recovered.Value()
			benchWork, err := routineBenchWork(ctx, benches, state.Snapshot, plans, playerPlans, resourceTargets, deficitKnown && !deficit)
			if err != nil {
				clockSchedulerLog("routine.step: bench work err=%v", err)
			}
			var rows []policy.WorkRequirement
			rows, known = benchWork.Value()
			required = mergeWorkRequirements(required, rows)
			required = mergeWorkRequirements(required, routineResearchWork(r.policy, needs, reading.Projection.Facts.Research))
		}
		if known {
			work, err := policy.AssignWork(pawns, required, preferences.Overrides)
			if err == nil {
				reading.Projection.Facts.WorkCoverage = work.Matches
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
		return store.RoutineReviewResult{}, ErrControl
	}
	// Manual cancels ctx before waiting for this gate, then invalidates any
	// completed review before returning. Never hold the local stop mutex for SQL.
	reading.Projection.Facts.AvailableMethods = r.methods
	result, err := p.journal.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: previous.Revision, WorkPreferenceRevision: preferences.Revision, Current: state.Snapshot, Tick: reading.Projection.Identity.Tick, Enabled: true, Policy: r.policy, Facts: reading.Projection.Facts, PartialPlanners: partial})
	if err != nil {
		clockSchedulerLog("routine.step: ReviewRoutine err=%v", err)
	} else {
		clockEvent("routine", "routine_review", "routine reviewed", "revision", result.Review.Revision, "previous_revision", previous.Revision, "tick", int64(reading.Projection.Identity.Tick), "goals", len(result.Goals), "emergency", routineEmergencyNames(result.Emergency))
	}
	return result, err
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
