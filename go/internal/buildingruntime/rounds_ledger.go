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
// Rounds) and abstains (policy.Abstaining, naming the unread fact) when it lacked the
// facts to say, which stops orphan removal for that Round. It writes nothing; a facts-only native
// read for what the projection lacks is allowed, and a read that fails is an
// abstain, not an error.
type OrderDeclarer interface {
	DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error)
}

// purposeReporter is an OrderDeclarer that also says why its purpose has
// nothing to place, and lends the game time its bills take: the verdict the
// ledger files beside its own (RoundsLedgerResult.Reports). DeclareOrders notes
// the verdict on the same request that declares the orders, so the two read
// one set of facts.
type purposeReporter interface {
	OrderDeclarer
	// reportTarget is the key the verdict files under (a concern id, or a
	// subject key beside the concern ids) and the concern whose owner gates it.
	reportTarget() (key, need policy.ConcernID)
	// report is the verdict the latest DeclareOrders noted.
	report() purposeReport
}

// purposeReport is a purpose's verdict once its concern is workable and the
// owner holds no open work of the purpose's own.
type purposeReport struct {
	order Verdict
	// workTicks asks for game time while the purpose's bill runs.
	workTicks uint32
	// openWork says the owner's open plans are the purpose's own work in
	// flight; nil when the purpose has none.
	openWork func(context.Context, *store.Store, store.StandardState) (bool, error)
}

// ledgerPurpose is one reporter's latest report with where it files.
type ledgerPurpose struct {
	key, need policy.ConcernID
	report    purposeReport
}

// LedgerReport is one purpose's verdict as the ledger step files it.
type LedgerReport struct {
	Key     policy.ConcernID
	Verdict Verdict
}

// purposeOf is d's latest report with where it files (or, unread, the
// unavailable verdict of a Round whose benches were not read); ok is false for a
// declarer that reports nothing.
func purposeOf(d OrderDeclarer, unread bool) (ledgerPurpose, bool) {
	reporter, ok := d.(purposeReporter)
	if !ok {
		return ledgerPurpose{}, false
	}
	key, need := reporter.reportTarget()
	if unread {
		return ledgerPurpose{key: key, need: need, report: purposeReport{order: fieldUnavailable("work_benches")}}, true
	}
	return ledgerPurpose{key: key, need: need, report: reporter.report()}, true
}

