package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// iedFixture is the corridor fixture with IEDs and an empty storage
// census: the colonists leave by the gate at (2,6), so the approach south
// of Entry (z < 5) is off every colonist route.
func iedFixture(ieds ...DefenseIED) DefenseRequest {
	r := defenseFixture()
	r.IEDs, r.FlammableStorage = ieds, domain.Known([]domain.Cell{})
	r.IEDMax, r.IEDStock = 8, domain.Known(map[Resource]int64{"Steel": 1000})
	for _, ied := range ieds {
		r.UnitCosts[ied.Definition] = []Amount{{Resource: "Steel", Count: 10}}
	}
	return r
}

var (
	testHE         = DefenseIED{Definition: "TrapIED_HighExplosive", Radius: 3.9}
	testIncendiary = DefenseIED{Definition: "TrapIED_Incendiary", Radius: 3.9, Incendiary: true}
)

func iedPlacements(t *testing.T, r DefenseRequest) map[domain.Cell]string {
	t.Helper()
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	tier, _ := layout.Tier(TierIEDs)
	return placed(t, tier)
}

func TestIEDsOnApproach(t *testing.T) {
	// Every second cell out from Entry, and the preferred HE IED takes
	// each.
	got := iedPlacements(t, iedFixture(testHE, testIncendiary))
	want := map[domain.Cell]string{}
	for _, c := range cells(15, 3, 15, 1) {
		want[c] = testHE.Definition
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
}

func TestIncendiaryIEDsKeepClearOfPlayerBuildings(t *testing.T) {
	// (15,3) is within the blast of the walls narrowing the opening.
	r := iedFixture(testIncendiary)
	want := map[domain.Cell]string{{X: 15, Z: 1}: testIncendiary.Definition}
	if got := iedPlacements(t, r); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	for i, c := range r.Cells {
		if c.Cell == (domain.Cell{X: 18, Z: 0}) {
			r.Cells[i].PlayerOwned = domain.Known(true)
		}
	}
	if got := iedPlacements(t, r); len(got) != 0 {
		t.Fatal(got)
	}
	// A high-explosive IED ignores a building that is not a door, route or
	// storage.
	r.IEDs = []DefenseIED{testHE}
	r.UnitCosts[testHE.Definition] = []Amount{{Resource: "Steel", Count: 10}}
	if got := iedPlacements(t, r); len(got) != 2 {
		t.Fatal(got)
	}
}

func TestIEDsKeepClearOfStorageAndDoors(t *testing.T) {
	r := iedFixture(testHE)
	r.FlammableStorage = domain.Known([]domain.Cell{{X: 18, Z: 0}})
	want := map[domain.Cell]string{{X: 15, Z: 3}: testHE.Definition}
	if got := iedPlacements(t, r); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	r = iedFixture(testHE)
	for i, c := range r.Cells {
		if c.Cell == (domain.Cell{X: 18, Z: 0}) {
			r.Cells[i].Door = domain.Known(true)
		}
	}
	if got := iedPlacements(t, r); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
}

func TestNoIEDsWithoutClearCellsOrStorageCensus(t *testing.T) {
	// Without the gate the colonist route to the map edge runs down the
	// approach itself.
	r := iedFixture(testHE)
	for i, c := range r.Cells {
		if c.Cell.X == 2 && c.Cell.Z >= 5 && c.Cell.Z <= 7 {
			r.Cells[i].Passable, r.Cells[i].Walkable, r.Cells[i].Door = domain.Known(false), domain.Known(false), domain.Known(false)
		}
	}
	if got := iedPlacements(t, r); len(got) != 0 {
		t.Fatal(got)
	}
	r = iedFixture(testHE)
	r.FlammableStorage = domain.Unknown[[]domain.Cell]()
	if got := iedPlacements(t, r); len(got) != 0 {
		t.Fatal(got)
	}
	// An unknown blast radius places nothing.
	if got := iedPlacements(t, iedFixture(DefenseIED{Definition: "TrapIED_HighExplosive"})); len(got) != 0 {
		t.Fatal(got)
	}
}
