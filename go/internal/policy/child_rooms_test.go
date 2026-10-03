package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func stagePawn(stage string) WorkPawn {
	return WorkPawn{Biotech: domain.Known(PawnBiotech{DevelopmentalStage: domain.Known(stage)})}
}

func furnitureDefs(sizes map[string]Bounds) []FurnitureDefinition {
	var defs []FurnitureDefinition
	for name, size := range sizes {
		defs = append(defs, FurnitureDefinition{Name: name, Available: domain.Known(true), Size: domain.Known(size), Roles: childRoles[name]})
	}
	return defs
}

var childDefs = map[string]Bounds{
	"Crib": {Width: 1, Height: 1}, "ToyBox": {Width: 1, Height: 1}, "BabyDecoration": {Width: 1, Height: 1},
	"Blackboard": {Width: 2, Height: 1}, "SchoolDesk": {Width: 2, Height: 1},
}

// childRoles are the roles the native catalog assigns the fixture defs.
var childRoles = map[string][]string{
	"Crib": {"BabyBed"}, "ToyBox": {"Toy"}, "BabyDecoration": {"Decoration"}, "Blackboard": {"Board"}, "SchoolDesk": {"Desk"},
	"DeathrestCasket": {"DeathrestCasket"}, "DeathrestAccelerator": {"DeathrestAccelerator"},
}

func TestChildRoomNeedsFollowDevelopmentalStages(t *testing.T) {
	roles := func(pawns ...WorkPawn) []RoomRole {
		var out []RoomRole
		for _, n := range ChildRoomNeeds(pawns) {
			out = append(out, n.Role)
		}
		return out
	}
	if got := roles(stagePawn("Adult"), WorkPawn{}); len(got) != 0 {
		t.Fatalf("adults and unknown pawns owe nothing: %v", got)
	}
	if got := roles(stagePawn("Newborn")); len(got) != 1 || got[0] != RoomRoleNursery {
		t.Fatalf("a newborn owes a nursery only: %v", got)
	}
	if got := roles(stagePawn("Baby")); len(got) != 2 || got[0] != RoomRoleNursery || got[1] != RoomRolePlayroom {
		t.Fatalf("a baby owes a nursery and playroom: %v", got)
	}
	if got := roles(stagePawn("Child")); len(got) != 2 || got[0] != RoomRolePlayroom || got[1] != RoomRoleClassroom {
		t.Fatalf("a child owes a playroom and classroom: %v", got)
	}
	needs := ChildRoomNeeds([]WorkPawn{stagePawn("Baby"), stagePawn("Baby"), stagePawn("Baby"), stagePawn("Child"), stagePawn("Child")})
	if needs[0].Furniture[0].Count != 3 || needs[2].Furniture[1].Count != 2 {
		t.Fatalf("a bed per baby and a desk per child: %+v", needs)
	}
	// The game scores no Nursery under two baby beds.
	if one := ChildRoomNeeds([]WorkPawn{stagePawn("Newborn")}); one[0].Furniture[0].Count != 2 {
		t.Fatalf("one baby still needs two beds: %+v", one)
	}
}

func TestChildRoomFurnitureIsTheFacilityRows(t *testing.T) {
	for _, role := range []RoomRole{RoomRoleNursery, RoomRolePlayroom, RoomRoleClassroom, RoomRoleDeathrestChamber} {
		f, err := Facility(role)
		if err != nil || f.Status != FacilityImplemented || len(f.Roles) == 0 {
			t.Fatalf("%s: %+v %v", role, f, err)
		}
	}
	needs := ChildRoomNeeds([]WorkPawn{stagePawn("Baby"), stagePawn("Child"), deathresterPawn(2)})
	if len(needs) != 4 {
		t.Fatalf("a baby, a child and a deathrester owe four rooms: %+v", needs)
	}
	for _, n := range needs {
		f, _ := Facility(n.Role)
		for _, furniture := range n.Furniture {
			if !slices.Contains(f.Roles, furniture.Role) {
				t.Errorf("%s places role %s, absent from its facility row %v", n.Role, furniture.Role, f.Roles)
			}
		}
	}
}

func deathresterPawn(capacity int) WorkPawn {
	return WorkPawn{Biotech: domain.Known(PawnBiotech{Deathrest: domain.Known(&PawnDeathrest{Capacity: domain.Known(capacity)})})}
}

