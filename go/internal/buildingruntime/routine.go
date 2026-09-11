package buildingruntime

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineReviewer observes and journals needs under Player's existing gate.
// It neither acquires authority nor creates methods or game orders.
type RoutineReviewer struct {
	player *Player
	native observation.RoutineSource
	clock  observation.Clock
	policy policy.RoutinePolicy
	maxAge time.Duration
}

func NewRoutineReviewer(player *Player, native observation.RoutineSource, clock observation.Clock, thresholds policy.RoutinePolicy, maxAge time.Duration) (*RoutineReviewer, error) {
	if player == nil || native == nil || clock == nil || thresholds.Validate() != nil || maxAge <= 0 || maxAge > time.Minute {
		return nil, ErrControl
	}
	return &RoutineReviewer{player: player, native: native, clock: clock, policy: thresholds, maxAge: maxAge}, nil
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
	reading, err := observation.ObserveRoutine(ctx, r.native, r.clock, expected, r.maxAge)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency)
	if err != nil {
		return store.RoutineReviewResult{}, err
	}
	reading.Projection.Facts.Hostiles, reading.Projection.Facts.CriticalPatients = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	if err = p.current(ctx, epoch); err != nil {
		return store.RoutineReviewResult{}, err
	}
	if p.session.State() != state {
		return store.RoutineReviewResult{}, ErrControl
	}
	// Manual cancels ctx before waiting for this gate, then invalidates any
	// completed review before returning. Never hold the local stop mutex for SQL.
	return p.journal.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: previous.Revision, Current: state.Snapshot, Tick: reading.Projection.Identity.Tick, Enabled: true, Policy: r.policy, Facts: reading.Projection.Facts})
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
