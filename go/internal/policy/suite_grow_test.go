package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A 3-wide suite (east of the corridor at x=0) grown from depth 4 to 6:
// the old room is x 3..6, its outer wall x=7, the extension x 8.
var (
	grownRoom = LayoutRoom{Role: ModuleSuite, Interior: Rectangle{X: 3, Z: 10, Width: 6, Height: 3}, Door: domain.Cell{X: 2, Z: 11}, DoorRot: domain.West}
	oldFloor  = Rectangle{X: 3, Z: 10, Width: 4, Height: 3}
	extFloor  = Rectangle{X: 8, Z: 10, Width: 1, Height: 3}
)

func oldWall() CurrentConstruction {
	c := CurrentConstruction{Colony: true}
	for i, cell := range rectCells(Rectangle{X: 7, Z: 10, Width: 1, Height: 3}) {
		b, _ := domain.NewBuilding("Wall", cell, domain.North, "BlocksGranite")
		c.Buildings = append(c.Buildings, CurrentBuilding{ID: string(rune('a' + i)), Building: b, Cells: []domain.Cell{cell}})
	}
	return c
}

func growthRooms(ext bool, extEnclosed bool) RoomObservation {
	obs := RoomObservation{Rooms: []Room{{ID: "home", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"bed"}, Cells: rectCells(oldFloor)}}}
	if ext {
		obs.Rooms = append(obs.Rooms, Room{ID: "ext", Enclosed: domain.Known(extEnclosed), Cells: rectCells(extFloor)})
	}
	return obs
}

func TestSuiteGrowthRemovesTheOldWallOnlyOnceTheRingStandsRoofed(t *testing.T) {
	plan := LayoutPlan{Wings: []Wing{{Purpose: WingSuites, Rooms: []LayoutRoom{grownRoom}}}}
	walls := oldWall()
	// No extension yet: the ring goes up, the wall stays.
	if g, ok := NextSuiteGrowth(plan, growthRooms(false, false), walls, nil); !ok || g.Kind != SuiteGrowthShell {
		t.Fatalf("no ring = %+v %v, want the shell", g, ok)
	}
	// The ring closed but not roofed (Enclosed false with an open roof):
	// still the shell.
	if g, ok := NextSuiteGrowth(plan, growthRooms(true, false), walls, nil); !ok || g.Kind != SuiteGrowthShell {
		t.Fatalf("unroofed ring = %+v %v, want the shell", g, ok)
	}
	// Enclosed and roofed: now the old wall comes down, all of it.
	g, ok := NextSuiteGrowth(plan, growthRooms(true, true), walls, nil)
	if !ok || g.Kind != SuiteGrowthOpen || len(g.Walls) != 3 {
		t.Fatalf("closed ring = %+v %v, want the three old walls", g, ok)
	}
	// One wall down: the rooms merged, the rest still come down.
	merged := RoomObservation{Rooms: []Room{{ID: "home", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"bed"}, Cells: append(append(rectCells(oldFloor), rectCells(extFloor)...), domain.Cell{X: 7, Z: 10})}}}
	walls.Buildings = walls.Buildings[1:]
	if g, ok := NextSuiteGrowth(plan, merged, walls, nil); !ok || g.Kind != SuiteGrowthOpen || len(g.Walls) != 2 {
		t.Fatalf("half open = %+v %v, want the two walls left", g, ok)
	}
}

func TestSuiteGrowthRelocatesFurnitureOntoTheGrownPlan(t *testing.T) {
	plan := LayoutPlan{Wings: []Wing{{Purpose: WingSuites, Rooms: []LayoutRoom{grownRoom}}}}
	full := RoomObservation{Rooms: []Room{{ID: "home", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"bed"}, Cells: rectCells(grownRoom.Interior)}}}
	room := TidyRoom{ID: "home", Room: InteriorRoom{Role: RoomRoleBedroom, Interior: grownRoom.Interior, Doors: []domain.Cell{grownRoom.Door}, Standing: []string{"Bed"}}}
	// The bed still against the old back wall (x 5..6).
	room.Pieces = []TidyPiece{{Thing: "bed", Def: "Bed", Size: bedSize, Rot: domain.West, Rect: Rectangle{X: 5, Z: 11, Width: 2, Height: 1}}}
	g, ok := NextSuiteGrowth(plan, full, CurrentConstruction{Colony: true}, []TidyRoom{room})
	if !ok || g.Kind != SuiteGrowthRelocate || len(g.Moves) != 1 || g.Moves[0].Thing != "bed" || g.Moves[0].To == room.Pieces[0].Rect {
		t.Fatalf("grown room = %+v %v, want the bed re-sited", g, ok)
	}
	// On plan: nothing left.
	room.Pieces[0].Rect, room.Pieces[0].Rot = g.Moves[0].To, g.Moves[0].Rot
	if g, ok := NextSuiteGrowth(plan, full, CurrentConstruction{Colony: true}, []TidyRoom{room}); ok {
		t.Fatalf("tidy room = %+v, want no step", g)
	}
}

