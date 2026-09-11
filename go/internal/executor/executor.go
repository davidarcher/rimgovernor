// Package executor owns one guarded building attempt at a time. Runtime must hold
// the colony's process/store writer lease before enabling this local executor.
package executor

import (
	"context"
	"errors"
	"fmt"
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
)

type Journal interface {
	LoadPlan(context.Context, domain.PlanID) (store.PlanState, error)
	ReserveAndPrepare(context.Context, domain.PlanID, domain.ActionID, store.Admission) (domain.Progress, error)
	Dispatch(context.Context, domain.PlanID, domain.ActionID, domain.GenerationSnapshot, domain.Tick) (domain.Progress, error)
	RecordReceipt(context.Context, domain.PlanID, domain.ActionID, domain.AttemptID, domain.Receipt) (domain.Progress, error)
	Observe(context.Context, domain.PlanID, domain.Observation, domain.GenerationSnapshot) (domain.Progress, error)
	Cancel(context.Context, domain.PlanID, domain.ActionID) (domain.Progress, error)
}
type Clock interface{ Now() time.Time }
type Limits struct{ MaxAge, RunTimeout, JournalTimeout time.Duration }
type Authority struct {
	Snapshot domain.GenerationSnapshot
	Enabled  bool
}

type Target struct {
	Action   domain.Action
	Snapshot domain.GenerationSnapshot
}

// Inspection combines current native facts with runtime-owned accounting inputs.
// Current's colony/map/load must come from actual native observation, not copied
// blindly from Target. Tick is shared by preview, stock and dependency evidence.
type Inspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Bounds                domain.Fact[policy.Bounds]
	Preview               policy.Preview
	Stock                 policy.StockObservation
	Held                  []policy.Reservation
	// Held contains other-plan commitments only. Completeness must come from
	// the runtime's accounting owner, never from an empty native response.
	ExternalHoldsComplete bool
	Rules                 []policy.ResourceRule
	admission             store.Admission
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
}

// Evidence is an observation for a specific dispatched attempt. Complete means
// the entire target/effect scope was inspected. Built describes the completed
// native building; a blueprint/frame or uncorrelated matching object cannot prove
// completion. The adapter must establish native attempt attribution.
type Evidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Built                 domain.Fact[domain.Building]
}
type Boundary interface {
	Inspect(context.Context, Target) (Inspection, error)
	Place(context.Context, Placement) (Receipt, error)
	Observe(context.Context, Placement, domain.GenerationSnapshot) (Evidence, error)
}
type Result struct {
	Progress     domain.Progress
	Refused      []policy.Refusal
	NativeCalled bool
}

type Executor struct {
	journal      Journal
	boundary     Boundary
	clock        Clock
	limits       Limits
	writer       chan struct{}
	mu           sync.Mutex
	authority    Authority
	generation   context.Context
	invalidate   context.CancelFunc
	activeAction domain.ActionID
	activeCancel context.CancelFunc
}

