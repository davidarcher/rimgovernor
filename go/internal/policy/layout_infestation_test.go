package policy

import (
	"math"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWallOffMountainPocket(t *testing.T) {
	plan := withInterior(LayoutPlan{
		Rooms: []PlannedRoom{{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 40, Z: 40, Width: 5, Height: 5}), DoorRot: domain.North}},
		// A stale pocket is replaced, not kept beside the new ones.
		Sites: []PlannedSite{testSite(NodeRing, Rectangle{X: 30, Z: 30, Width: 30, Height: 3}), testSite(NodePocketWall, Rectangle{X: 1, Z: 1, Width: 1, Height: 1})},
	})
	var cells []SurveyCell
	open := func(r Rectangle) {
		for _, c := range rectCells(r) {
			cells = append(cells, SurveyCell{Cell: c, Walkable: true, ThickRoof: true})
		}
	}
	open(Rectangle{X: 34, Z: 36, Width: 3, Height: 2}) // small pocket inside the ring: walled
	open(Rectangle{X: 50, Z: 50, Width: 5, Height: 5}) // 25 cells: too big
	open(Rectangle{X: 43, Z: 43, Width: 2, Height: 1}) // under the bedroom: used
	open(Rectangle{X: 90, Z: 90, Width: 2, Height: 2}) // far from the base
	cells = append(cells, SurveyCell{Cell: domain.Cell{X: 38, Z: 36}, Rock: true, ThickRoof: true})
	cells = append(cells, SurveyCell{Cell: domain.Cell{X: 38, Z: 38}, Walkable: true}) // open sky
	s := MapSurvey{Bounds: Bounds{Width: 100, Height: 100}, Cells: cells}

	got := PlanMountainPockets(plan, s)
	var walled []domain.Cell
	for _, r := range got.Sites {
		if r.Kind == NodePocketWall {
			walled = append(walled, r.Footprint.Cells()...)
		}
	}
	slices.SortFunc(walled, func(a, b domain.Cell) int {
		if cellLess(a, b) {
			return -1
		}
		return 1
	})
	want := rectCells(Rectangle{X: 34, Z: 36, Width: 3, Height: 2})
	if !slices.Equal(walled, want) {
		t.Fatalf("walled %v, want %v", walled, want)
	}
	sections, err := PocketSections(got, "Wall")
	if err != nil || len(sections) != 1 || len(sections[0].Buildings) != 6 || sections[0].Buildings[0].Definition() != "Wall" || !IsPerimeterTier(sections[0].Name) {
		t.Fatalf("sections %+v err %v", sections, err)
	}
	if !sameSites(got, got) || sameSites(plan, got) {
		t.Fatal("pocket walls are not part of the perimeter's identity")
	}
	if none := PlanMountainPockets(LayoutPlan{Rooms: plan.Rooms}, s); len(none.Sites) != 0 {
		t.Fatalf("a plan without a perimeter walls %v", none.Sites)
	}
}

func TestLightBaseRooms(t *testing.T) {
	reach := glowReach(12, 214)
	plan := LayoutPlan{Rooms: []PlannedRoom{
		{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 10, Z: 10, Width: 4, Height: 4})},
		{Role: PlannedWorkshop, Region: RectRegion(Rectangle{X: 20, Z: 10, Width: 20, Height: 7})},
		{Role: PlannedReserve, Region: RectRegion(Rectangle{X: 60, Z: 10, Width: 6, Height: 6})},
	}}
	lamps := BaseRoomLamps(plan, reach)
	if len(lamps) != 3 {
		t.Fatalf("lamps %v, want one in the bedroom and two in the workshop", lamps)
	}
	for _, r := range plan.Rooms {
		for _, c := range rectCells(r.Region.Bounds()) {
			best := math.Inf(1)
			for _, l := range lamps {
				best = min(best, distance(l, c))
			}
			if r.Role == PlannedReserve {
				if best <= reach {
					t.Fatalf("reserve cell %v lit", c)
				}
				continue
			}
			if best > reach {
				t.Fatalf("%s cell %v is %.1f from a lamp", r.Role, c, best)
			}
		}
	}
	if BaseRoomLamps(plan, 0) != nil {
		t.Fatal("an unknown reach places lamps")
	}
	sections, err := LightSections(plan, "StandingLamp", reach)
	if err != nil || len(sections) != 1 || len(sections[0].Buildings) != 3 || !IsPerimeterTier(sections[0].Name) {
		t.Fatalf("sections %+v err %v", sections, err)
	}
}

func TestLightShapedRoomsStayOnTheFloor(t *testing.T) {
	reach := glowReach(12, 214)
	var ring []RowRun
	for z := int32(0); z < 15; z++ {
		if z >= 4 && z <= 10 {
			ring = append(ring, RowRun{Z: z, X: 0, Length: 4}, RowRun{Z: z, X: 11, Length: 4})
		} else {
			ring = append(ring, RowRun{Z: z, X: 0, Length: 15})
		}
	}
	for name, region := range map[string]Region{
		"ring": RegionOfRuns(ring),
		"L":    RegionOfRuns([]RowRun{{Z: 0, X: 0, Length: 3}, {Z: 1, X: 0, Length: 3}, {Z: 2, X: 0, Length: 20}, {Z: 3, X: 0, Length: 20}}),
	} {
		lamps := BaseRoomLamps(LayoutPlan{Rooms: []PlannedRoom{{Role: PlannedWorkshop, Region: region}}}, reach)
		if len(lamps) == 0 {
			t.Fatalf("%s: no lamps", name)
		}
		for _, l := range lamps {
			if !region.Contains(l) {
				t.Fatalf("%s: lamp %v off the floor", name, l)
			}
		}
		for _, c := range region.Cells() {
			best := math.Inf(1)
			for _, l := range lamps {
				best = min(best, distance(l, c))
			}
			if best > reach {
				t.Fatalf("%s: cell %v is %.1f from a lamp", name, c, best)
			}
		}
	}
}
