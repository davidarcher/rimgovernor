// Package executor owns one guarded building attempt at a time. Runtime must hold
// the colony's process/store writer lease before enabling this local executor.
package executor

import (
	"context"
	"errors"
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
	Observe(context.Context, domain.PlanID, domain.Observation, domain.GenerationSnapshot) (domain.Progress, error)
	Cancel(context.Context, domain.PlanID, domain.ActionID) (domain.Progress, error)
	Hold(context.Context, domain.PlanID, domain.ActionID, []domain.HeldReason, domain.Tick) (domain.Progress, error)
}
type Clock interface{ Now() time.Time }
type RoutineScope interface {
	AuthorizeRoutinePlan(context.Context, domain.GenerationSnapshot, domain.GenerationSnapshot) error
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
}

// ZoneJournal records an applied zone_create's receipt with the zone
// identity its evidence named.
type ZoneJournal interface {
	RecordZoneReceipt(context.Context, domain.PlanID, domain.ActionID, domain.AttemptID, string) (domain.Progress, error)
}

type Boundary interface {
	// InspectIntent anchors a building intent's dispatch to the current
	// native tick; WriteIntent sends the intent through Actions/Apply.
	InspectIntent(context.Context, Target) (IntentInspection, error)
	WriteIntent(context.Context, Placement) (Receipt, error)
}
type Result struct {
	Progress     domain.Progress
	Refused      []policy.Refusal
	NativeCalled bool
	// Detail is native's free-text account of an unresolved effect, when
	// the boundary carries one (an acquisition's pending reason, #291); it
	// is evidence for the log, never a decision input.
	Detail string
}

type Executor struct {
	acquisition            AcquisitionBoundary
	acquisitionJournal     AcquisitionJournal
	haul                   HaulBoundary
	haulJournal            HaulJournal
	gearReplace            GearReplaceBoundary
	gearReplaceJournal     GearReplaceJournal
	trade                  TradeBoundary
	tradeJournal           TradeJournal
	mineAcquisition        AcquisitionBoundary
	mineAcquisitionJournal MineAcquisitionJournal
	ranged                 RangedBoundary
	rangedJournal          RangedJournal
	movement               MovementBoundary
	movementJournal        MovementJournal
	routineScope           RoutineScope
	journal                Journal
	draftJournal           DraftJournal
	draft                  DraftBoundary
	meleeJournal           MeleeJournal
	melee                  MeleeBoundary
	boundary               Boundary
	clock                  Clock
	limits                 Limits
	writer                 chan struct{}
	mu                     sync.Mutex
	authority              Authority
	generation             context.Context
	invalidate             context.CancelFunc
	activeAction           domain.ActionID
	activeCancel           context.CancelFunc
	stopped                bool
}

func New(journal Journal, boundary Boundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
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
		e.routineScope = routine[0]
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
		if e.routineScope == nil {
			return ErrAuthority
		}
		if err := e.routineScope.AuthorizeRoutinePlan(ctx, current.Snapshot, expected); err != nil {
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
	if e.activeAction == action && e.activeCancel != nil {
		e.activeCancel()
	}
	e.mu.Unlock()
	return e.journal.Cancel(ctx, plan, action)
}

// Run dispatches at most one native placement OR consumes one later observation.
// It never polls and never retries within the same call. Every invocation reloads
// durable state, so restart and caller cancellation cannot erase uncertainty.
func (e *Executor) Run(ctx context.Context, plan domain.PlanID, actionID domain.ActionID) (Result, error) {
	ctx, cancel := context.WithTimeout(ctx, e.limits.RunTimeout)
	defer cancel()
	e.mu.Lock()
	if e.stopped {
		e.mu.Unlock()
		return Result{}, ErrStopped
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
		return Result{}, ctx.Err()
	}
	if err := ctx.Err(); err != nil {
		return Result{}, err
	}
	e.mu.Lock()
	e.activeAction = actionID
	e.activeCancel = cancel
	e.mu.Unlock()
	defer func() { e.mu.Lock(); e.activeAction = ""; e.activeCancel = nil; e.mu.Unlock() }()
	if generation.Err() != nil {
		return Result{}, ErrAuthority
	}
	state, err := e.journal.LoadPlan(ctx, plan)
	if err != nil {
		return Result{}, err
	}
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
	if action.Kind().IntentMode() && progress.View().Unresolved {
		return e.settleIntent(progress)
	}
	if plainIntents[action.Kind()] {
		return e.runIntent(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.OwnedDraftAction && e.draft != nil {
		return e.runDraft(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.AcquisitionAction && e.acquisition != nil {
		return e.runAcquisition(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.RangedAttackAction && e.ranged != nil {
		return e.runRangedAttack(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.MovementAction && e.movement != nil {
		return e.runMovement(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.HaulAction && e.haul != nil {
		return e.runHaul(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.GearReplaceAction && e.gearReplace != nil {
		return e.runGearReplace(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.TradeAction && e.trade != nil {
		return e.runTrade(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.MeleeAttackAction && e.melee != nil {
		return e.runMelee(ctx, action, progress, authority, generation)
	}
	if action.Kind() == domain.MineAcquisitionAction && e.mineAcquisition != nil {
		return e.runMineAcquisition(ctx, action, progress, authority, generation)
	}
	return Result{}, errors.New("missing or unsupported action")
}

// holdEmergency durably records an emergency-gated, not-yet-dispatched
// action's hold reasons via journal.Hold, deduplicated the same way inspect's
// building branch already does, so a caller polling e.g. /api/plan learns why
// the action is stuck instead of only observing a bare ErrHeld. Shared by
// every emergency-aware family (building, acquisition, bill, mine
// acquisition, supply, work, zone), not just building. Best-effort: a
// durable-write failure here must not mask the emergency hold itself -- the
// action stays Pending/Prepared regardless, so the next inspection
// recomputes and retries recording the reason.
func (e *Executor) holdEmergency(ctx context.Context, plan domain.PlanID, actionID domain.ActionID, decision policy.EmergencyDecision, tick domain.Tick, progress domain.Progress) domain.Progress {
	seen := map[domain.HeldReason]bool{}
	var reasons []domain.HeldReason
	for _, hold := range decision.Holds {
		held := domain.HeldUnknownFacts
		switch hold.Reason {
		case policy.EmergencyUnsafeThreat:
			held = domain.HeldUnsafeThreat
		case policy.EmergencyCriticalMedical:
			held = domain.HeldCriticalMedical
		case policy.EmergencyStaleFacts:
			held = domain.HeldStaleFacts
		}
		if !seen[held] {
			seen[held] = true
			reasons = append(reasons, held)
		}
	}
	if next, err := e.journal.Hold(ctx, plan, actionID, reasons, tick); err == nil {
		return next
	}
	return progress
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
	if journal, ok := e.journal.(ZoneJournal); ok && zone != "" && kind == domain.ReceiptAccepted {
		progress, err = journal.RecordZoneReceipt(ctx, plan, placement.Action.ID(), placement.Attempt, zone)
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
