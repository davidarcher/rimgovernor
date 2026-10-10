package buildingruntime

import (
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
