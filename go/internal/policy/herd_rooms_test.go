package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"slices"
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
	core := corePlan(utilityTestZones(), 3, BuildTierCamp)
	return PlanUtilities(core, UtilityWants{PenAnimals: animals})
}

// standing is the room census of rooms standing enclosed on each room's
// interior.
func standing(rooms ...PlannedRoom) RoomObservation {
	var out RoomObservation
	for i, r := range rooms {
		out.Rooms = append(out.Rooms, Room{ID: strconv.Itoa(i), Enclosed: domain.Known(true), Cells: rectCells(r.Interior)})
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

func TestHerdRoomsHaveADoorAndHoldTheirBeds(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	for _, animals := range []int{10, 20, 45} {
		plan := herdTestPlan(t, animals)
		barns, vets := plan.HerdRooms(PlannedBarn), plan.HerdRooms(PlannedVetRoom)
		if len(barns) != 1 || len(vets) != 1 {
			t.Fatal("want one barn and one vet room", animals, len(barns), len(vets))
		}
		for _, room := range append(barns, vets...) {
			if _, ok := doorSide(room.Interior, room.Door); !ok {
				t.Fatal("door is not in the room's ring", room)
			}
			if _, ok := InteriorRoomFromLayout(room, testShapes); !ok {
				t.Fatal("room has no interior plan input", room)
			}
		}
		if got := herdRoomBeds(barns[0], testShapes, testHerdFurniture.Spot); got < animals {
			t.Fatal("barn holds fewer sleeping spots than animals", animals, got)
		}
		if got := herdRoomBeds(vets[0], testShapes, testHerdFurniture.Bed); got < VetBeds(animals) || got < 2 {
			t.Fatal("vet room holds fewer beds than the herd needs", animals, got)
		}
	}
}

// herdGround is the walls and doors standing on every room's ring.
func herdGround(plan LayoutPlan, rooms ...PlannedRoom) GroundCensus {
	out := GroundCensus{walls: map[domain.Cell]bool{}, doors: map[domain.Cell]bool{}}
	for _, r := range rooms {
		g := ringWalls(plan, r)
		for c := range g.walls {
			out.walls[c] = true
		}
		for c := range g.doors {
			out.doors[c] = true
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

func TestHerdStepReconcilesBarnThenVetRoom(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barn, vet := plan.HerdRooms(PlannedBarn)[0], plan.HerdRooms(PlannedVetRoom)[0]
	const animals = 12
	shapes := RoomObservation{Shapes: testShapes}
	// Shell: no ring stands, and the template already wants every spot.
	step := NextHerdStep(plan, shapes, GroundCensus{}, nil, nil, animals, testHerdFurniture)
	if step.Kind != HerdReconcile || !step.Room.Same(barn) || herdTemplateCount(step, testAnimalSpot) != animals {
		t.Fatal("barn reconcile first", step)
	}
	// The ring stands: the reconciler builds the spots on site, installs them from stock.
	ground := herdGround(plan, barn)
	step = NextHerdStep(plan, standing(barn), ground, nil, nil, animals, testHerdFurniture)
	if step.Kind != HerdReconcile || step.Role != PlannedBarn || herdTemplateCount(step, testAnimalSpot) != animals {
		t.Fatal("barn sleeping spots", step)
	}
	for _, p := range step.Template {
		if !rectInside(barn.Interior, Rectangle{X: p.Minimum.X, Z: p.Minimum.Z, Width: 1, Height: 1}) {
			t.Fatal("spot outside the barn", p)
		}
	}
	in := ReconcileInput{Plan: plan, Room: barn, Ground: ground, Rooms: standing(barn), Furniture: step.Template}
	if ops := ReconcileRoom(in); len(ops) != 1 || ops[0].Kind != OpBuild || len(ops[0].Pieces) != animals {
		t.Fatal("build on site", ops)
	}
	in.Stock = map[string]int{testAnimalSpot: 2}
	if ops := ReconcileRoom(in); len(ops) != 2 || ops[0].Kind != OpInstall || len(ops[0].Pieces) != 2 || ops[1].Kind != OpBuild || len(ops[1].Pieces) != animals-2 {
		t.Fatal("stock first", ops)
	}
	var built []CurrentBuilding
	for _, p := range step.Template {
		built = append(built, bedAt(t, p.DefName, InteriorPiece{Slot: p.Slot, Def: p.DefName, Size: p.Size, Rot: p.Rot, Rect: Rectangle{X: p.Minimum.X, Z: p.Minimum.Z, Width: 1, Height: 1}}))
	}
	step = NextHerdStep(plan, standing(barn), ground, built, nil, animals, testHerdFurniture)
	if step.Kind != HerdReconcile || !step.Room.Same(vet) {
		t.Fatal("vet room after the barn beds", step)
	}
	rooms := standing(barn, vet)
	ground = herdGround(plan, barn, vet)
	step = NextHerdStep(plan, rooms, ground, built, nil, animals, testHerdFurniture)
	if step.Kind != HerdReconcile || step.Role != PlannedVetRoom || herdTemplateCount(step, testAnimalBed) != VetBeds(animals) || !rectInside(vet.Interior, Rectangle{X: step.Template[0].Minimum.X, Z: step.Template[0].Minimum.Z, Width: 1, Height: 1}) {
		t.Fatal("vet animal beds", step)
	}
	for _, p := range step.Template {
		built = append(built, bedAt(t, p.DefName, InteriorPiece{Slot: p.Slot, Def: p.DefName, Size: p.Size, Rot: p.Rot, Rect: Rectangle{X: p.Minimum.X, Z: p.Minimum.Z, Width: 1, Height: 1}}))
	}
	if step = NextHerdStep(plan, rooms, ground, built, nil, animals, testHerdFurniture); step.Kind != HerdNone {
		t.Fatal("every bed stands", step)
	}
	// The herd grows: the barn is topped up with the new spots only.
	if step = NextHerdStep(plan, rooms, ground, built, nil, animals+3, testHerdFurniture); step.Kind != HerdReconcile || step.Role != PlannedBarn || herdTemplateCount(step, testAnimalSpot) != animals+3 {
		t.Fatal("barn topped up", step)
	}
	if step = NextHerdStep(plan, rooms, ground, nil, nil, 0, testHerdFurniture); step.Kind != HerdNone {
		t.Fatal("no animals owe no room", step)
	}
}

// A ring that lost a wall after the room stood is rebuilt by the same diff that
// raised it: no dedicated lost-shell step (#2114).
func TestHerdStepRebuildsALostWall(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barn := plan.HerdRooms(PlannedBarn)[0]
	const animals = 4
	// The barn alone: the vet room's own reconcile is not under test.
	var kept []LayoutReservation
	for _, r := range plan.Reservations {
		if r.Kind != ReserveVetRoom {
			kept = append(kept, r)
		}
	}
	plan.Reservations = kept
	layout, ok := PlanInterior(mustInterior(t, barn), testHerdFurniture.Spot)
	if !ok {
		t.Fatal("no barn layout")
	}
	var built []CurrentBuilding
	for _, p := range layout.Pieces[:animals] {
		built = append(built, bedAt(t, p.Def, p))
	}
	ground := herdGround(plan, barn)
	if step := NextHerdStep(plan, standing(barn), ground, built, nil, animals, testHerdFurniture); step.Kind != HerdNone {
		t.Fatal("a matching room owes nothing", step)
	}
	var lost domain.Cell
	for c := range ground.walls {
		lost = c
		break
	}
	delete(ground.walls, lost)
	step := NextHerdStep(plan, standing(barn), ground, built, nil, animals, testHerdFurniture)
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

func TestHerdStepFlagsEachStandingVetBedMedical(t *testing.T) {
	plan := herdTestPlan(t, 20)
	vet := plan.HerdRooms(PlannedVetRoom)[0]
	layout, ok := PlanInterior(mustInterior(t, vet), testHerdFurniture.Bed)
	if !ok {
		t.Fatal("no vet layout")
	}
	bed := bedAt(t, testAnimalBed, layout.Pieces[0])
	census := func(medical domain.Fact[bool]) []SleepingBed {
		return []SleepingBed{{ID: bed.ID, Medical: medical}}
	}
	// Without a barn, the vet room's own step is the first one read.
	only := plan
	only.Reservations = nil
	for _, r := range plan.Reservations {
		if r.Kind != ReserveBarn {
			only.Reservations = append(only.Reservations, r)
		}
	}
	ground := herdGround(only, vet)
	step := NextHerdStep(only, standing(vet), ground, []CurrentBuilding{bed}, census(domain.Known(false)), 1, testHerdFurniture)
	if step.Kind != HerdMedical || step.Bed != bed.ID {
		t.Fatal("flag the standing vet bed", step)
	}
	for _, medical := range []domain.Fact[bool]{domain.Known(true), domain.Unknown[bool]()} {
		if step = NextHerdStep(only, standing(vet), ground, []CurrentBuilding{bed}, census(medical), 1, testHerdFurniture); step.Kind == HerdMedical {
			t.Fatal("a flagged or unread bed is not flagged again", step)
		}
	}
}

func TestHerdOutgrowsItsRoomsAndAddsAnotherWithoutMovingAny(t *testing.T) {
	small := herdTestPlan(t, 10)
	grown := PlanHerdSites(small, 60)
	if len(grown.HerdRooms(PlannedBarn)) < 2 {
		t.Fatal("no second barn for a herd of 60", len(grown.HerdRooms(PlannedBarn)))
	}
	if grown.herdCapacity(PlannedBarn) < 60 || grown.herdCapacity(PlannedVetRoom) < VetBeds(60) {
		t.Fatal("rooms hold fewer beds than the herd", grown.herdCapacity(PlannedBarn), grown.herdCapacity(PlannedVetRoom))
	}
	for i, r := range small.Reservations {
		if grown.Reservations[i] != r {
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
	layout, ok := PlanInterior(mustInterior(t, first), testHerdFurniture.Spot)
	if !ok {
		t.Fatal("no barn layout")
	}
	for _, p := range layout.Pieces {
		built = append(built, bedAt(t, p.Def, p))
	}
	step := NextHerdStep(grown, rooms, herdGround(grown, first), built, nil, 60, testHerdFurniture)
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

func TestBarnAndVetRoomAreAnimalFurnitureRooms(t *testing.T) {
	f, err := Facility(RoomRoleBarn)
	if err != nil || f.Status != FacilityImplemented {
		t.Fatal("barn is an implemented facility", f, err)
	}
	if _, ok := InteriorTemplateFor(RoomRoleVetRoom); !ok {
		t.Fatal("vet room has a template")
	}
}

// A vet room walled against the barn never opens its door on the barn's wall.
func TestVetRoomDoorAvoidsNeighbourWall(t *testing.T) {
	plan := LayoutPlan{Reservations: []LayoutReservation{
		{Kind: ReserveBarn, Area: Rectangle{X: 0, Z: 10, Width: 8, Height: 8}},
		{Kind: ReserveVetRoom, Area: Rectangle{X: 0, Z: 2, Width: 8, Height: 8}},
	}}
	rooms := plan.HerdRooms(PlannedVetRoom)
	if len(rooms) != 1 {
		t.Fatalf("vet rooms = %d", len(rooms))
	}
	door := rooms[0].Door
	if door.Z == 9 && rooms[0].DoorRot == domain.North {
		t.Fatalf("door %v faces the barn's wall", door)
	}
}

// The planned vet room stands against the barn's wall: one shared wall with
// a link door, and its own door still opens outside.
func TestVetRoomSharesTheBarnWall(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barns, vets := plan.HerdRooms(PlannedBarn), plan.HerdRooms(PlannedVetRoom)
	if len(barns) == 0 || len(vets) == 0 {
		t.Fatal("no barn or vet room", len(barns), len(vets))
	}
	vet := vets[0]
	if vet.Link == nil {
		t.Fatalf("vet room %v has no door into the barn %v", vet.Interior, barns[0].Interior)
	}
	if !contains(roomWalls(vet), *vet.Link) || !contains(roomWalls(barns[0]), *vet.Link) {
		t.Fatalf("link %v is not on both walls", *vet.Link)
	}
	if *vet.Link == vet.Door || *vet.Link == barns[0].Door {
		t.Fatalf("link %v doubles a room's own door", *vet.Link)
	}
	if got := plan.ShellDoors(vet); !slices.Contains(got, *vet.Link) {
		t.Fatalf("shell doors %v omit the link", got)
	}
}