func New(journal Journal, boundary Boundary, clock Clock, limits Limits) (*Executor, error) {
	if journal == nil || boundary == nil || clock == nil || limits.MaxAge < 0 || limits.RunTimeout <= 0 || limits.JournalTimeout <= 0 {
		return nil, errors.New("invalid executor dependencies or limits")
	}
	generation, cancel := context.WithCancel(context.Background())
	return &Executor{journal: journal, boundary: boundary, clock: clock, limits: limits, writer: make(chan struct{}, 1), generation: generation, invalidate: cancel}, nil
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
	if authority == e.authority {
		return nil
	}
	e.invalidate()
	e.generation, e.invalidate = context.WithCancel(context.Background())
	e.authority = authority
	return nil
}
func (e *Executor) current() Authority { e.mu.Lock(); defer e.mu.Unlock(); return e.authority }
func (e *Executor) guard(ctx context.Context, expected domain.GenerationSnapshot, generation context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if generation.Err() != nil {
		return ErrAuthority
	}
	current := e.current()
	if !current.Enabled || !current.Snapshot.Matches(expected) {
		return ErrAuthority
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
	if action.Kind() != domain.BuildingAction {
		return Result{}, errors.New("missing or unsupported building action")
	}
	result := Result{Progress: progress}
	if progress.View().Unresolved {
		return e.reconcile(ctx, action, progress, generation)
	}
	switch progress.View().Stage {
	case domain.Completed, domain.Cancelled:
		return result, nil
	case domain.Pending, domain.Prepared:
	default:
		return result, ErrEvidence
	}
	expected := authority.Snapshot
	if expected.Plan != state.Spec.ID() || expected.Revision != state.Spec.Revision() {
		return result, ErrAuthority
	}
	if err = e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	inspection, refusals, err := e.inspect(ctx, Target{action, expected}, progress, generation)
	result.Refused = refusals
	if err != nil {
		return result, err
	}
	progress, err = e.journal.ReserveAndPrepare(ctx, plan, actionID, inspection.admission)
	if err != nil {
		return result, err
	}
	result.Progress = progress
	// Refresh after durable preparation: resources and native safety may have
	// changed while journaling. Prepared restart follows the same fresh admission.
	inspection, refusals, err = e.inspect(ctx, Target{action, expected}, progress, generation)
	result.Refused = refusals
	if err != nil {
		return result, err
	}
	if err = e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	progress, err = e.journal.ReserveAndPrepare(ctx, plan, actionID, inspection.admission)
	if err != nil {
		return result, err
	}
	result.Progress = progress
	if err = e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	progress, err = e.journal.Dispatch(ctx, plan, actionID, expected, inspection.Tick)
	if err != nil {
		return result, err
	}
	result.Progress = progress
	placement := Placement{Action: action, Attempt: progress.View().Attempt, Snapshot: expected, Tick: inspection.Tick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, plan, placement, domain.ReceiptUnknown, err)
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return e.record(result, plan, placement, domain.ReceiptUnknown, ErrHeld)
	}
	result.NativeCalled = true
	receipt, callErr := e.boundary.Place(ctx, placement)
	kind := receipt.Kind
	if callErr != nil {
		kind = domain.ReceiptUnknown
	} else if receipt.Action != actionID || receipt.Attempt != placement.Attempt || !receipt.Snapshot.Matches(expected) {
		kind = domain.ReceiptUnknown
		callErr = ErrEvidence
	} else {
		switch kind {
		case domain.ReceiptAccepted, domain.ReceiptRefused, domain.ReceiptUnknown:
		default:
			kind = domain.ReceiptUnknown
			callErr = ErrEvidence
		}
	}
	return e.record(result, plan, placement, kind, errors.Join(callErr, ctx.Err()))
}