// suiteOwnerCase is a plan grown with one small suite, a's bed standing in
// it, and a target of 50 its floor cannot meet.
func suiteOwnerCase(t *testing.T) (LayoutPlan, RoomObservation, SleepingObservation, map[string]RoomTarget) {
	t.Helper()
	plan := Grow(PlanCore(coreTestZones(), 2, BuildTierCamp), 2, 1, BuildTierCamp, 0.1)
	i := wingOf(plan.Wings, WingSuites)
	if i < 0 {
		t.Fatal("no suite wing")
	}
	s := plan.Wings[i].Rooms[0]
	rooms := RoomObservation{Rooms: []Room{{ID: "s1", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"sb"}, Cells: rectCells(s.Interior)}}}
	sleeping := SleepingObservation{Colonists: 1,
		People: []SleepingPerson{{ID: "a", OwnedBed: domain.Known("sb")}},
		Beds:   []SleepingBed{{ID: "sb", Definition: "Bed", Room: domain.Known("s1"), Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []PawnID{"a"}, AccessibleTo: []PawnID{"a"}}},
		Rooms:  domain.Known([]UpkeepRoom{{ID: "s1", Role: "Bedroom", Quality: domain.Known(RoomQuality{Space: 5, Impressiveness: 20})}}),
	}
	return plan, rooms, sleeping, map[string]RoomTarget{"s1": {Min: 50}}
}

func TestSuiteGrowsOutwardInThePlan(t *testing.T) {
	plan, rooms, sleeping, targets := suiteOwnerCase(t)
	before := plan.Wings[wingOf(plan.Wings, WingSuites)].Rooms[0]
	if claims := SuiteClaims(plan, rooms, sleeping, targets, nil, nil); len(claims) != 0 {
		t.Fatalf("a growable suite claims %+v", claims)
	}
	suites := SuiteTargets(plan, rooms, sleeping, targets, nil)
	if len(suites) != 1 || suites[0] != 50 || !SuitesOwed(plan, suites) {
		t.Fatalf("suite targets = %v", suites)
	}
	grown := Grow(plan, 2, 1, BuildTierCamp, suites...)
	after := grown.Wings[wingOf(grown.Wings, WingSuites)].Rooms[0]
	if after.Interior.Width != suiteMaxDepth || after.Interior.Height != before.Interior.Height || after.Door != before.Door {
		t.Fatalf("grown %+v from %+v, want deepened to %d, same width and door", after, before, suiteMaxDepth)
	}
	// It grew away from the corridor: the corridor side stays put.
	if before.Interior.X > before.Door.X && after.Interior.X != before.Interior.X ||
		before.Interior.X < before.Door.X && after.Interior.X+after.Interior.Width != before.Interior.X+before.Interior.Width {
		t.Fatalf("grown %+v from %+v toward the corridor", after.Interior, before.Interior)
	}
}

func TestBlockedSuiteFallsBackToANewSuite(t *testing.T) {
	plan, rooms, sleeping, targets := suiteOwnerCase(t)
	s := plan.Wings[wingOf(plan.Wings, WingSuites)].Rooms[0]
	// Another room takes the ground the suite would grow into.
	block := LayoutRoom{Role: ModuleStorage, Interior: Rectangle{X: s.Interior.X + s.Interior.Width + 1, Z: s.Interior.Z, Width: 1, Height: 1}}
	if s.Interior.X < s.Door.X {
		block.Interior.X = s.Interior.X - 3
	}
	plan.Rooms = append(plan.Rooms, block)
	claims := SuiteClaims(plan, rooms, sleeping, targets, nil, nil)
	if len(claims) != 1 || claims[0].Pawn != "a" || claims[0].Bed != "sb" {
		t.Fatalf("blocked suite claims = %+v, want a's", claims)
	}
	suites := SuiteTargets(plan, rooms, sleeping, targets, claims)
	if len(suites) != 2 || suites[1] != 50 {
		t.Fatalf("blocked suite targets = %v, want a new suite at 50", suites)
	}
}
