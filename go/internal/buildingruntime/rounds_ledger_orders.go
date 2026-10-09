package buildingruntime

import (
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
