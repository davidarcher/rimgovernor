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
	if reviewer.temperatureEnabled() {
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
	return r.step(call, epoch, newStepArbiter())
}

// step is also usable by a scheduler already holding the player gate.
func (r *RoutineReviewer) step(ctx, epoch context.Context, arbiter *stepArbiter) (store.RoutineReviewResult, error) {
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
	identity, _, err := r.native.Identity(ctx)
	if err != nil {
		clockSchedulerLog("routine.step: native.Identity err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		clockSchedulerLog("routine.step: DecodeIdentity err=%v", err)
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
	definitions := routineProjectDefinitions(plans, state.Snapshot)
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
	if r.temperatureEnabled() {
		observe = observation.ObserveRoutineTemperature
	}
	reading, err := observe(ctx, r.native, r.clock, expected, r.maxAge, claims, definitions...)
	if err != nil {
		clockSchedulerLog("routine.step: observe err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency)
	if err != nil {
		clockSchedulerLog("routine.step: NewEmergencySnapshot err=%v", err)
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.Hostiles, reading.Projection.Facts.CriticalPatients = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	// Owned drafts belong to this persistent controller's shared journal.
	// Use the same complete catalog and cleanup predicate as the release sweep.
	cleanup := false
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			cleanup = cleanup || draftOutstanding(progress)
		}
	}
	reading.Projection.Facts.CleanupPawns = domain.Known(cleanup)
	reading.Projection.ApplyFieldBudget(r.policy.FoodTargetDays)
	if pawns, known := reading.Projection.WorkPawns.Value(); known {
		reading.Projection.Facts.Workers = policy.RoutineWorkers(pawns)
		required, known := routineProjectWork(definitions, reading.Projection.Definitions).Value()
		if known {
			work, err := policy.AssignWork(pawns, required, preferences.Overrides)
			if err == nil {
				reading.Projection.Facts.WorkCoverage = work.Matches
			}
		}
	}
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
	result, err := p.journal.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: previous.Revision, WorkPreferenceRevision: preferences.Revision, Current: state.Snapshot, Tick: reading.Projection.Identity.Tick, Enabled: true, Policy: r.policy, Facts: reading.Projection.Facts})
	if err != nil {
		clockSchedulerLog("routine.step: ReviewRoutine err=%v", err)
	} else {
		clockSchedulerLog("routine.step: ReviewRoutine ok newRevision=%d", result.Review.Revision)
	}
	return result, err
}

// Caller holds the player gate. Invalidating existing work needs no native read,
// including when a stale browser request stops a different observed world.
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
