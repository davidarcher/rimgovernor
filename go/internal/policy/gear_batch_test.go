package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestGearBatchNetsStoredMaterialAndQuality(t *testing.T) {
	r := gearModelFixture()
	v, _ := r.Observation.Value()
	for _, id := range []PawnID{"b", "c", "d"} {
		p := v.Pawns[0]
		p.Pawn = id
		v.Pawns = append(v.Pawns, p)
	}
	v.Stored = domain.Known([]GearStock{{"Parka", "Cloth", 2, 9, 1}, {"Parka", "Cloth", 1, 9, 8}, {"Parka", "Synthread", 2, 9, 8}})
	r.Observation = domain.Known(v)
	r = withBenchDef(r, "TableTailor")
	m, err := DeclareGearOrders(r)
	if err != nil || m.Abstain || len(m.Orders) != 1 || m.Orders[0].Target != 3 || !reflect.DeepEqual(m.Orders[0].Ingredients, []string{"Cloth"}) {
		t.Fatal(m, err)
	}
	v.Stored = domain.Known([]GearStock{{"Parka", "Cloth", 2, 9, 4}})
	r.Observation = domain.Known(v)
	m, err = DeclareGearOrders(r)
	if err != nil || len(m.Orders) != 0 {
		t.Fatal("stored items produced twice", m, err)
	}
}

func TestGearAllocatedStockCannotSatisfyAnotherBillGap(t *testing.T) {
	stock := []GearStock{{"Parka", "Cloth", 2, 9, 1}}
	loadouts := []GearLoadout{{Role: GearWorker, Gaps: []GearGap{{Source: GearStored, Gain: 1, Wanted: GearOption{Definition: "Parka", Stuff: "Cloth", Quality: 2, Condition: 1}}}}}
	got := unassignedGearStock(stock, loadouts)
	if got[0].Count != 0 || stock[0].Count != 1 {
		t.Fatal(got, stock)
	}
}

func TestGearSparesUseResourceGoalAndCoveredStorage(t *testing.T) {
	zone, needed, blocked, err := SelectStockpileCapacity(3, ResourceStorage{Capacity: 1, StackLimit: 1, Haulers: 1, Candidates: []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}})
	if err != nil || !needed || blocked || len(zone.Cells) != 2 {
		t.Fatal(zone, needed, blocked, err)
	}
}
