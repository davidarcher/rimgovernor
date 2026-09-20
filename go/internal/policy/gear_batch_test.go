package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"testing"
)

func TestGearBatchNetsStoredMaterialAndQuality(t *testing.T) {
	r := gearFixture()
	v, _ := r.Observation.Value()
	for _, id := range []PawnID{"b", "c", "d"} {
		p := v.Pawns[0]
		p.Pawn = id
		v.Pawns = append(v.Pawns, p)
	}
	v.Stored = domain.Known([]GearStock{{"Parka", "Cloth", 2, 9, 1}, {"Parka", "Cloth", 1, 9, 8}, {"Parka", "Synthread", 2, 9, 8}})
	r.Observation = domain.Known(v)
	r.Stock = []Stock{{"Cloth", domain.Known(int64(240))}, {"Synthread", domain.Known(int64(0))}}
	m, err := SelectGearMethod(r)
	if err != nil || m.Kind != GearProduce || m.Count != 3 || !reflect.DeepEqual(m.Costs, []Amount{{"Cloth", 240}}) || !reflect.DeepEqual(m.Filter, []Resource{"Cloth"}) {
		t.Fatal(m, err)
	}
	r.Stock[0].Available = domain.Known(int64(239))
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind == GearProduce {
		t.Fatal("unfunded full batch", m, err)
	}
	v.Stored = domain.Known([]GearStock{{"Parka", "Cloth", 2, 9, 4}})
	r.Observation = domain.Known(v)
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind == GearProduce {
		t.Fatal("stored items produced twice", m, err)
	}
}

func TestGearWeaponBatch(t *testing.T) {
	r := gearFixture()
	v, _ := r.Observation.Value()
	v.Pawns[0].Replacements = domain.Known([]GearReplacement{})
	r.Observation = domain.Known(v)
	r.WeaponDemand = []Amount{{"Parka", 3}}
	r.Stock = []Stock{{"Cloth", domain.Known(int64(240))}, {"Synthread", domain.Known(int64(0))}}
	m, err := SelectGearMethod(r)
	if err != nil || m.Kind != GearProduce || m.Count != 3 {
		t.Fatal(m, err)
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
	p := RoutinePolicy{GearSpareTargets: map[Resource]int64{"Apparel_BasicShirt": 3}}
	targets, err := p.EffectiveResourceTargets(domain.Known([]Amount{}), nil)
	if err != nil || targets["Apparel_BasicShirt"] != 3 || !p.ResourceGoalConfigured() || !p.TracksResource("Apparel_BasicShirt") {
		t.Fatal(targets, err)
	}
	zone, needed, blocked, err := SelectStockpileCapacity(3, ResourceStorage{Capacity: 1, StackLimit: 1, Haulers: 1, Candidates: []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}})
	if err != nil || !needed || blocked || len(zone.Cells) != 2 {
		t.Fatal(zone, needed, blocked, err)
	}
}
