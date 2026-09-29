package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// iedFixture opens the home area onto the census border at z=29, so the
// colonist routes leave north and the approach south of Entry (z < 14) is
// off every colonist route.
func iedFixture(ieds ...DefenseIED) DefenseRequest {
	r := defenseFixture()
	r.IEDs, r.FlammableStorage = ieds, domain.Known([]domain.Cell{})
	for i, c := range r.Cells {
		if c.Cell.Z == 29 && c.Cell.X >= 1 && c.Cell.X <= 18 {
			r.Cells[i] = DefenseCell{Cell: c.Cell, Walkable: domain.Known(true), Passable: domain.Known(true), BlocksSight: domain.Known(false),
				PlayerOwned: domain.Known(false), NaturalRock: domain.Known(false), EdgeReachable: domain.Known(true), HomeArea: domain.Known(true), Door: domain.Known(false), CoverFill: domain.Known(0.0)}
		}
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

func TestIEDsOnApproachOutsideBlastOfSafeLane(t *testing.T) {
	// (9,12) is within 3.9 of the safe lane's (8,14); every second cell
	// beyond it is clear, and the preferred HE IED takes each.
	got := iedPlacements(t, iedFixture(testHE, testIncendiary))
	want := map[domain.Cell]string{}
	for _, c := range cells(9, 10, 9, 8, 9, 6, 9, 4) {
		want[c] = testHE.Definition
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
}

func TestIncendiaryIEDsKeepClearOfPlayerBuildings(t *testing.T) {
	r := iedFixture(testIncendiary)
	for i, c := range r.Cells {
		if c.Cell == (domain.Cell{X: 10, Z: 5}) {
			r.Cells[i].PlayerOwned = domain.Known(true)
		}
	}
	got := iedPlacements(t, r)
	want := map[domain.Cell]string{{X: 9, Z: 10}: testIncendiary.Definition, {X: 9, Z: 0}: testIncendiary.Definition}
	if !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	// A high-explosive IED ignores a building that is not a door, route or
	// storage.
	r.IEDs = []DefenseIED{testHE}
	if got := iedPlacements(t, r); len(got) != 4 {
		t.Fatal(got)
	}
}

func TestIEDsKeepClearOfStorageAndDoors(t *testing.T) {
	r := iedFixture(testHE)
	r.FlammableStorage = domain.Known([]domain.Cell{{X: 9, Z: 7}})
	want := map[domain.Cell]string{{X: 9, Z: 2}: testHE.Definition, {X: 9, Z: 0}: testHE.Definition}
	if got := iedPlacements(t, r); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	r = iedFixture(testHE)
	for i, c := range r.Cells {
		if c.Cell == (domain.Cell{X: 11, Z: 7}) {
			r.Cells[i].Door = domain.Known(true)
		}
	}
	want = map[domain.Cell]string{{X: 9, Z: 2}: testHE.Definition, {X: 9, Z: 0}: testHE.Definition}
	if got := iedPlacements(t, r); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
}

func TestNoIEDsWithoutClearCellsOrStorageCensus(t *testing.T) {
	// The colonist route to the map edge runs down the approach itself.
	r := defenseFixture()
	r.IEDs, r.FlammableStorage = []DefenseIED{testHE}, domain.Known([]domain.Cell{})
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
