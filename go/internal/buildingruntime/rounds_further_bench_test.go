package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The workshop planner reads the dispatcher's latest unmet throughput and the
// colony size from the ledger: a bench-bound shortfall below the cap asks for a
// further bench, the same shortfall at the cap does not.
func TestLedgerFurtherBenchesReadsUnmetAndColonySize(t *testing.T) {
	r := &Rounder{}
	r.ledger.unmet = []policy.UnmetThroughput{{BenchKind: "TableMachining", ShortPerDay: 3, Reason: policy.UnmetBenchesExhausted}}
	r.ledger.colonists = 4
	one := []policy.GearBench{{ID: "a", Def: "TableMachining"}}
	if got := r.ledgerFurtherBenches(one); len(got) != 1 || got[0] != "TableMachining" {
		t.Fatalf("one bench for four colonists: %v", got)
	}
	if got := r.ledgerFurtherBenches(append(one, policy.GearBench{ID: "b", Def: "TableMachining"})); len(got) != 0 {
		t.Fatalf("at the cap: %v", got)
	}
}