// purposeKeys are the keys the registered reporters file under.
func (r *Rounder) purposeKeys() []policy.ConcernID {
	r.ledger.mu.Lock()
	defer r.ledger.mu.Unlock()
	var keys []policy.ConcernID
	for _, d := range r.ledger.declarers {
		if purpose, ok := purposeOf(d, true); ok {
			keys = append(keys, purpose.key)
		}
	}
	return keys
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
	// reports are the latest review's purpose verdicts, for the world they were
	// declared in (reportsWorld).
	reports      []ledgerPurpose
	reportsWorld domain.GenerationSnapshot
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
	// benchKinds is the latest readback's bench definition by bench id, which
	// turns a journaled bill action back into an order Key.
	benchKinds map[string]string
	// attempts is the placement index (WorkLedger), seeded once from the journal.
	attempts placementIndex
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
	l.reports, l.reportsWorld = nil, snapshot
	if l.world != snapshot && (l.world.Colony != snapshot.Colony || l.world.Load != snapshot.Load || l.world.Map != snapshot.Map) {
		l.orphans = nil
		l.dispatch = policy.DispatchMemory{}
	}
	l.world = snapshot
	l.unmet, l.colonists = nil, 0
	l.benchKinds = nil
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
		for _, d := range declarers {
			if purpose, ok := purposeOf(d, true); ok {
				l.reports = append(l.reports, purpose)
			}
		}
		l.mu.Unlock()
		return domain.Unknown[bool](), nil
	}
	benches := make([]policy.GearBench, 0, len(reads))
	for _, read := range reads {
		benches = append(benches, read.Bench)
	}
	declared := make([]policy.Declared, 0, len(declarers))
	named := make([]policy.NamedDeclared, 0, len(declarers))
	var purposes []ledgerPurpose
	for _, d := range declarers {
		one, err := d.DeclareOrders(ctx, snapshot, projection, benches)
		if err != nil {
			return domain.Unknown[bool](), err
		}
		declared = append(declared, one)
		named = append(named, policy.NamedDeclared{Name: declarerName(d), Declared: one})
		if purpose, ok := purposeOf(d, false); ok {
			purposes = append(purposes, purpose)
		}
	}
	actual, known := policy.LedgerActuals(benches)
	l.mu.Lock()
	defer l.mu.Unlock()
	l.reports = purposes
	l.benchKinds = map[string]string{}
	for _, b := range benches {
		l.benchKinds[b.ID] = b.Def
	}
	l.noteBatches(declared)
	if !known {
		l.view = policy.NewLedgerView(policy.LedgerViewUnread, int64(projection.Identity.Tick))
		for _, d := range named {
			l.view.Declarers = append(l.view.Declarers, policy.DeclarerView(d.Name, d.Declared))
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
	// Reports are the purposes' verdicts (purposeReporter), filed beside the
	// ledger's own.
	Reports []LedgerReport
	// NativeWorkTicks asks for game time while a purpose's bill runs: a placed
	// bill is no work to the clock.
	NativeWorkTicks uint32
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
		return RoundsLedgerResult{Verdict: BuildingReasonDisabled, Reports: r.heldReports(BuildingReasonDisabled)}, nil
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
	// The purposes' verdicts never fail the step: a read that fails reports
	// the purpose unavailable (purposeVerdict), whatever the commit does.
	reports, ticks := l.purposeReports(call, state.Snapshot, review)
	result, err := l.commit(call, epoch, arbiter, state, review)
	if err != nil {
		return RoundsLedgerResult{}, err
	}
	result.Reports, result.NativeWorkTicks = reports, ticks
	return result, nil
}

// heldReports files one verdict for every registered reporter: a switched-off
// service or a priority gate holds the purposes with the ledger.
func (r *Rounder) heldReports(v Verdict) []LedgerReport {
	var out []LedgerReport
	for _, key := range r.purposeKeys() {
		out = append(out, LedgerReport{Key: key, Verdict: v})
	}
	return out
}

// purposeReports are the latest review's purpose verdicts, and the largest
// game time any of them lends.
func (l *RoundsLedgerPlanner) purposeReports(call context.Context, snapshot domain.GenerationSnapshot, review store.Rounds) (reports []LedgerReport, ticks uint32) {
	r := l.reviewer
	r.ledger.mu.Lock()
	purposes := r.ledger.reports
	if r.ledger.reportsWorld != snapshot {
		purposes = nil
	}
	r.ledger.mu.Unlock()
	for _, purpose := range purposes {
		verdict, lent := l.purposeVerdict(call, review, purpose)
		reports = append(reports, LedgerReport{Key: purpose.key, Verdict: verdict})
		ticks = max(ticks, lent)
	}
	return reports, ticks
}

// purposeVerdict is one purpose's verdict: no deficit while its concern is not
// workable, existing work while the owner holds the purpose's own, else what its
// declaration said. A journal read that fails reports the purpose unavailable.
func (l *RoundsLedgerPlanner) purposeVerdict(call context.Context, review store.Rounds, purpose ledgerPurpose) (Verdict, uint32) {
	journal := l.reviewer.player.journal
	owner, workable, err := journal.Workable(call, review, purpose.need)
	if err != nil {
		return fieldUnavailable("purpose_owner"), 0
	}
	if !workable {
		return BuildingReasonNoDeficit, 0
	}
	if purpose.report.openWork != nil {
		open, err := purpose.report.openWork(call, journal, owner)
		if err != nil {
			return fieldUnavailable("purpose_plans"), 0
		}
		if open {
			return BuildingReasonExistingWork, 0
		}
	}
	return purpose.report.order, purpose.report.workTicks
}

// commit places and removes the Round's bills (MaintainWorkLedger's method).
func (l *RoundsLedgerPlanner) commit(call, epoch context.Context, arbiter *stepArbiter, state ControlState, review store.Rounds) (RoundsLedgerResult, error) {
	r := l.reviewer
	p := r.player
	owner, workable, err := p.journal.Workable(call, review, policy.MaintainWorkLedger)
	if err != nil {
		return RoundsLedgerResult{}, err
	}
	if !workable {
		return RoundsLedgerResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// An accepted bill action completes within the step; an unknown receipt
	// leaves its plan open, and the next commit waits for the resend.
	for _, method := range owner.Methods {
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
	for _, action := range actions {
		r.notePlacement(id, action, domain.ProgressView{})
	}
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
func ledgerMethod(owner store.StandardState, key string) domain.MethodID {
	tried := map[domain.MethodID]bool{}
	for _, method := range owner.History {
		tried[method.Method] = true
	}
	for attempt := 0; ; attempt++ {
		digest := sha256.Sum256([]byte(fmt.Sprintf("ledger/%s/%d", key, attempt)))
		if method := domain.MethodID(fmt.Sprintf("%s%x", ledgerMethodPrefix, digest[:16])); !tried[method] {
			return method
		}
	}
}
