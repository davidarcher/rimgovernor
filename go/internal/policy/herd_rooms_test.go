package policy

import (
	"strconv"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var testHerdFurniture = HerdFurniture{
	Spot: InteriorPieceDef{Def: testAnimalSpot, Size: domain.Cell{X: 1, Z: 1}},
	Bed:  InteriorPieceDef{Def: testAnimalBed, Size: domain.Cell{X: 1, Z: 1}},
}

func herdTestPlan(t *testing.T, animals int) LayoutPlan {
	t.Helper()
	core := corePlan(utilityTestZones(), 3, TechTierCamp)
	return PlanUtilities(core, UtilityWants{PenAnimals: animals})
}

// standing is the room census of rooms standing enclosed on each room's
// interior.
func standing(rooms ...PlannedRoom) RoomObservation {
	var out RoomObservation
	for i, r := range rooms {
		out.Rooms = append(out.Rooms, Room{ID: strconv.Itoa(i), Enclosed: domain.Known(true), Cells: rectCells(r.Region.Bounds())})
	}
	return out
}

func bedAt(t *testing.T, def string, p InteriorPiece) CurrentBuilding {
	t.Helper()
	b, err := domain.NewBuilding(def, p.Anchor(), p.Rot, "")
	if err != nil {
		t.Fatal(err)
	}
	return CurrentBuilding{ID: p.Slot, Building: b, Cells: rectCells(p.Rect)}
}

// herdGround is the walls and doors standing on every room's ring.
func herdGround(plan LayoutPlan, rooms ...PlannedRoom) GroundCensus {
	out := GroundCensus{walls: map[domain.Cell]bool{}, doors: map[domain.Cell]bool{}, flaps: map[domain.Cell]bool{}}
	for _, r := range rooms {
		g := ringWalls(plan, r)
		for c := range g.walls {
			out.walls[c] = true
		}
		for c := range g.doors {
			out.doors[c] = true
		}
		for c := range g.flaps {
			out.flaps[c] = true
		}
	}
	return out
}

// herdTemplateCount is the pieces of def a HerdReconcile's template wants.
func herdTemplateCount(step HerdStep, def string) int {
	n := 0
	for _, p := range step.Template {
		if p.DefName == def {
			n++
		}
	}
	return n
}

// A ring that lost a wall after the room stood is rebuilt by the same diff that
// raised it: no dedicated lost-shell step.
func TestHerdStepRebuildsALostWall(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barn := plan.HerdRooms(PlannedBarn)[0]
	const animals = 4
	layout, ok := PlanRoomInterior(mustInterior(t, barn), testHerdFurniture.Spot)
	if !ok {
		t.Fatal("no barn layout")
	}
	var built []CurrentBuilding
	for _, p := range layout.Pieces[:animals] {
		built = append(built, bedAt(t, p.Def, p))
	}
	ground := herdGround(plan, barn)
	if step := NextHerdStep(plan, standing(barn), ground, built, nil, animals, 0, testHerdFurniture); step.Kind != HerdNone {
		t.Fatal("a matching room owes nothing", step)
	}
	var lost domain.Cell
	for c := range ground.walls {
		lost = c
		break
	}
	delete(ground.walls, lost)
	step := NextHerdStep(plan, standing(barn), ground, built, nil, animals, 0, testHerdFurniture)
	if step.Kind != HerdReconcile || !step.Room.Same(barn) || herdTemplateCount(step, testAnimalSpot) != animals {
		t.Fatal("lost wall reconciled", step)
	}
	in := ReconcileInput{Plan: plan, Room: barn, Ground: ground, Rooms: standing(barn), Furniture: step.Template}
	for _, b := range built {
		in.Rows = append(in.Rows, ClearanceTarget{EntityID: b.ID, DefName: b.Building.Definition(), Minimum: b.Cells[0], Maximum: b.Cells[0], Player: true})
	}
	if ops := ReconcileRoom(in); len(ops) != 1 || ops[0].Kind != OpWallIn || len(ops[0].Cells) != 1 || ops[0].Cells[0] != lost {
		t.Fatal("only the lost wall is raised", ops)
	}
}

func TestHerdOutgrowsItsRoomsAndAddsAnotherWithoutMovingAny(t *testing.T) {
	small := herdTestPlan(t, 10)
	grown := PlanHerdSites(small, 60)
	if units := grown.herdUnits(); len(units) != 1 {
		t.Fatal("an outgrown unit grows beside itself, never a second unit", len(units))
	}
	if len(grown.HerdRooms(PlannedBarn)) < 2 {
		t.Fatal("no second barn for a herd of 60", len(grown.HerdRooms(PlannedBarn)))
	}
	if grown.herdCapacity(PlannedBarn) < 60 {
		t.Fatal("rooms hold fewer beds than the herd", grown.herdCapacity(PlannedBarn))
	}
	for i, r := range small.Reservations {
		if !grown.Reservations[i].Equal(r) {
			t.Fatal("a placed reservation moved", r)
		}
	}
	if again := PlanHerdSites(grown, 60); len(again.Reservations) != len(grown.Reservations) {
		t.Fatal("top-up is not idempotent")
	}
	// The second barn is furnished once the first holds its share.
	first, second := grown.HerdRooms(PlannedBarn)[0], grown.HerdRooms(PlannedBarn)[1]
	var built []CurrentBuilding
	rooms := standing(first)
	layout, ok := PlanRoomInterior(mustInterior(t, first), testHerdFurniture.Spot)
	if !ok {
		t.Fatal("no barn layout")
	}
	for _, p := range layout.Pieces {
		built = append(built, bedAt(t, p.Def, p))
	}
	step := NextHerdStep(grown, rooms, herdGround(grown, first), built, nil, 60, 0, testHerdFurniture)
	if step.Kind != HerdReconcile || !step.Room.Same(second) {
		t.Fatal("second barn reconciled once the first is full", step)
	}
}

func mustInterior(t *testing.T, r PlannedRoom) InteriorRoom {
	t.Helper()
	in, ok := InteriorRoomFromLayout(r, testShapes)
	if !ok {
		t.Fatal("no interior", r)
	}
	return in
}