func TestDeathrestChamberFollowsDeathresters(t *testing.T) {
	if got := ChildRoomNeeds([]WorkPawn{stagePawn("Adult")}); len(got) != 0 {
		t.Fatalf("no deathrester owes no chamber: %+v", got)
	}
	needs := ChildRoomNeeds([]WorkPawn{deathresterPawn(3), deathresterPawn(1)})
	if len(needs) != 1 || needs[0].Role != RoomRoleDeathrestChamber || needs[0].Module != ModuleDeathrestChamber {
		t.Fatalf("deathresters owe a chamber: %+v", needs)
	}
	if f := needs[0].Furniture; f[0].Role != RoleDeathrestCasket || f[0].Count != 2 || f[1].Role != RoleDeathrestAccelerator || f[1].Count != 2 || !f[1].Optional {
		t.Fatalf("a casket per deathrester, accelerators beyond capacity one: %+v", f)
	}
	plan, room := childRoomFixture(ModuleDeathrestChamber)
	defs := furnitureDefs(map[string]Bounds{"DeathrestCasket": {Width: 1, Height: 2}})
	// The accelerator is optional: the catalog lacking one still furnishes caskets.
	if step := NextChildRoomStep(plan, RoomObservation{Shapes: testShapes}, nil, needs, defs); step.Kind != ChildRoomShell || step.Room != room {
		t.Fatalf("chamber shell: %+v", step)
	}
	rooms := tombStanding(room)
	if step := NextChildRoomStep(plan, rooms, nil, needs, defs); step.Kind != ChildRoomPlace || step.Piece.Def != "DeathrestCasket" {
		t.Fatalf("casket placement: %+v", step)
	}
	// A required role the catalog does not carry leaves the room unowed.
	if step := NextChildRoomStep(plan, rooms, nil, needs, furnitureDefs(map[string]Bounds{"DeathrestAccelerator": {Width: 1, Height: 1}})); step.Owed() {
		t.Fatalf("no casket in the catalog: %+v", step)
	}
}

func TestChildRoomFurnitureComesFromCatalogRoles(t *testing.T) {
	// A def is furniture by its catalog role, not its name.
	defs := furnitureDefs(map[string]Bounds{"CosyCot": {Width: 1, Height: 1}})
	defs[0].Roles = []string{"BabyBed"}
	plan, room := childRoomFixture(ModuleNursery)
	needs := ChildRoomNeeds([]WorkPawn{stagePawn("Newborn"), stagePawn("Newborn")})[:1]
	if step := NextChildRoomStep(plan, tombStanding(room), nil, needs, defs); step.Kind != ChildRoomPlace || step.Piece.Def != "CosyCot" {
		t.Fatalf("a role-carrying def is placed: %+v", step)
	}
	defs[0].Roles = nil
	if step := NextChildRoomStep(plan, tombStanding(room), nil, needs, defs); step.Owed() {
		t.Fatalf("a def without the role is not a baby bed: %+v", step)
	}
}

func TestChildRoomSizesHoldTheirFurniture(t *testing.T) {
	shape := ChildRoomShape{Module: ModuleClassroom, Pieces: []PieceCount{{Size: domain.Cell{X: 2, Z: 1}, Count: 7}}}
	sizes := ChildRoomSizes(shape)
	if len(sizes) == 0 {
		t.Fatal("no size holds seven desks")
	}
	for _, s := range sizes {
		if !shape.holds(s[0], s[1]) || s[1] > coreMaxDepth {
			t.Errorf("size %v does not hold the shape", s)
		}
	}
	// Fewest cells first.
	for i := 1; i < len(sizes); i++ {
		if sizes[i-1][0]*sizes[i-1][1] > sizes[i][0]*sizes[i][1] {
			t.Fatalf("sizes not ordered by area: %v", sizes)
		}
	}
	bigger := ChildRoomShape{Module: ModuleClassroom, Pieces: []PieceCount{{Size: domain.Cell{X: 2, Z: 1}, Count: 14}}}
	if a, b := sizes[0], ChildRoomSizes(bigger)[0]; a[0]*a[1] >= b[0]*b[1] {
		t.Fatalf("more desks need more floor: %v then %v", a, b)
	}
}

