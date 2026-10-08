package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func stockOf(resource policy.Resource, count int64) policy.StockObservation {
	return policy.StockObservation{Values: []policy.Stock{{Resource: resource, Available: domain.Known(count)}}}
}

func TestFundingLedgerFundsOnlyWhatTheStockStillPays(t *testing.T) {
	t.Parallel()
	l := newFundingLedger(stockOf("Granite", 100))
	wall := []policy.Amount{{Resource: "Granite", Count: 40}}
	if !l.funded(wall) {
		t.Fatal("first wall unfunded")
	}
	l.claim(wall)
	l.claim(wall)
	if l.funded(wall) {
		t.Fatal("a third wall was funded past the stock")
	}
	if l.funded([]policy.Amount{{Resource: "Steel", Count: 1}}) {
		t.Fatal("a resource the stock does not name was funded")
	}
	if !l.funded([]policy.Amount{{Resource: "Granite", Count: 10}, {Resource: "Granite", Count: 10}}) {
		t.Fatal("two costs of one resource were not summed within the stock")
	}
	if l.funded([]policy.Amount{{Resource: "Granite", Count: 15}, {Resource: "Granite", Count: 10}}) {
		t.Fatal("two costs of one resource were funded past the stock")
	}
}

func TestDefenseTierOpenMatchesAttemptsAndRepairs(t *testing.T) {
	t.Parallel()
	open := []domain.MethodID{"defense-perimeter-r1-07-0", "defense-perimeter-03-r2-1"}
	for name, want := range map[policy.DefenseTierName]bool{
		"perimeter-r1-07": true,
		"perimeter-03":    true,
		"perimeter-r1-0":  false,
		"perimeter-r1-08": false,
		"perimeter-3":     false,
	} {
		if got := defenseTierOpen(open, name); got != want {
			t.Fatalf("%s: open = %v, want %v", name, got, want)
		}
	}
}

func TestDefensePipelineKeepsTiersOffOpenCells(t *testing.T) {
	t.Parallel()
	p := newDefensePipeline(newFundingLedger(stockOf("Granite", 10)))
	wall := func(x, z int32) domain.Building {
		b, err := domain.NewBuilding("Wall", domain.Cell{X: x, Z: z}, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		return b
	}
	p.add([]domain.Building{wall(5, 5), wall(6, 5)})
	if !p.overlaps([]domain.Building{wall(5, 5)}) {
		t.Fatal("an open tier's own cell was allowed")
	}
	if p.overlaps([]domain.Building{wall(7, 6), wall(7, 5), wall(5, 6)}) {
		t.Fatal("a tier beside an open tier was held")
	}
}
