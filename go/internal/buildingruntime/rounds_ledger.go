package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// OrderDeclarer is what a bill planner implements to put its production
// orders in the work ledger. A migrated planner declares the orders it wants
// standing each Round and commits no bill method of its own: the ledger Concern
// (policy.MaintainWorkLedger) places, keeps and removes the bills.
//
// DeclareOrders is called once per review, on the review's projection and the
// bench readback the ledger reconciles against. It returns the planner's whole
// wanted set (an order it omits is an orphan after policy.OrphanGraceRounds
// Rounds) and sets Declared.Abstain when it lacked the facts to say, which
// stops orphan removal for that Round. It writes nothing; a facts-only native
// read for what the projection lacks is allowed, and a read that fails is an
// abstain, not an error.
type OrderDeclarer interface {
	DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error)
}

// workLedger is the Rounder's ledger memory: the declarers, the orphan counters
// and the reconcile plan of the latest review. All of it is derived from the
// game and the planners, so a restart empties it and only delays removal.
type workLedger struct {
	mu        sync.Mutex
	declarers []OrderDeclarer
	world     domain.GenerationSnapshot
	orphans   map[string]int
	pending   *ledgerPending
	// batches are the declared finite batches of the latest review that no
	// declarer abstained from: ingredient demand (Rounder.openBills).
	batches []policy.OrderSpec
	// dispatch is the bench dispatcher's calibration memory and unmet its
	// latest unmet throughput per bench kind (RoundsFacts.UnmetThroughput).
	dispatch policy.DispatchMemory
	unmet    []policy.UnmetThroughput
	// colonists sizes the further-bench cap (policy.BenchCap).
	colonists int
	// view is the latest review's read-only projection (LedgerView).
	view policy.LedgerView
}

// ledgerUnmet is the latest review's unmet throughput per bench kind.
func (r *Rounder) ledgerUnmet() []policy.UnmetThroughput {
	r.ledger.mu.Lock()
	defer r.ledger.mu.Unlock()
	return r.ledger.unmet
}

// ledgerFurtherBenches is the bench kinds the latest review's unmet throughput
// says need another bench (policy.FurtherBenchKinds) against the standing ones.
func (r *Rounder) ledgerFurtherBenches(benches []policy.GearBench) []string {
	r.ledger.mu.Lock()
	defer r.ledger.mu.Unlock()
	return policy.FurtherBenchKinds(r.ledger.unmet, benches, r.ledger.colonists)
}

// ledgerPending is a review's reconcile plan, committed by the ledger planner
// at most once.
type ledgerPending struct {
	snapshot domain.GenerationSnapshot
	remove   []policy.ActualBill
	place    []policy.LedgerPlacement
	unplaced []policy.OrderSpec
}

func (p *ledgerPending) actions() int { return len(p.remove) + len(p.place) }

// AddOrderDeclarer registers a planner's declarations. With no declarer the
// ledger is inactive: it reads nothing and removes nothing.
func (r *Rounder) AddOrderDeclarer(d OrderDeclarer) {
	r.ledger.mu.Lock()
	defer r.ledger.mu.Unlock()
	r.ledger.declarers = append(r.ledger.declarers, d)
}

