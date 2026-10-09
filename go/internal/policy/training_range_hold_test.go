package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rangeRow(id, def string, cell domain.Cell, hp int64) UpkeepStructure {
	return UpkeepStructure{ID: id, Definition: def, Cell: cell, Home: true, HitPoints: hp, MaxHitPoints: 100}
}

func jobPawn(job string) WorkPawn {
	return WorkPawn{Job: domain.Known(PawnJob{Def: job})}
}

func TestRangeHomeHoldToggle(t *testing.T) {
	dummy := RangeDefNames[RangeDummy]
	whole := []UpkeepStructure{rangeRow("a", dummy, domain.Cell{X: 5, Z: 5}, 100), rangeRow("w", "Wall", domain.Cell{X: 1, Z: 1}, 10)}
	hurt := []UpkeepStructure{rangeRow("a", dummy, domain.Cell{X: 5, Z: 5}, 40), rangeRow("w", "Wall", domain.Cell{X: 1, Z: 1}, 10)}
	idle := domain.Known([]WorkPawn{jobPawn("Wait_Wander"), jobPawn("Repair")})
	drilling := domain.Known([]WorkPawn{jobPawn("Wait_Wander"), jobPawn(RangeJobPrefix + "Shooting")})
	unreadJob := domain.Known([]WorkPawn{{}})
	held := []domain.Cell{{X: 5, Z: 5}}
	for _, tc := range []struct {
		name       string
		structures domain.Fact[[]UpkeepStructure]
		pawns      domain.Fact[[]WorkPawn]
		want       []domain.Cell
	}{
		{"whole range is held", domain.Known(whole), idle, held},
		{"damaged while idle opens the window", domain.Known(hurt), idle, nil},
		{"damaged while drilling stays held", domain.Known(hurt), drilling, held},
		{"damaged with an unread job stays held", domain.Known(hurt), unreadJob, held},
		{"damaged with unknown pawns stays held", domain.Known(hurt), domain.Unknown[[]WorkPawn](), held},
		{"no range holds nothing", domain.Known([]UpkeepStructure{rangeRow("w", "Wall", domain.Cell{X: 1, Z: 1}, 10)}), idle, nil},
		{"unknown structures hold nothing", domain.Unknown[[]UpkeepStructure](), idle, nil},
	} {
		if got := RangeHomeHold(tc.structures, tc.pawns); !slices.Equal(got, tc.want) {
			t.Errorf("%s: got %v want %v", tc.name, got, tc.want)
		}
	}
}

func TestHomeAreaHoldClearsRangeFromHome(t *testing.T) {
	cell := domain.Cell{X: 30, Z: 30}
	r := extentFixture(t, cell)
	h, _ := r.Home.Value()
	var current []domain.Cell
	for x := int32(26); x <= 34; x++ {
		for z := int32(26); z <= 34; z++ {
			current = append(current, domain.Cell{X: x, Z: z})
		}
	}
	h.Home, h.AutoHome = domain.Known(current), domain.Known(false)
	bounds, home := domain.Known(Bounds{Width: 60, Height: 60}), domain.Known(h)
	plan := func(hold []domain.Cell) HomeAreaPlan {
		got, err := PlanHomeArea(bounds, r.Construction, r.Claims, home, hold)
		value, known := got.Value()
		if err != nil || !known {
			t.Fatal(known, err)
		}
		return value
	}
	if !plan(nil).Empty() {
		t.Fatal("whole home converged")
	}
	held := plan([]domain.Cell{cell})
	if !slices.Equal(held.Clear, []domain.Cell{cell}) || len(held.Set) != 0 {
		t.Fatalf("hold did not clear the cell: %+v", held)
	}
}

// A damaged dummy with nobody drilling: the hold lifts, the home plan sets the
// dummy's cell back into home, and once home the upkeep review targets it for
// vanilla repair. Whole again, the hold returns and the plan settles with the
// cell out of home.
func TestDamagedDummyIsRepairedThroughTheHomeToggle(t *testing.T) {
	cell := domain.Cell{X: 30, Z: 30}
	r := extentFixture(t, cell)
	h, _ := r.Home.Value()
	var around []domain.Cell
	for x := int32(26); x <= 34; x++ {
		for z := int32(26); z <= 34; z++ {
			if c := (domain.Cell{X: x, Z: z}); c != cell {
				around = append(around, c)
			}
		}
	}
	h.Home, h.AutoHome = domain.Known(around), domain.Known(false)
	idle := domain.Known([]WorkPawn{jobPawn("Wait_Wander")})
	hurt := UpkeepStructure{ID: "dummy", Definition: RangeDefNames[RangeDummy], Cell: cell, HitPoints: 20, MaxHitPoints: 100, Priority: 2}
	plan := func(structure UpkeepStructure) HomeAreaPlan {
		hold := RangeHomeHold(domain.Known([]UpkeepStructure{structure}), idle)
		got, err := PlanHomeArea(domain.Known(Bounds{Width: 60, Height: 60}), r.Construction, r.Claims, domain.Known(h), hold)
		value, known := got.Value()
		if err != nil || !known {
			t.Fatal(known, err)
		}
		return value
	}
	if got := plan(hurt); !slices.Equal(got.Set, []domain.Cell{cell}) || len(got.Clear) != 0 {
		t.Fatalf("damaged idle range not brought home: %+v", got)
	}
	hurt.Home = true
	review, err := ReviewUpkeep(UpkeepObservation{Structures: domain.Known([]UpkeepStructure{hurt})}, UpkeepHistory{}, nil)
	if err != nil {
		t.Fatal(err)
	}
	var targets []string
	for _, need := range review.Needs {
		if need.Concern == MaintainEssentialRepairs {
			targets, _ = need.Targets.Value()
		}
	}
	if !slices.Equal(targets, []string{"dummy"}) {
		t.Fatalf("repair targets %v", targets)
	}
	whole := hurt
	whole.HitPoints = whole.MaxHitPoints
	if got := plan(whole); !got.Empty() {
		t.Fatalf("whole range with the hold in place should settle: %+v", got)
	}
}