func childRoomFixture(role ModuleRole) (LayoutPlan, LayoutRoom) {
	room := LayoutRoom{Role: role, Interior: Rectangle{X: 10, Z: 20, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 19}, DoorRot: domain.North}
	return LayoutPlan{Rooms: []LayoutRoom{room}}, room
}

func standingPiece(t *testing.T, def string, p InteriorPiece) CurrentBuilding {
	t.Helper()
	b, err := domain.NewBuilding(def, p.Anchor(), p.Rot, "")
	if err != nil {
		t.Fatal(err)
	}
	return CurrentBuilding{ID: def + p.Slot, Building: b, Cells: rectCells(p.Rect)}
}

func TestNextChildRoomStepShellsThenFurnishesTheNursery(t *testing.T) {
	plan, room := childRoomFixture(ModuleNursery)
	needs := ChildRoomNeeds([]WorkPawn{stagePawn("Newborn"), stagePawn("Newborn")})[:1]
	defs := furnitureDefs(childDefs)
	if step := NextChildRoomStep(plan, RoomObservation{Shapes: testShapes}, nil, needs, defs); step.Kind != ChildRoomShell || step.Room != room || !step.Owed() {
		t.Fatalf("unbuilt room: %+v", step)
	}
	rooms := tombStanding(room)
	var built []CurrentBuilding
	cells := map[domain.Cell]bool{}
	for i := 0; i < 2; i++ {
		step := NextChildRoomStep(plan, rooms, built, needs, defs)
		if step.Kind != ChildRoomPlace || step.Piece.Def != "Crib" || !step.Owed() {
			t.Fatalf("crib %d: %+v", i, step)
		}
		in := room.Interior
		if r := step.Piece.Rect; r.X < in.X || r.Z < in.Z || r.X+r.Width > in.X+in.Width || r.Z+r.Height > in.Z+in.Height {
			t.Fatalf("crib outside the room: %+v", r)
		}
		for _, c := range rectCells(step.Piece.Rect) {
			if cells[c] {
				t.Fatalf("crib %d overlaps an earlier one at %v", i, c)
			}
			cells[c] = true
		}
		// The row inside the entrance stays floor.
		if step.Piece.Rect.Z == in.Z {
			t.Fatalf("crib on the entrance row: %+v", step.Piece.Rect)
		}
		built = append(built, standingPiece(t, "Crib", step.Piece))
	}
	if step := NextChildRoomStep(plan, rooms, built, needs, defs); step.Kind != ChildRoomNone || step.Owed() {
		t.Fatalf("two cribs stand: %+v", step)
	}
}

func TestNextChildRoomStepPlacesEachPieceTheRoleScores(t *testing.T) {
	plan, room := childRoomFixture(ModuleClassroom)
	needs := ChildRoomNeeds([]WorkPawn{stagePawn("Child")})[1:]
	if len(needs) != 1 || needs[0].Role != RoomRoleClassroom {
		t.Fatalf("needs: %+v", needs)
	}
	defs := furnitureDefs(childDefs)
	rooms := tombStanding(room)
	var built []CurrentBuilding
	var placed []string
	for i := 0; i < 4; i++ {
		step := NextChildRoomStep(plan, rooms, built, needs, defs)
		if step.Kind != ChildRoomPlace {
			break
		}
		placed = append(placed, step.Piece.Def)
		if step.Piece.Rect.Width != 2 || step.Piece.Rect.Height != 1 {
			t.Fatalf("the footprint is the catalog's 2x1: %+v", step.Piece.Rect)
		}
		built = append(built, standingPiece(t, step.Piece.Def, step.Piece))
	}
	if len(placed) != 2 || placed[0] != "Blackboard" || placed[1] != "SchoolDesk" {
		t.Fatalf("a blackboard then the child's desk: %v", placed)
	}
}