// reviewLedger is one Round's declare and readback: it collects the
// declarations, reads every bench's bills through the bench census, reconciles
// them and keeps the plan for the ledger planner. The finding is the diff:
// owed while it places or removes anything, unknown when the readback is.
func (r *Rounder) reviewLedger(ctx context.Context, snapshot domain.GenerationSnapshot, expected observation.Identity, projection observation.ColonyProjection) (domain.Fact[bool], error) {
	l := &r.ledger
	l.mu.Lock()
	declarers := append([]OrderDeclarer(nil), l.declarers...)
	if len(declarers) == 0 {
		l.batches = nil
	}
	l.pending = nil
	if l.world != snapshot && (l.world.Colony != snapshot.Colony || l.world.Load != snapshot.Load || l.world.Map != snapshot.Map) {
		l.orphans = nil
		l.dispatch = policy.DispatchMemory{}
	}
	l.world = snapshot
	l.unmet, l.colonists = nil, 0
	l.view = policy.NewLedgerView(policy.LedgerViewInactive, int64(projection.Identity.Tick))
	l.mu.Unlock()
	native, ok := r.native.(RoundsWorkBenchSource)
	if len(declarers) == 0 || !ok {
		return domain.Unknown[bool](), nil
	}
	reads, _, err := r.benchSource(native, expected, false).ReadGearBenches(ctx, boundary.Identity(snapshot))
	if err != nil {
		if ctx.Err() != nil {
			return domain.Unknown[bool](), ctx.Err()
		}
		l.mu.Lock()
		l.view = policy.NewLedgerView(policy.LedgerViewUnread, int64(projection.Identity.Tick))
		l.mu.Unlock()
		return domain.Unknown[bool](), nil
	}
	benches := make([]policy.GearBench, 0, len(reads))
	for _, read := range reads {
		benches = append(benches, read.Bench)
	}
	declared := make([]policy.Declared, 0, len(declarers))
	named := make([]policy.NamedDeclared, 0, len(declarers))
	for _, d := range declarers {
		one, err := d.DeclareOrders(ctx, snapshot, projection, benches)
		if err != nil {
			return domain.Unknown[bool](), err
		}
		declared = append(declared, one)
		named = append(named, policy.NamedDeclared{Name: declarerName(d), Declared: one})
	}
	actual, known := policy.LedgerActuals(benches)
	l.mu.Lock()
	defer l.mu.Unlock()
	l.noteBatches(declared)
	if !known {
		l.view = policy.NewLedgerView(policy.LedgerViewUnread, int64(projection.Identity.Tick))
		for _, d := range named {
			l.view.Declarers = append(l.view.Declarers, policy.LedgerDeclarerView{Name: d.Name, Orders: len(d.Orders), Abstain: d.Abstain})
		}
		return domain.Unknown[bool](), nil
	}
	var pawns []policy.WorkPawn
	if rows, ok := projection.WorkPawns.Value(); ok {
		pawns = rows
	}
	copies, dispatched := l.dispatch.Dispatch(policy.DispatchInputs{Declared: declared, Benches: benches, Pawns: pawns, Stock: projection.Resources, Tick: int64(projection.Identity.Tick)})
	plan := policy.ReconcileLedger(declared, actual, l.orphans, copies)
	l.orphans = plan.Orphans
	placed, unplaced := policy.PlaceLedgerOrders(plan.Place, benches)
	wanted, _ := policy.WantedOrders(declared)
	l.unmet = l.dispatch.Unmet(dispatched, wanted, unplaced, benches)
	l.colonists = len(pawns)
	l.view = policy.BuildLedgerView(policy.LedgerViewInput{Tick: int64(projection.Identity.Tick), Declarers: named, Plan: plan, Placed: placed, Unplaced: unplaced,
		Dispatch: dispatched, Memory: l.dispatch, Stock: projection.Resources, Unmet: l.unmet,
		Further: policy.FurtherBenchKinds(l.unmet, benches, l.colonists)})
	if policy.LedgerDiffOwed(plan) {
		l.pending = &ledgerPending{snapshot: snapshot, remove: plan.Remove, place: placed, unplaced: unplaced}
	}
	return domain.Known(policy.LedgerDiffOwed(plan)), nil
}

// RoundsLedgerPlanner commits MaintainWorkLedger's method: the Round's
// reconcile plan as one batched plan of production_bill and
// remove_production_bill actions, under one atomic arbiter claim.
type RoundsLedgerPlanner struct {
	reviewer *Rounder
}

// RoundsLedgerResult is the ledger planner's step result.
type RoundsLedgerResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsLedgerPlanner(reviewer *Rounder) (*RoundsLedgerPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoundsLedgerPlanner: reviewer == nil", ErrControl)
	}
	return &RoundsLedgerPlanner{reviewer: reviewer}, nil
}

