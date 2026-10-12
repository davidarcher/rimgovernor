package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// conduitSurvey is a w x h firm map; edit marks cells.
func conduitSurvey(w, h int32, edit func(c *SurveyCell)) MapSurvey {
	var s MapSurvey
	for z := int32(0); z < h; z++ {
		for x := int32(0); x < w; x++ {
			c := SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true}
			if edit != nil {
				edit(&c)
			}
			s.Cells = append(s.Cells, c)
		}
	}
	return s
}

func cellAt(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }

func TestConduitRouteAvoidsDeepWater(t *testing.T) {
	// A water wall at x=5 with a gap at z=9: the route must go through it.
	survey := conduitSurvey(12, 10, func(c *SurveyCell) {
		if c.Cell.X == 5 && c.Cell.Z != 9 {
			c.Walkable, c.Footing = false, FootingNone
		}
	})
	allowed := ConduitCells(survey, nil)
	path := routeCells([]domain.Cell{cellAt(0, 0)}, map[domain.Cell]bool{cellAt(10, 0): true}, allowed)
	if len(path) == 0 {
		t.Fatal("no route around the water")
	}
	crossed := false
	for _, c := range path {
		if c.X == 5 {
			crossed = true
			if c.Z != 9 {
				t.Fatalf("path crosses water at %v", c)
			}
		}
	}
	if !crossed {
		t.Fatal("path never crossed the line")
	}
	if path[0] != cellAt(10, 0) || path[len(path)-1] != cellAt(0, 0) {
		t.Fatalf("path runs destination first, start last: %v", path)
	}
}

func TestConduitRouteBlockedByWaterAndRock(t *testing.T) {
	survey := conduitSurvey(8, 3, func(c *SurveyCell) {
		if c.Cell.X == 4 {
			c.Rock = true
		}
	})
	if path := routeCells([]domain.Cell{cellAt(0, 1)}, map[domain.Cell]bool{cellAt(7, 1): true}, ConduitCells(survey, nil)); path != nil {
		t.Fatalf("rock wall must block: %v", path)
	}
	// A dug room's walls open the rock.
	dug := PlannedRoom{ID: 1, Dug: true, Region: RectRegion(Rectangle{X: 3, Z: 0, Width: 2, Height: 3}), DoorRot: domain.North}
	if !ConduitCells(survey, []PlannedRoom{dug})[cellAt(4, 1)] {
		t.Fatal("rock inside a dug room's walls must be open")
	}
}

func TestConduitRouteIgnoresBuildings(t *testing.T) {
	survey := conduitSurvey(8, 1, func(c *SurveyCell) {
		c.Built = c.Cell.X >= 2 && c.Cell.X <= 5 // walls, doors, furniture
	})
	path := routeCells([]domain.Cell{cellAt(0, 0)}, map[domain.Cell]bool{cellAt(7, 0): true}, ConduitCells(survey, nil))
	if len(path) != 8 {
		t.Fatalf("a conduit runs straight through buildings: %v", path)
	}
}

func TestRouteCellsNearestStartDeterministic(t *testing.T) {
	allowed := map[domain.Cell]bool{}
	for x := int32(0); x < 10; x++ {
		for z := int32(0); z < 10; z++ {
			allowed[cellAt(x, z)] = true
		}
	}
	dest := map[domain.Cell]bool{cellAt(9, 5): true}
	starts := []domain.Cell{cellAt(0, 0), cellAt(7, 5)}
	first := routeCells(starts, dest, allowed)
	if first[len(first)-1] != cellAt(7, 5) || len(first) != 3 {
		t.Fatalf("the nearest start wins: %v", first)
	}
	for i := 0; i < 20; i++ {
		if again := routeCells(starts, dest, allowed); !reflect.DeepEqual(first, again) {
			t.Fatalf("route differs between runs: %v vs %v", first, again)
		}
	}
}

// solarSite is a stored solar plot named id.
func solarSite(id string, r Rectangle) PlannedSite {
	return newSite(id, NodeSolar, RectRegion(r), ReasonPowerSite)
}

// conduitPlan holds the 2x2 solar plot "solar-1" at (2, 2) and the reservations.
func conduitPlan(reservations ...LayoutReservation) LayoutPlan {
	return LayoutPlan{
		Sites: []PlannedSite{solarSite("solar-1", Rectangle{X: 2, Z: 2, Width: 2, Height: 2})},
		Rooms: []PlannedRoom{
			{ID: 1, Role: PlannedWorkshop, Region: RectRegion(Rectangle{X: 20, Z: 5, Width: 3, Height: 3}), Door: cellAt(19, 6), DoorRot: domain.West},
			{ID: 2, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 20, Z: 15, Width: 3, Height: 3}), Door: cellAt(19, 16), DoorRot: domain.West},
		},
		Reservations: reservations,
		NextRoomID:   3,
	}
}

