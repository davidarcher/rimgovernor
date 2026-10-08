// Package executor owns one guarded building attempt at a time. Runtime must hold
// the colony's process/store writer lease before enabling this local executor.
package executor

import (
	"context"
	"errors"
	"slices"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

var (
	ErrAuthority = errors.New("building authority changed or disabled")
	ErrHeld      = errors.New("building execution held")
	ErrEvidence  = errors.New("invalid building evidence")
	ErrStopped   = errors.New("building executor stopped")
)

type Journal interface {
	LoadPlan(context.Context, domain.PlanID) (store.PlanState, error)
	// Prepare is the untyped preparation of an intent-mode action: native
	// re-validates the intent itself, so nothing is admitted in the journal.
	Prepare(context.Context, domain.PlanID, domain.ActionID, domain.GenerationSnapshot, domain.Tick) (domain.Progress, error)
	Dispatch(context.Context, domain.PlanID, domain.ActionID, domain.GenerationSnapshot, domain.Tick) (domain.Progress, error)
	RecordReceipt(context.Context, domain.PlanID, domain.ActionID, domain.AttemptID, domain.Receipt) (domain.Progress, error)
	// RecordZoneReceipt records an applied zone_create's receipt with the
	// zone identity its evidence named.
	RecordZoneReceipt(context.Context, domain.PlanID, domain.ActionID, domain.AttemptID, string) (domain.Progress, error)
	// RecordBillReceipt records an applied bill placement's receipt with the
	// native bill id its evidence named.
	RecordBillReceipt(context.Context, domain.PlanID, domain.ActionID, domain.AttemptID, string) (domain.Progress, error)
	Observe(context.Context, domain.PlanID, domain.Observation, domain.GenerationSnapshot) (domain.Progress, error)
	Cancel(context.Context, domain.PlanID, domain.ActionID) (domain.Progress, error)
	Hold(context.Context, domain.PlanID, domain.ActionID, []domain.HeldReason, domain.Tick) (domain.Progress, error)
	// The batch forms advance many actions in one transaction (#1040);
	// results are in input order, one per item.
	PrepareBatch(context.Context, []store.BatchAttempt) ([]store.BatchResult, error)
	DispatchBatch(context.Context, []store.BatchAttempt) ([]store.BatchResult, error)
	RecordReceipts(context.Context, []store.BatchReceipt) ([]store.BatchResult, error)
}
type Clock interface{ Now() time.Time }
type RoundsScope interface {
	AuthorizeRoundsPlan(context.Context, domain.GenerationSnapshot, domain.GenerationSnapshot) error
}
type Limits struct{ MaxAge, RunTimeout, JournalTimeout time.Duration }
type Authority struct {
	Snapshot domain.GenerationSnapshot
	Enabled  bool
}

type Target struct {
	Action   domain.Action
	Snapshot domain.GenerationSnapshot
}

// Placement's composite identity is the native deduplication/precondition token.
// The native adapter must verify colony/map/load and deduplicate Action+Attempt
// atomically with placement. This package never wires an unguarded bridge call.
type Placement struct {
	Action   domain.Action
	Attempt  domain.AttemptID
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
}
type Receipt struct {
	Action   domain.ActionID
	Attempt  domain.AttemptID
	Snapshot domain.GenerationSnapshot
	Kind     domain.Receipt
	// Zone is the zone an applied zone_create's evidence names.
	Zone string
	// Bill is the native bill an applied production_bill's or surgery's
	// evidence names.
	Bill string
}

type Boundary interface {
	// InspectIntent anchors a building intent's dispatch to the current
	// native tick; WriteIntents sends the intents of one world through one
	// Actions/Apply call and returns their receipts in input order.
	InspectIntent(context.Context, Target) (IntentInspection, error)
	WriteIntents(context.Context, []Placement) ([]Receipt, error)
}
type Result struct {
	Progress     domain.Progress
	Refused      []policy.Refusal
	NativeCalled bool
}

type Executor struct {
	haul            HaulBoundary
	haulJournal     HaulJournal
	trade           TradeBoundary
	tradeJournal    TradeJournal
	movement        MovementBoundary
	movementJournal MovementJournal
	roundsScope     RoundsScope
	journal         Journal
	boundary        Boundary
	clock           Clock
	limits          Limits
	writer          chan struct{}
	mu              sync.Mutex
	authority       Authority
	generation      context.Context
	invalidate      context.CancelFunc
	activeActions   []domain.ActionID
	activeCancel    context.CancelFunc
	stopped         bool
}

func New(journal Journal, boundary Boundary, clock Clock, limits Limits, routine ...RoundsScope) (*Executor, error) {
	if journal == nil || boundary == nil || clock == nil || limits.MaxAge < 0 || limits.RunTimeout <= 0 || limits.JournalTimeout <= 0 {
		return nil, errors.New("invalid executor dependencies or limits")
	}
	generation, cancel := context.WithCancel(context.Background())
	if len(routine) > 1 {
		cancel()
		return nil, errors.New("one routine scope owner required")
	}
	e := &Executor{journal: journal, boundary: boundary, clock: clock, limits: limits, writer: make(chan struct{}, 1), generation: generation, invalidate: cancel}
	if len(routine) == 1 {
		e.roundsScope = routine[0]
	}
	return e, nil
}

// UpdateAuthority invalidates queued and active work. Disabled authority permits
// later observation/reconciliation, but never dispatch. An unloaded world may use
// a zero snapshot while disabled; it cannot establish observation attribution.
func (e *Executor) UpdateAuthority(authority Authority) error {
	if authority.Enabled {
		if err := authority.Snapshot.Validate(); err != nil {
			return err
		}
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.stopped {
		if authority.Enabled {
			return ErrStopped
		}
		return nil
	}
	if authority == e.authority {
		return nil
	}
	e.invalidate()
	e.generation, e.invalidate = context.WithCancel(context.Background())
	e.authority = authority
	return nil
}
func (e *Executor) current() Authority { e.mu.Lock(); defer e.mu.Unlock(); return e.authority }

// Stop permanently rejects new work and joins the current dispatch, including
// its bounded receipt journal write. A timeout means the owner must retain its
// process lock and retry Stop before closing the journal or transport.
func (e *Executor) Stop(ctx context.Context) error {
	e.mu.Lock()
	e.stopped = true
	e.authority.Enabled = false
	e.invalidate()
	if e.activeCancel != nil {
		e.activeCancel()
	}
	e.mu.Unlock()
	select {
	case e.writer <- struct{}{}:
		<-e.writer
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}
func (e *Executor) guard(ctx context.Context, expected domain.GenerationSnapshot, generation context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if generation.Err() != nil {
		return ErrAuthority
	}
	current := e.current()
	if !current.Enabled {
		return ErrAuthority
	}
	if !current.Snapshot.Matches(expected) {
		if e.roundsScope == nil {
			return ErrAuthority
		}
		if err := e.roundsScope.AuthorizeRoundsPlan(ctx, current.Snapshot, expected); err != nil {
			return errors.Join(ErrAuthority, err)
		}
		if generation.Err() != nil || e.current() != current {
			return ErrAuthority
		}
	}
	return nil
}

func (e *Executor) Cancel(ctx context.Context, plan domain.PlanID, action domain.ActionID) (domain.Progress, error) {
	e.mu.Lock()
	if slices.Contains(e.activeActions, action) && e.activeCancel != nil {
		e.activeCancel()
	}
	e.mu.Unlock()
	return e.journal.Cancel(ctx, plan, action)
}

// withPlan takes the writer slot for actions, reloads plan once and runs fn
// under the run timeout, cancelled when authority changes.
func (e *Executor) withPlan(ctx context.Context, plan domain.PlanID, actions []domain.ActionID, fn func(context.Context, store.PlanState, Authority, context.Context) error) error {
	ctx, cancel := context.WithTimeout(ctx, e.limits.RunTimeout)
	defer cancel()
	e.mu.Lock()
	if e.stopped {
		e.mu.Unlock()
		return ErrStopped
	}
	generation := e.generation
	authority := e.authority
	e.mu.Unlock()
	stop := context.AfterFunc(generation, cancel)
	defer stop()
	select {
	case e.writer <- struct{}{}:
		defer func() { <-e.writer }()
	case <-ctx.Done():
		return ctx.Err()
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	e.mu.Lock()
	e.activeActions = actions
	e.activeCancel = cancel
	e.mu.Unlock()
	defer func() { e.mu.Lock(); e.activeActions = nil; e.activeCancel = nil; e.mu.Unlock() }()
	if generation.Err() != nil {
		return ErrAuthority
	}
	state, err := e.journal.LoadPlan(ctx, plan)
	if err != nil {
		return err
	}
	return fn(ctx, state, authority, generation)
}

// find returns the spec action and progress of actionID in state.
func find(state store.PlanState, actionID domain.ActionID) (domain.Action, domain.Progress) {
	var action domain.Action
	var progress domain.Progress
	for _, candidate := range state.Spec.Actions() {
		if candidate.ID() == actionID {
			action = candidate
			break
		}
	}
	for _, candidate := range state.Progress {
		if candidate.View().Action == actionID {
			progress = candidate
			break
		}
	}
	return action, progress
}

func (e *Executor) runLoaded(ctx context.Context, state store.PlanState, actionID domain.ActionID, authority Authority, generation context.Context) (Result, error) {
	action, progress := find(state, actionID)
	if action.Kind().IntentMode() && progress.View().Unresolved {
		return e.settleIntent(progress)
	}
	if action.Kind() == domain.MovementAction && e.movement != nil {
		return e.runMovement(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.HaulAction && e.haul != nil {
		return e.runHaul(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.TradeAction && e.trade != nil {
		return e.runTrade(ctx, action, progress, authority, generation)
	}
	return Result{}, errors.New("missing or unsupported action")
}

// receiptAfterCallError classifies a failed native write: a failure the
// transport proves happened before the call was issued is a no-effect
// ReceiptUnsent; anything else is uncertain and must reconcile.
func receiptAfterCallError(err error) domain.Receipt {
	if errors.Is(err, domain.ErrWriteUnsent) {
		return domain.ReceiptUnsent
	}
	return domain.ReceiptUnknown
}

func (e *Executor) fresh(start, end time.Time) bool {
	now := e.clock.Now()
	return !start.IsZero() && !end.IsZero() && !end.Before(start) && !now.Before(end) && !now.Before(start) && now.Sub(start) <= e.limits.MaxAge
}
func (e *Executor) record(result Result, plan domain.PlanID, placement Placement, kind domain.Receipt, cause error) (Result, error) {
	return e.recordZone(result, plan, placement, kind, "", cause)
}

// recordZone is record naming the zone an applied zone_create created.
func (e *Executor) recordZone(result Result, plan domain.PlanID, placement Placement, kind domain.Receipt, zone string, cause error) (Result, error) {
	// Caller/authority cancellation must not erase the attempt. Only a bounded local
	// journal write uses a fresh context; native calls never outlive their authority.
	ctx, cancel := context.WithTimeout(context.Background(), e.limits.JournalTimeout)
	defer cancel()
	var progress domain.Progress
	var err error
	if zone != "" && kind == domain.ReceiptAccepted {
		progress, err = e.journal.RecordZoneReceipt(ctx, plan, placement.Action.ID(), placement.Attempt, zone)
	} else {
		progress, err = e.journal.RecordReceipt(ctx, plan, placement.Action.ID(), placement.Attempt, kind)
	}
	if err == nil {
		result.Progress = progress
	}
	return result, errors.Join(cause, err)
}

// settleIntent closes an intent-mode attempt dispatched without a recorded
// receipt: nothing is observed, the outcome is recorded unknown and the
// idempotent intent is dispatched again.
func (e *Executor) settleIntent(progress domain.Progress) (Result, error) {
	v := progress.View()
	return e.record(Result{Progress: progress}, v.Plan, Placement{Action: progress.Action(), Attempt: v.Attempt, Snapshot: v.Snapshot, Tick: v.Tick}, domain.ReceiptUnknown, nil)
}
