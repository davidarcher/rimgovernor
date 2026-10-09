package buildingruntime

import (
	"context"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ledgerMethodPrefix starts the id of every method the ledger planner commits.
const ledgerMethodPrefix = "ledger-"

// ledgerPlanMethod reports whether a plan was committed by the ledger planner.
func ledgerPlanMethod(method domain.MethodID) bool {
	return strings.HasPrefix(string(method), ledgerMethodPrefix)
}

// noteBatches keeps the finite batches the declarers want as the ingredient
// demand of the migrated planners. A Round in which any declarer abstained
// declared only part of the wanted set, so the previous Round's stands.
// Callers hold the ledger's lock.
func (l *workLedger) noteBatches(declared []policy.Declared) {
	var batches []policy.OrderSpec
	seen := map[string]bool{}
	for _, d := range declared {
		if d.Abstain {
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

// declaredBatches are the latest review's declared finite batches.
func (l *workLedger) declaredBatches() []policy.OrderSpec {
	l.mu.Lock()
	defer l.mu.Unlock()
	return append([]policy.OrderSpec(nil), l.batches...)
}

// markMigrated flags the bills the journal says a migrated owner placed
// (policy.LedgerMigratedOwner) as the only ones the ledger may remove. A bill
// no plan placed, one whose Standard the review no longer binds (after a
// reload), and one of an unmigrated owner stay unflagged and are kept.
func (r *Rounder) markMigrated(ctx context.Context, snapshot domain.GenerationSnapshot, review store.Rounds, actual []policy.ActualBill) error {
	var ids []string
	for _, b := range actual {
		if b.Kind == policy.LedgerProduction {
			ids = append(ids, b.ID)
		}
	}
	placed, err := r.player.journal.PlacedBills(ctx, snapshot, ids)
	if err != nil {
		return err
	}
	for i, b := range actual {
		if bill, ok := placed[b.ID]; ok {
			concern, bound := review.Need(bill.Standard)
			actual[i].Migrated = bound && policy.LedgerMigratedOwner(concern)
		}
	}
	return nil
}
