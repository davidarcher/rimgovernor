package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func successorBenchRow(id string, bills ...GearBill) GearBench {
	return GearBench{
		ID: id, Def: "TableMachining", Usable: domain.Known(true), Bills: domain.Known(bills),
		Recipes: domain.Known([]GearRecipe{{Definition: "vest", AvailableOn: domain.Known(true)}}),
	}
}

// A workshop successor's bench inherits the ledger's work with no copy
// mechanism: the old bench's bills match the declared order while it stands,
// are never orphaned, and when the packed old bench leaves the readback the
// same order is missing and lands on the new bench.
func TestSuccessorBenchTakesTheLedgersBillsWhenTheOldBenchIsPacked(t *testing.T) {
	vest := ledgerSpec("vest")
	declared := []Declared{{Orders: []OrderSpec{vest}}}
	old := ActualBill{ID: "b1", Bench: "old", Spec: vest}

	// Both benches stand: the standing bill satisfies the order, nothing is
	// placed twice and nothing is removed however many Rounds pass.
	orphans := map[string]int(nil)
	for range OrphanGraceRounds + 1 {
		got := ReconcileLedger(declared, []ActualBill{old}, orphans, nil)
		if len(got.Place) != 0 || len(got.Remove) != 0 || len(got.Orphans) != 0 {
			t.Fatalf("with both benches standing: %+v", got)
		}
		orphans = got.Orphans
	}
	both := []GearBench{successorBenchRow("old", GearBill{ID: "b1", Recipe: "vest", Spec: domain.Known(vest)}), successorBenchRow("new")}
	if placed, _ := PlaceLedgerOrders(nil, both); len(placed) != 0 {
		t.Fatalf("placed %+v with nothing missing", placed)
	}

	// The old bench is packed: its bill is gone from the readback, the order is
	// missing and the new bench takes it.
	got := ReconcileLedger(declared, nil, orphans, nil)
	if len(got.Place) != 1 || got.Place[0].Key() != vest.Key() {
		t.Fatalf("lost bill not re-placed: %+v", got)
	}
	placed, unplaced := PlaceLedgerOrders(got.Place, []GearBench{successorBenchRow("new")})
	if len(unplaced) != 0 || len(placed) != 1 || placed[0].Bench != "new" {
		t.Fatalf("placed %+v, unplaced %+v", placed, unplaced)
	}
}

// The predecessor's packed bench is installed in the successor from stock,
// ahead of building a new one; with no stock the successor builds on site.
func TestSuccessorInstallsThePredecessorsPackedBench(t *testing.T) {
	in, _ := reconFixture()
	in.Furniture = []WantedPiece{{DefName: "TableMachining", Minimum: domain.Cell{X: 10, Z: 10}, Maximum: domain.Cell{X: 12, Z: 10}}}
	if k := readyKinds(Reconcile(in)); !kindsEqual(k, OpBuild) {
		t.Fatalf("no stock: %v", k)
	}
	in.Stock = map[string]int{"TableMachining": 1}
	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpInstall) || len(readyOp(t, rec, OpInstall).Pieces) != 1 {
		t.Fatalf("packed bench not installed: %v", k)
	}
}
