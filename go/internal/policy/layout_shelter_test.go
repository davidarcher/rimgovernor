package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func shelterOf(t *testing.T, p LayoutPlan) PlannedRoom {
	t.Helper()
	var found []PlannedRoom
	for _, r := range p.Rooms {
		if r.Role == PlannedShelter {
			found = append(found, r)
		}
	}
	if len(found) != 1 {
		t.Fatalf("plan holds %d shelters, want one", len(found))
	}
	return found[0]
}

func TestPlanHoldsOneShelterSizedForTheColony(t *testing.T) {
	p := tiledPlan(coreTestSurvey(), 3)
	s := shelterOf(t, p)
	if !shelterHolds(s, 3, ShelterCampfires(false), 0) {
		t.Fatalf("shelter interior %dx%d holds less than the colony needs", s.Region.Bounds().Width, s.Region.Bounds().Height)
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal(err)
	}
	if !noThroughfare[PlannedShelter] {
		t.Fatal("the shelter is a thoroughfare")
	}
}

func TestShelterSizeGrowsWithColonistsAndCampfires(t *testing.T) {
	cells := func(n, fires int) int32 {
		s := ShelterSizes(n, fires, 0)[0]
		return s[0] * s[1]
	}
	for n := 1; n < 20; n++ {
		if cells(n+1, 1) < cells(n, 1) {
			t.Fatalf("size shrank from %d to %d colonists", n, n+1)
		}
	}
	if cells(20, 1) <= cells(2, 1) {
		t.Fatal("size does not grow with colonists")
	}
	if ShelterInteriorArea(4, 3, 0) <= ShelterInteriorArea(4, 1, 0) {
		t.Fatal("area does not grow with campfires")
	}
	// Every shape holds the contents.
	for n := 1; n < 30; n++ {
		for _, s := range ShelterSizes(n, 2, 0) {
			if int(s[0]*s[1]) < ShelterInteriorArea(n, 2, 0) || s[0] < s[1] {
				t.Fatalf("shape %v too small for %d colonists", s, n)
			}
		}
	}
}

func TestShelterDoorFacesTheBase(t *testing.T) {
	in := Rectangle{X: 10, Z: 10, Width: 6, Height: 4}
	for _, c := range []struct {
		seed domain.Cell
		rot  domain.Rotation
		door domain.Cell
	}{
		{domain.Cell{X: 40, Z: 12}, domain.East, domain.Cell{X: 16, Z: 12}},
		{domain.Cell{X: -20, Z: 12}, domain.West, domain.Cell{X: 9, Z: 12}},
		{domain.Cell{X: 13, Z: 60}, domain.North, domain.Cell{X: 13, Z: 14}},
		{domain.Cell{X: 13, Z: -30}, domain.South, domain.Cell{X: 13, Z: 9}},
	} {
		r := shelterRoom(in, c.seed)
		if r.DoorRot != c.rot || r.Door != c.door {
			t.Fatalf("seed %v: door %v %v, want %v %v", c.seed, r.Door, r.DoorRot, c.door, c.rot)
		}
	}
}