func TestRouteConduitsGrowsOneNetWithSharedCells(t *testing.T) {
	plan := conduitPlan(LayoutReservation{Kind: ReserveBatteryRoom, Area: RectRegion(Rectangle{X: 2, Z: 10, Width: 2, Height: 2})})
	survey := conduitSurvey(30, 24, nil)
	consumers := []conduitNode{
		{ref: RoomRef(1), cells: RectRegion(Rectangle{X: 20, Z: 5, Width: 3, Height: 3}).Cells()},
		{ref: RoomRef(2), cells: RectRegion(Rectangle{X: 20, Z: 15, Width: 3, Height: 3}).Cells()},
	}
	links, decisions := RouteConduits(plan, consumers, survey, nil)
	if len(links) != 3 || len(decisions) != 3 {
		t.Fatalf("one link per non-root node: %v %v", links, decisions)
	}
	plan.Conduits = links
	if !plan.Valid() {
		t.Fatal("the routed net must be a valid plan")
	}
	// The battery room joins the root (the stored solar plot, which leads), not
	// a star centre; the second consumer joins the nearest cell of the net,
	// which is cells a previous link laid.
	if links[0].From != SiteRef("battery_room-0") || links[0].To != SiteRef("solar-1") {
		t.Fatalf("the battery room joins the root: %+v", links[0])
	}
	if links[2].To == SiteRef("solar-1") {
		t.Fatalf("the kitchen should join the net where it is nearest, not the root: %+v", links[2])
	}
	for _, d := range decisions {
		if d.Verdict != TilePlace || d.Reason != ReasonConduitRouted {
			t.Fatalf("decision: %+v", d)
		}
	}
	plan.Conduits = nil
	again, _ := RouteConduits(plan, consumers, survey, nil)
	if !reflect.DeepEqual(links, again) {
		t.Fatal("the net must not vary between runs")
	}
}

func TestRouteConduitsReportsBlockedAndSourceless(t *testing.T) {
	plan := conduitPlan()
	survey := conduitSurvey(30, 24, func(c *SurveyCell) {
		if c.Cell.X == 10 { // a water moat splits the map
			c.Walkable, c.Footing = false, FootingNone
		}
	})
	consumers := []conduitNode{{ref: RoomRef(1), cells: RectRegion(Rectangle{X: 20, Z: 5, Width: 3, Height: 3}).Cells()}}
	links, decisions := RouteConduits(plan, consumers, survey, nil)
	if len(links) != 0 || len(decisions) != 1 || decisions[0].Reason != ReasonConduitBlocked || decisions[0].Verdict != TileUnsatisfied {
		t.Fatalf("a moated consumer is blocked, not guessed: %v %v", links, decisions)
	}
	bare := conduitPlan()
	bare.Sites = nil
	links, decisions = RouteConduits(bare, consumers, survey, nil)
	if len(links) != 0 || len(decisions) != 1 || decisions[0].Reason != ReasonConduitNoSource {
		t.Fatalf("no power site, no net: %v %v", links, decisions)
	}
}

// A tiled plan carries the conduit net: every room holding a power-drawing
// station joins the net grown from the power site.
func TestTilePlansConduitsForPowerDrawingRooms(t *testing.T) {
	in, _ := tileInput(t, plainScoreSurvey(), 6, true)
	stations := map[string]Station{}
	for def, st := range in.Catalog.Stations {
		st.BaseW = -100
		stations[def] = st
	}
	in.Catalog.Stations = stations
	cell := in.Survey.Cells[0].Cell
	in.Sites = []PlannedSite{solarSite("solar-1", Rectangle{X: cell.X, Z: cell.Z, Width: 1, Height: 1})}
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Plan.Conduits) == 0 || len(res.Plan.Conduits) != len(res.ConduitDecisions) {
		t.Fatalf("links %d, decisions %d", len(res.Plan.Conduits), len(res.ConduitDecisions))
	}
	for _, d := range res.ConduitDecisions {
		if d.Reason != ReasonConduitRouted {
			t.Errorf("%+v", d)
		}
	}
	again, err := TileEmbedding(in)
	if err != nil || !reflect.DeepEqual(res.Plan.Conduits, again.Plan.Conduits) {
		t.Fatalf("the net must be deterministic: %v", err)
	}
}

func TestConduitValidRejectsBadLinks(t *testing.T) {
	plan := conduitPlan()
	good := ConduitLink{From: RoomRef(1), To: SiteRef("solar-1"), Cells: []domain.Cell{cellAt(5, 5), cellAt(5, 6)}}
	plan.Conduits = []ConduitLink{good}
	if !plan.Valid() {
		t.Fatal("a good link is valid")
	}
	for name, bad := range map[string]ConduitLink{
		"unknown room": {From: RoomRef(9), To: good.To, Cells: good.Cells},
		"unknown site": {From: good.From, To: SiteRef("solar-7"), Cells: good.Cells},
		"self link":    {From: good.From, To: good.From, Cells: good.Cells},
		"no cells":     {From: good.From, To: good.To},
		"gap":          {From: good.From, To: good.To, Cells: []domain.Cell{cellAt(5, 5), cellAt(5, 7)}},
		"repeat":       {From: good.From, To: good.To, Cells: []domain.Cell{cellAt(5, 5), cellAt(5, 6), cellAt(5, 5)}},
		"plain string": {From: "1", To: good.To, Cells: good.Cells},
	} {
		plan.Conduits = []ConduitLink{bad}
		if plan.Valid() {
			t.Errorf("%s must be invalid", name)
		}
	}
}
