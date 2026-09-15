package buildingruntime

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// CaravanJourneyNative is the read-only native surface CaravanJourneyTracker
// needs: the same Identity call every clock-scheduled planner uses to learn
// the current authoritative snapshot, plus the two censuses
// policy.ClassifyCaravanJourney requires. Nothing in this family ever
// dispatches a native write; a tracked caravan's status can only ever be
// observed, never forced, which is what makes "no wrong-map writes" true by
// construction rather than by a runtime check.
type CaravanJourneyNative interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error)
	ReadHomeColonists(context.Context, *c.Identity) (*n.ListPawnsReply, bridge.Result, error)
}

// CaravanJourneyJournal is the tracking-record persistence
// CaravanJourneyTracker needs. *store.Store satisfies it directly.
type CaravanJourneyJournal interface {
	ListActiveCaravanTracking(context.Context) ([]store.CaravanTracking, error)
	ResolveCaravanTracking(context.Context, string) error
	MarkCaravanStuck(ctx context.Context, caravanID string, tick int64, status store.StuckCaravanStatus) error
	ClearCaravanStuck(ctx context.Context, caravanID string) error
}

// CaravanJourneyCapabilities gates the caravan-journey tracking capability;
// see EnableCaravanDeparture (executor package) for why capabilities are
// wired this way rather than inferred from a composed boundary.
type CaravanJourneyCapabilities struct {
	Native CaravanJourneyNative
}

// CaravanJourneyOutcome is one tracked caravan's classification from a
// single Step, kept for observability even when it changes nothing.
type CaravanJourneyOutcome struct {
	CaravanID string
	Status    policy.CaravanJourneyStatus
}

// CaravanJourneyResult reports what one Step observed and resolved.
// Resolved lists only caravans this Step actually marked home; Outcomes
// covers every tracked caravan the census could classify, whatever its
// status.
type CaravanJourneyResult struct {
	Reason   CaravanJourneyReason
	Outcomes []CaravanJourneyOutcome
	Resolved []string
}

type CaravanJourneyReason int

const (
	CaravanJourneyReasonNone CaravanJourneyReason = iota
	CaravanJourneyDisabled
	CaravanJourneyNoneTracked
	CaravanJourneyPolled
)

// CaravanJourneyTracker polls policy.ClassifyCaravanJourney for every
// caravan store.CaravanTracking still lists as away, and reconciles the
// ones it can safely prove home. It is deliberately not a
// RoutineBuildingPlanner: it dispatches nothing, competes with no goal for
// a plan slot, and its only durable effects are store.ResolveCaravanTracking
// and the store.MarkCaravanStuck/ClearCaravanStuck bookkeeping described
// below, all local writes, never a native call. This is what makes the
// exit evidence's "no wrong-map writes" true here: the tracker cannot write
// to native at all, home or foreign map, so there is no wrong map to write
// to.
//
// A caravan classified Stopped, OnForeignMap or Unknown is recorded via
// store.MarkCaravanStuck (SinceTick set once, on first observation) and
// cleared via store.ClearCaravanStuck the moment it is next seen InFlight
// or resolved home. This is the honest ceiling on "failure recovery"
// available today: native's world-progression census can say a caravan's
// crew is visible on some other live map (see policy.CaravanJourneyOnForeignMap)
// but cannot say why a caravan stopped, whether it was ambushed, or when
// (if ever) it will move again, so RimGovernor does not guess. Instead it
// gives an operator or a future slice an inspectable, tick-stamped record
// (store.ListStuckCaravanTracking) of exactly which caravans have gone
// how long without resolving, with no reconciliation or write attempted on
// any of them.
//
// Reconciliation is deliberately minimal: mark the tracking record
// resolved. There is no separate "claim" or reservation on a departed
// crew to release -- FormCaravan's own crew leaves the home map, so every
// home-scoped read (ReadHomeColonists, routine work assignment, and so on)
// already excludes them for as long as they are away, with no bookkeeping
// of RimGovernor's own to undo. Once ClassifyCaravanJourney reports
// CaravanJourneyReturnedHome, ReadHomeColonists already lists the crew
// again on its own, and returned cargo is now ordinary home-map inventory
// that the existing haul/storage routines (RoutineSecureSuppliesPlanner and
// friends) already scan for -- nothing caravan-specific needs to hand it
// off. If a future slice finds returning cargo needs its own claim (for
// example, to stop a haul routine from touching it before some other
// reconciliation step runs), that claim belongs here, gated on the same
// ReturnedHome transition, not invented ad hoc elsewhere.
type CaravanJourneyTracker struct {
	player  *Player
	native  CaravanJourneyNative
	journal CaravanJourneyJournal
	clock   executor.Clock
	maxAge  time.Duration
}

func NewCaravanJourneyTracker(player *Player, native CaravanJourneyNative, journal CaravanJourneyJournal, clock executor.Clock, maxAge time.Duration) (*CaravanJourneyTracker, error) {
	if player == nil || native == nil || journal == nil || clock == nil || maxAge <= 0 {
		return nil, ErrControl
	}
	return &CaravanJourneyTracker{player, native, journal, clock, maxAge}, nil
}

func (t *CaravanJourneyTracker) Step(ctx context.Context) (CaravanJourneyResult, error) {
	call, epoch, done, err := t.player.enter(ctx, false)
	if err != nil {
		return CaravanJourneyResult{}, err
	}
	defer done()
	return t.step(call, epoch, newStepArbiter())
}