func (e *Executor) inspect(ctx context.Context, target Target, progress domain.Progress, generation context.Context) (Inspection, []policy.Refusal, error) {
	inspection, err := e.boundary.Inspect(ctx, target)
	if err != nil {
		return inspection, nil, err
	}
	if err = e.guard(ctx, target.Snapshot, generation); err != nil {
		return inspection, nil, err
	}
	if !inspection.Current.Matches(target.Snapshot) || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return inspection, nil, ErrHeld
	}
	if !inspection.ExternalHoldsComplete {
		return inspection, nil, ErrHeld
	}
	state, err := e.journal.LoadPlan(ctx, target.Snapshot.Plan)
	if err != nil {
		return inspection, nil, err
	}
	held, err := persistentHolds(state, target.Action.ID(), progress)
	if err != nil {
		return inspection, nil, err
	}
	for _, external := range inspection.Held {
		if external.Progress.View().Plan == state.Spec.ID() {
			return inspection, nil, fmt.Errorf("%w: current-plan holds must come from the journal", ErrEvidence)
		}
		held = append(held, external)
	}
	input, err := policy.NewInput(policy.Request{Current: inspection.Current, CurrentTick: inspection.Tick, Bounds: inspection.Bounds, Stock: inspection.Stock, Held: held, Rules: inspection.Rules, Candidates: []policy.Candidate{{Action: target.Action, Progress: progress, Purpose: policy.Routine, Preview: inspection.Preview}}})
	if err != nil {
		return inspection, nil, fmt.Errorf("%w: %v", ErrEvidence, err)
	}
	decision := policy.Admit(input)
	if len(decision.Admitted) != 1 || decision.Admitted[0].Action != target.Action {
		return inspection, decision.Refused, ErrHeld
	}
	accepted := decision.Admitted[0]
	inspection.admission = store.Admission{Snapshot: accepted.Snapshot, Tick: inspection.Tick, Footprint: append([]domain.Cell(nil), accepted.Footprint...), Costs: make([]store.MaterialCost, len(accepted.Costs))}
	for i, cost := range accepted.Costs {
		inspection.admission.Costs[i] = store.MaterialCost{Definition: string(cost.Resource), Count: cost.Count}
	}
	return inspection, nil, nil
}
func (e *Executor) fresh(start, end time.Time) bool {
	now := e.clock.Now()
	return !start.IsZero() && !end.IsZero() && !end.Before(start) && !now.Before(end) && !now.Before(start) && now.Sub(start) <= e.limits.MaxAge
}
func (e *Executor) record(result Result, plan domain.PlanID, placement Placement, kind domain.Receipt, cause error) (Result, error) {
	// Caller/authority cancellation must not erase the attempt. Only a bounded local
	// journal write uses a fresh context; native calls never outlive their authority.
	ctx, cancel := context.WithTimeout(context.Background(), e.limits.JournalTimeout)
	defer cancel()
	progress, err := e.journal.RecordReceipt(ctx, plan, placement.Action.ID(), placement.Attempt, kind)
	if err == nil {
		result.Progress = progress
	}
	return result, errors.Join(cause, err)
}
func (e *Executor) reconcile(ctx context.Context, action domain.Action, progress domain.Progress, generation context.Context) (Result, error) {
	result := Result{Progress: progress}
	view := progress.View()
	if generation.Err() != nil {
		return result, ErrAuthority
	}
	current := e.current().Snapshot
	if err := current.Validate(); err != nil {
		return result, ErrAuthority
	}
	if view.Snapshot.Colony != current.Colony || view.Snapshot.Map != current.Map || view.Snapshot.Load != current.Load {
		return result, ErrAuthority
	}
	placement := Placement{Action: action, Attempt: view.Attempt, Snapshot: view.Snapshot, Tick: view.Tick}
	evidence, err := e.boundary.Observe(ctx, placement, current)
	if err != nil {
		return result, err
	}
	if err = ctx.Err(); err != nil {
		return result, err
	}
	if generation.Err() != nil || !e.current().Snapshot.Matches(current) || !e.fresh(evidence.StartedAt, evidence.ObservedAt) {
		return result, ErrHeld
	}
	observed := evidence.Observation
	if observed.Action != action.ID() || observed.Attempt != view.Attempt || !observed.Snapshot.Matches(current) {
		return result, ErrEvidence
	}
	switch observed.Effect {
	case domain.EffectAbsent:
		if !evidence.Complete {
			return result, ErrEvidence
		}
	case domain.EffectCompleted:
		built, known := evidence.Built.Value()
		wanted, _ := action.Building()
		if !evidence.Complete || !known || built != wanted {
			return result, ErrEvidence
		}
	case domain.EffectPending, domain.EffectUnknown:
	default:
		return result, ErrEvidence
	}
	progress, err = e.journal.Observe(ctx, view.Plan, observed, current)
	if err == nil {
		result.Progress = progress
	}
	return result, err
}