func TestNextChildRoomStepWaitsOnTheCatalogAndThePlan(t *testing.T) {
	plan, room := childRoomFixture(ModuleNursery)
	needs := ChildRoomNeeds([]WorkPawn{stagePawn("Newborn")})[:1]
	locked := []FurnitureDefinition{{Name: "Crib", Roles: []string{"BabyBed"}, Available: domain.Known(false), Size: domain.Known(Bounds{Width: 1, Height: 1})}}
	unknown := []FurnitureDefinition{{Name: "Crib", Roles: []string{"BabyBed"}, Available: domain.Known(true), Size: domain.Unknown[Bounds]()}}
	for name, defs := range map[string][]FurnitureDefinition{"locked": locked, "unknown size": unknown, "absent": nil} {
		if step := NextChildRoomStep(plan, tombStanding(room), nil, needs, defs); step.Kind != ChildRoomNone {
			t.Errorf("%s crib: %+v", name, step)
		}
	}
	// The baby sleeping spot answers when the crib is not researched.
	spot := []FurnitureDefinition{locked[0], {Name: "BabySleepingSpot", Roles: []string{"BabyBed"}, Available: domain.Known(true), Size: domain.Known(Bounds{Width: 1, Height: 1})}}
	if step := NextChildRoomStep(plan, tombStanding(room), nil, needs, spot); step.Kind != ChildRoomPlace || step.Piece.Def != "BabySleepingSpot" {
		t.Fatalf("fallback bed: %+v", step)
	}
	// A plan with no nursery room leaves the room to the layout review.
	empty, _ := childRoomFixture(ModulePlayroom)
	if step := NextChildRoomStep(empty, tombStanding(room), nil, needs, furnitureDefs(childDefs)); step.Kind != ChildRoomNone {
		t.Fatalf("no planned nursery: %+v", step)
	}
	owed := ChildRoomsOwed(empty, needs, furnitureDefs(childDefs))
	if len(owed) != 1 || owed[0].Module != ModuleNursery {
		t.Fatalf("the plan owes a nursery: %+v", owed)
	}
	if owed := ChildRoomsOwed(plan, needs, furnitureDefs(childDefs)); len(owed) != 0 {
		t.Fatalf("the nursery is planned: %+v", owed)
	}
	if owed := ChildRoomsOwed(empty, needs, nil); len(owed) != 0 {
		t.Fatalf("unknown furniture sizes no room: %+v", owed)
	}
}

func TestGrowChildRoomAddsOneAndKeepsTheRest(t *testing.T) {
	base := Grow(LayoutPlan{Zones: coreTestZones()}, 6, 1, BuildTierCamp)
	shape := ChildRoomShape{Module: ModuleNursery, Pieces: []PieceCount{{Size: domain.Cell{X: 1, Z: 1}, Count: 4}}}
	grown, added := growChildRoom(base, shape)
	if !added || len(grown.Rooms) != len(base.Rooms)+1 {
		t.Fatalf("added=%v rooms %d -> %d", added, len(base.Rooms), len(grown.Rooms))
	}
	for i := range base.Rooms {
		if grown.Rooms[i] != base.Rooms[i] {
			t.Fatalf("room %d moved", i)
		}
	}
	if _, ok := grown.ChildRoomFor(shape); !ok {
		t.Fatal("no nursery holds four cribs")
	}
	if _, err := CheckRoutes(grown); err != nil {
		t.Fatal(err)
	}
	if again, added := growChildRoom(grown, shape); added || len(again.Rooms) != len(grown.Rooms) {
		t.Fatal("a nursery already holds the cribs")
	}
	if _, added := growChildRoom(grown, ChildRoomShape{Module: ModuleNursery}); added {
		t.Fatal("no furniture, no room")
	}
}

// A room beside a north-south hallway is stored transposed (door east or
// west); it must still count as holding the shape it was grown for, or every
// review grows another.
func TestChildRoomForTransposedRoom(t *testing.T) {
	shape := ChildRoomShape{Module: ModuleWorship, Pieces: []PieceCount{{Size: domain.Cell{X: 1, Z: 1}, Count: 6}}}
	var size [2]int32
	for _, s := range ChildRoomSizes(shape) {
		if s[0] != s[1] {
			size = s
			break
		}
	}
	if size == ([2]int32{}) {
		t.Skip("every size holding the shape is square")
	}
	room := LayoutRoom{Role: ModuleWorship, Interior: Rectangle{X: 10, Z: 20, Width: size[1], Height: size[0]}, Door: domain.Cell{X: 9, Z: 21}, DoorRot: domain.East}
	if _, ok := (LayoutPlan{Rooms: []LayoutRoom{room}}).ChildRoomFor(shape); !ok {
		t.Fatalf("transposed %v room does not hold the shape", size)
	}
}
