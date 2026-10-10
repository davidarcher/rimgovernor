package buildingruntime

import (
	"context"
	"fmt"
	"strings"

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

// WorkLedger is WorkLedgerView with each order's latest placement outcome read
// from the journal: the ledger planner's ledger-* methods, whose plans hold the
// production_bill actions and native's receipts (accepted, or refused with its
// reason). Nothing is stored for it; when the journal cannot be read the view
// says so (AttemptsKnown false) rather than claiming no order was tried.
func (r *Rounder) WorkLedger(ctx context.Context) policy.LedgerView {
	view := r.WorkLedgerView()
	r.ledger.mu.Lock()
	kinds := r.ledger.benchKinds
	r.ledger.mu.Unlock()
	if len(view.Orders) == 0 {
		return view
	}
	attempts, ok := r.ledgerAttempts(ctx, kinds)
	if !ok {
		return view
	}
	return view.WithAttempts(attempts)
}

// ledgerAttempts is the latest outcome per order Key over every plan of the
// ledger owner's methods.
func (r *Rounder) ledgerAttempts(ctx context.Context, kinds map[string]string) (policy.PlacementAttempts, bool) {
	journal := r.player.journal
	review, err := journal.LoadRounds(ctx)
	if err != nil {
		return nil, false
	}
	owner, _, err := journal.WorkableOwner(ctx, review, policy.MaintainWorkLedger)
	if err != nil || owner == nil {
		return nil, false
	}
	attempts := policy.PlacementAttempts{}
	for _, method := range owner.OwnerHistory() {
		if !strings.HasPrefix(string(method.Method), ledgerMethodPrefix) {
			continue
		}
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return nil, false
		}
		for _, progress := range plan.Progress {
			bill, ok := progress.Action().ProductionBill()
			kind := kinds[bill.Bench()]
			if !ok || kind == "" {
				continue
			}
			key := policy.OrderSpec{BenchKind: kind, Recipe: bill.Recipe(), Ingredients: bill.Ingredients(), Worker: bill.Worker(), Mode: bill.Mode(), Target: bill.Target()}.Key()
			attempts.Note(key, attemptOf(string(method.Plan), progress.View()))
		}
	}
	return attempts, true
}

// attemptOf is a journaled bill action as a placement outcome.
func attemptOf(plan string, v domain.ProgressView) policy.PlacementAttempt {
	a := policy.PlacementAttempt{Plan: plan, Tick: int64(v.Tick), Outcome: policy.AttemptPending}
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
