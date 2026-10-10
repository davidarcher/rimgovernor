package buildingruntime

import (
	"context"
	"fmt"
	"strings"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ledgerMethodPrefix starts the id of every method the ledger planner commits.
const ledgerMethodPrefix = "ledger-"

// noteBatches keeps the finite batches the declarers want as the ingredient
// demand of the migrated planners. A Round in which any declarer abstained
// declared only part of the wanted set, so the previous Round's stands.
// Callers hold the ledger's lock.
func (l *workLedger) noteBatches(declared []policy.Declared) {
	var batches []policy.OrderSpec
	seen := map[string]bool{}
	for _, d := range declared {
		if d.Abstained() {
			return
		}
		for _, o := range d.Orders {
			if o.Mode == domain.GearBatch && !seen[o.Key()] {
				seen[o.Key()] = true
				batches = append(batches, o)
			}
		}
	}
	l.batches = batches
}

// declarerName is a declarer's short name for the ledger view: its Go type
// without the package, the Rounds prefix or the Planner/Declarer suffix.
func declarerName(d OrderDeclarer) string {
	name := fmt.Sprintf("%T", d)
	name = name[strings.LastIndex(name, ".")+1:]
	name = strings.TrimPrefix(name, "Rounds")
	for _, suffix := range []string{"Planner", "Declarer"} {
		name = strings.TrimSuffix(name, suffix)
	}
	return name
}

// WorkLedgerView is the latest review's ledger as a read-only projection of
// memory: nothing is persisted and no planner reads it back. MaintainTrade's
// silver gap rides along while the world is the one it was read in.
func (r *Rounder) WorkLedgerView() policy.LedgerView {
	r.ledger.mu.Lock()
	view := r.ledger.view
	world := r.ledger.world
	r.ledger.mu.Unlock()
	if view.Status == "" {
		view = policy.NewLedgerView(policy.LedgerViewNone, 0)
	}
	view.Export = r.exports.view(world)
	return view
}

// declaredBatches are the latest review's declared finite batches.
func (l *workLedger) declaredBatches() []policy.OrderSpec {
	l.mu.Lock()
	defer l.mu.Unlock()
	return append([]policy.OrderSpec(nil), l.batches...)
}

// placementIndex is the Rounder's memory of each placed bill's latest outcome,
// the ledger's Attempt column. It is updated where the ledger planner commits a
// placement and where the worker dispatches it (WorkerConfig.Placement), and
// seeded once from the journal's receipts so a restart does not read as "never
// attempted". It is keyed by the bill's bench id and spec rather than the order
// Key, which needs the bench's kind, a readback fact a restart has not read yet.
type placementIndex struct {
	mu      sync.Mutex
	seeded  bool
	entries map[string]placementEntry
}

type placementEntry struct {
	bench   string
	spec    policy.OrderSpec
	attempt policy.PlacementAttempt
}

func placementEntryOf(plan string, bill domain.ProductionBill, attempt policy.PlacementAttempt) (string, placementEntry) {
	spec := policy.OrderSpec{Recipe: bill.Recipe(), Ingredients: bill.Ingredients(), Worker: bill.Worker(), Mode: bill.Mode(), Target: bill.Target()}
	attempt.Plan = plan
	return bill.Bench() + "/" + spec.Key(), placementEntry{bench: bill.Bench(), spec: spec, attempt: attempt}
}

// notePlacement records a committed or dispatched production bill's progress.
func (r *Rounder) notePlacement(plan domain.PlanID, action domain.Action, v domain.ProgressView) {
	bill, ok := action.ProductionBill()
	if !ok {
		return
	}
	key, e := placementEntryOf(string(plan), bill, attemptOf(v))
	x := &r.ledger.attempts
	x.mu.Lock()
	defer x.mu.Unlock()
	if x.entries == nil {
		x.entries = map[string]placementEntry{}
	}
	x.entries[key] = e
}

// WorkLedger is WorkLedgerView with each order's latest placement outcome from
// the placement index. The index is seeded once from the ledger planner's
// journaled plans; until that read succeeds the view says so (AttemptsKnown
// false) rather than claiming no order was tried.
func (r *Rounder) WorkLedger(ctx context.Context) policy.LedgerView {
	view := r.WorkLedgerView()
	r.ledger.mu.Lock()
	kinds := r.ledger.benchKinds
	r.ledger.mu.Unlock()
	if len(view.Orders) == 0 || !r.seedPlacements(ctx) {
		return view
	}
	attempts := policy.PlacementAttempts{}
	r.ledger.attempts.mu.Lock()
	for _, e := range r.ledger.attempts.entries {
		if kind := kinds[e.bench]; kind != "" {
			spec := e.spec
			spec.BenchKind = kind
			attempts.Note(spec.Key(), e.attempt)
		}
	}
	r.ledger.attempts.mu.Unlock()
	return view.WithAttempts(attempts)
}

// seedPlacements fills the index from the journal once and reports whether it
// is filled. Outcomes noted live meanwhile stand: they are newer.
func (r *Rounder) seedPlacements(ctx context.Context) bool {
	x := &r.ledger.attempts
	x.mu.Lock()
	seeded := x.seeded
	x.mu.Unlock()
	if seeded {
		return true
	}
	journal := r.player.journal
	review, err := journal.LoadRounds(ctx)
	if err != nil {
		return false
	}
	owner, _, err := journal.WorkableOwner(ctx, review, policy.MaintainWorkLedger)
	if err != nil || owner == nil {
		return false
	}
	found := map[string]placementEntry{}
	for _, method := range owner.OwnerHistory() {
		if !strings.HasPrefix(string(method.Method), ledgerMethodPrefix) {
			continue
		}
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return false
		}
		for _, progress := range plan.Progress {
			if bill, ok := progress.Action().ProductionBill(); ok {
				key, e := placementEntryOf(string(method.Plan), bill, attemptOf(progress.View()))
				if held, ok := found[key]; !ok || e.attempt.Tick >= held.attempt.Tick {
					found[key] = e
				}
			}
		}
	}
	x.mu.Lock()
	defer x.mu.Unlock()
	if x.entries == nil {
		x.entries = map[string]placementEntry{}
	}
	for key, e := range found {
		if _, live := x.entries[key]; !live {
			x.entries[key] = e
		}
	}
	x.seeded = true
	return true
}

// attemptOf is a bill action's progress as a placement outcome.
func attemptOf(v domain.ProgressView) policy.PlacementAttempt {
	a := policy.PlacementAttempt{Tick: int64(v.Tick), Outcome: policy.AttemptPending}
	receipt, known := v.Receipt.Value()
	switch {
	case !known:
	case receipt == domain.ReceiptAccepted:
		a.Outcome = policy.AttemptAccepted
	case receipt == domain.ReceiptRefused:
		a.Outcome = policy.AttemptRefused
		if refusal, ok := v.Refusal.Value(); ok {
			a.Code, a.Reason = refusal.Code, refusal.Reason
		}
	default:
		a.Outcome = policy.AttemptUnconfirmed
	}
	return a
}