func (t *CaravanJourneyTracker) step(call, epoch context.Context, arbiter *stepArbiter) (CaravanJourneyResult, error) {
	active, err := t.journal.ListActiveCaravanTracking(call)
	if err != nil {
		return CaravanJourneyResult{}, err
	}
	if len(active) == 0 {
		return CaravanJourneyResult{Reason: CaravanJourneyNoneTracked}, nil
	}
	state := t.player.State()
	if !state.Enabled {
		return CaravanJourneyResult{Reason: CaravanJourneyDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return CaravanJourneyResult{}, ErrControl
	}
	identity, _, err := t.native.Identity(call)
	if err != nil {
		return CaravanJourneyResult{}, err
	}
	decoded, err := observation.DecodeIdentity(identity)
	if err != nil {
		return CaravanJourneyResult{}, err
	}
	generation, generationKnown := decoded.NativeGeneration.Value()
	if decoded.Colony != state.Snapshot.Colony || decoded.Load != state.Snapshot.Load || decoded.Map != state.Snapshot.Map || !generationKnown || generation != state.Snapshot.Native {
		return CaravanJourneyResult{}, ErrControl
	}
	expected := state.Snapshot
	started := t.clock.Now()
	world, _, err := t.native.ReadWorldProgression(call, boundary.Identity(expected), false)
	if err != nil {
		return CaravanJourneyResult{}, err
	}
	current, err := boundary.Context(world.Context, expected)
	if err != nil {
		return CaravanJourneyResult{}, err
	}
	home, _, err := t.native.ReadHomeColonists(call, boundary.Identity(current))
	if err != nil {
		return CaravanJourneyResult{}, err
	}
	homeObserved := home.GetObserved()
	if homeObserved == nil {
		return CaravanJourneyResult{}, executor.ErrHeld
	}
	if current, err = boundary.Context(homeObserved.Context, current); err != nil {
		return CaravanJourneyResult{}, err
	}
	if homeObserved.Context.GetTick() < world.Context.GetTick() {
		return CaravanJourneyResult{}, executor.ErrEvidence
	}
	roster := make(map[domain.PawnID]bool, len(homeObserved.Pawns))
	for _, row := range homeObserved.Pawns {
		if row == nil || row.Pawn == nil {
			return CaravanJourneyResult{}, executor.ErrEvidence
		}
		roster[domain.PawnID(row.Pawn.GetId())] = true
	}
	byID := make(map[string]bridge.CaravanJourney, len(world.Caravans))
	for _, journey := range world.Caravans {
		byID[journey.ID] = journey
	}
	foreign := make(map[domain.PawnID]bool)
	for _, m := range world.Maps {
		if m.Home {
			continue
		}
		for _, pawn := range m.PawnIDs {
			foreign[domain.PawnID(pawn)] = true
		}
	}
	tick := world.Context.GetTick()
	result := CaravanJourneyResult{Reason: CaravanJourneyPolled}
	var resolve []string
	for _, tracked := range active {
		journey, found := byID[tracked.CaravanID]
		status := policy.ClassifyCaravanJourney(tracked.Crew, found, journey.Moving, roster, foreign)
		result.Outcomes = append(result.Outcomes, CaravanJourneyOutcome{CaravanID: tracked.CaravanID, Status: status})
		switch status {
		case policy.CaravanJourneyReturnedHome:
			resolve = append(resolve, tracked.CaravanID)
		case policy.CaravanJourneyInFlight:
			if err = t.journal.ClearCaravanStuck(call, tracked.CaravanID); err != nil {
				return CaravanJourneyResult{}, err
			}
		case policy.CaravanJourneyStopped, policy.CaravanJourneyOnForeignMap, policy.CaravanJourneyUnknown:
			if err = t.journal.MarkCaravanStuck(call, tracked.CaravanID, tick, stuckStatus(status)); err != nil {
				return CaravanJourneyResult{}, err
			}
		}
	}
	if len(resolve) == 0 {
		return result, nil
	}
	elapsed := t.clock.Now().Sub(started)
	if t.player.State() != state || elapsed < 0 || elapsed > t.maxAge {
		return CaravanJourneyResult{}, executor.ErrHeld
	}
	for _, id := range resolve {
		if err = t.journal.ResolveCaravanTracking(call, id); err != nil {
			return result, err
		}
		if err = t.player.current(call, epoch); err != nil {
			return result, err
		}
		result.Resolved = append(result.Resolved, id)
	}
	return result, nil
}

// stuckStatus maps every non-InFlight, non-ReturnedHome
// policy.CaravanJourneyStatus onto its store.StuckCaravanStatus text.
// Panicking on an unmapped status is deliberate: step's switch only calls
// this for the three statuses listed here, so an unmapped value means a new
// policy.CaravanJourneyStatus was added without updating this tracker.
func stuckStatus(status policy.CaravanJourneyStatus) store.StuckCaravanStatus {
	switch status {
	case policy.CaravanJourneyStopped:
		return store.StuckCaravanStopped
	case policy.CaravanJourneyOnForeignMap:
		return store.StuckCaravanOnForeignMap
	case policy.CaravanJourneyUnknown:
		return store.StuckCaravanUnknown
	default:
		panic("stuckStatus: unmapped caravan journey status")
	}
}