func (l *RoundsLedgerPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsLedgerResult, error) {
	r := l.reviewer
	p := r.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsLedgerResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoundsLedgerResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsLedgerResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsLedgerResult{Verdict: BuildingReasonNoReview}, nil
	}
	owner, workable, err := p.journal.WorkableOwner(call, review, policy.MaintainWorkLedger)
	if err != nil {
		return RoundsLedgerResult{}, err
	}
	if !workable {
		return RoundsLedgerResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// An accepted bill action completes within the step; an unknown receipt
	// leaves its plan open, and the next commit waits for the resend.
	for _, method := range owner.OwnerMethods() {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsLedgerResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsLedgerResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	r.ledger.mu.Lock()
	pending := r.ledger.pending
	r.ledger.mu.Unlock()
	if pending == nil || pending.snapshot != state.Snapshot {
		return RoundsLedgerResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if pending.actions() == 0 {
		return RoundsLedgerResult{Verdict: awaitingPlan("ledger_bench", "")}, nil
	}
	id := domain.MintPlanID()
	actions, claims, key, err := ledgerActions(id, pending)
	if err != nil {
		return RoundsLedgerResult{}, err
	}
	if arbiter != nil && !arbiter.tryClaim(nil, claims...) {
		return RoundsLedgerResult{Verdict: claimHeld("bench")}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsLedgerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsLedgerResult{}, err
	}
	if p.session.State() != state {
		return RoundsLedgerResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if err = p.journal.CommitOwnerMethod(call, owner, ledgerMethod(owner, key), "", plan); err != nil {
		return RoundsLedgerResult{}, err
	}
	r.ledger.mu.Lock()
	r.ledger.pending = nil
	r.ledger.mu.Unlock()
	return RoundsLedgerResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// ledgerActions builds the plan's actions, removals first so their slots free
// before the placements, at most store.MaxBillPlanActions of them (the rest is
// next Round's diff). The claims are every bench the plan touches and the id
// of each bill it removes. The key names
// the plan's content for its method id.
func ledgerActions(plan domain.PlanID, p *ledgerPending) (actions []domain.Action, claims []string, key string, _ error) {
	seen := map[string]bool{}
	claim := func(k string) {
		if !seen[k] {
			seen[k] = true
			claims = append(claims, k)
		}
	}
	var keys []string
	for _, b := range p.remove {
		if len(actions) >= store.MaxBillPlanActions {
			break
		}
		removal, err := domain.NewRemoveProductionBill(b.Bench, b.ID)
		if err != nil {
			return nil, nil, "", err
		}
		action, err := domain.NewRemoveProductionBillAction(domain.ActionID(fmt.Sprintf("%s-%d", plan, len(actions))), removal)
		if err != nil {
			return nil, nil, "", err
		}
		actions = append(actions, action)
		claim("bench:" + b.Bench)
		claim("remove-bill:" + b.ID)
		keys = append(keys, "remove/"+b.Bench+"/"+b.ID)
	}
	for _, placement := range p.place {
		if len(actions) >= store.MaxBillPlanActions {
			break
		}
		bill, err := ledgerBill(placement)
		if err != nil {
			return nil, nil, "", err
		}
		action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-%d", plan, len(actions))), bill)
		if err != nil {
			return nil, nil, "", err
		}
		actions = append(actions, action)
		claim("bench:" + placement.Bench)
		keys = append(keys, "place/"+placement.Bench+"/"+placement.Spec.Key())
	}
	sort.Strings(keys)
	digest := sha256.New()
	for _, k := range keys {
		fmt.Fprintln(digest, k)
	}
	return actions, claims, fmt.Sprintf("%x", digest.Sum(nil)[:16]), nil
}

// ledgerBill is the production bill a placement stands for, the shape
// RoundsBillPlanner.admit builds from a selection.
func ledgerBill(p policy.LedgerPlacement) (domain.ProductionBill, error) {
	s := p.Spec
	if s.Mode == domain.HumanButcherForever {
		return domain.NewHumanButcherBill(p.Bench, s.Recipe, s.Worker)
	}
	bill, err := domain.NewProductionBill(p.Bench, s.Recipe, s.Mode, s.Target, s.Ingredients...)
	if err == nil && s.Worker != "" {
		bill, err = bill.PinWorker(s.Worker)
	}
	return bill, err
}

// ledgerMethod is the owner's next method for a plan of this content. The
// same reconcile diff next Round (native lost a placed bill, refused a
// removal) is a new attempt: the id skips every method the owner has tried.
func ledgerMethod(owner store.WorkOwner, key string) domain.MethodID {
	tried := map[domain.MethodID]bool{}
	for _, method := range owner.OwnerHistory() {
		tried[method.Method] = true
	}
	for attempt := 0; ; attempt++ {
		digest := sha256.Sum256([]byte(fmt.Sprintf("ledger/%s/%d", key, attempt)))
		if method := domain.MethodID(fmt.Sprintf("%s%x", ledgerMethodPrefix, digest[:16])); !tried[method] {
			return method
		}
	}
}
