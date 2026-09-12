package buildingruntime

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
	rules   []policy.ResourceRule
}

// RoutineCapabilities is the runtime's complete configured method set. Omitting
// it leaves availability unspecified for callers that compose methods themselves.
type RoutineCapabilities struct{ Methods []policy.GoalID }

func NewRoutineReviewer(player *Player, native observation.RoutineSource, clock observation.Clock, thresholds policy.RoutinePolicy, maxAge time.Duration, capabilities ...RoutineCapabilities) (*RoutineReviewer, error) {
	if player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > time.Minute {
		return nil, ErrControl
	}
	rules := player.session.ResourceRules()
	if err := policy.ValidateResourceRules(rules); err != nil {
		return nil, err
	}
	methods := domain.Unknown[[]policy.GoalID]()
	if len(capabilities) > 1 {
		return nil, ErrControl
	}
	if len(capabilities) == 1 {
		methods = domain.Known(append([]policy.GoalID{}, capabilities[0].Methods...))
		if _, err := policy.DetectRoutine(policy.RoutineFacts{AvailableMethods: methods}, policy.RoutineLatches{}, thresholds); err != nil {
			return nil, err
		}
	}
	return &RoutineReviewer{methods: methods, player: player, native: native, clock: clock, policy: thresholds, maxAge: maxAge, rules: append([]policy.ResourceRule(nil), rules...)}, nil
}

func (r *RoutineReviewer) Step(ctx context.Context) (store.RoutineReviewResult, error) {
	call, epoch, done, err := r.player.enter(ctx, false)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

// step is also usable by a scheduler already holding the player gate.
func (r *RoutineReviewer) step(ctx, epoch context.Context) (store.RoutineReviewResult, error) {
	p := r.player
	state := p.session.State()
	if !state.Enabled {
		return p.stopRoutine(ctx)
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return store.RoutineReviewResult{}, ErrControl
	}
	previous, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	identity, _, err := r.native.Identity(ctx)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	native, known := expected.NativeGeneration.Value()
	if expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map || !known || native != state.Snapshot.Native {
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
		return store.RoutineReviewResult{}, err
	}
	reading, err := observation.ObserveRoutineOwned(ctx, r.native, r.clock, expected, r.maxAge, claims, definitions...)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency)
	if err != nil {
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
		return store.RoutineReviewResult{}, err
	}
	if p.session.State() != state {
		return store.RoutineReviewResult{}, ErrControl
	}
	// Manual cancels ctx before waiting for this gate, then invalidates any
	// completed review before returning. Never hold the local stop mutex for SQL.
	reading.Projection.Facts.AvailableMethods = r.methods
	return p.journal.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: previous.Revision, WorkPreferenceRevision: preferences.Revision, Current: state.Snapshot, Tick: reading.Projection.Identity.Tick, Enabled: true, Policy: r.policy, Facts: reading.Projection.Facts})
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
