package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Recorded royalty facts: a Yeoman with no throne requirement, a Knight
// whose throne room is 30 cells at impressiveness 55, and a Baron's larger
// one. Favor is what the native read would report.
func throneLadder() []RoyalRung {
	return []RoyalRung{
		{Title: "Yeoman", Seniority: domain.Known(100), FavorNeeded: domain.Known(6)},
		{Title: "Knight", Seniority: domain.Known(200), FavorNeeded: domain.Known(10), ThroneMinArea: domain.Known(30), ThroneMinImpressiveness: domain.Known(55), ThroneThings: []string{"Throne"}, ThroneAssigned: domain.Known(true)},
		{Title: "Baron", Seniority: domain.Known(300), FavorNeeded: domain.Known(16), ThroneMinArea: domain.Known(48), ThroneMinImpressiveness: domain.Known(70), ThroneThings: []string{"GrandThrone", "Throne"}, ThroneAssigned: domain.Known(true)},
	}
}

func royalHolder(title string, favor int) []RoyalHolding {
	return []RoyalHolding{{FactionDef: "Empire", Title: title, Favor: domain.Known(favor)}}
}

func TestThroneNeedFollowsTheNextTitle(t *testing.T) {
	cases := []struct {
		name  string
		holds []RoyalHolding
		want  string
		area  int
	}{
		{"a Yeoman is owed the Knight's room", royalHolder("Yeoman", 0), "Knight", 30},
		{"a Knight is owed the Baron's room", royalHolder("Knight", 0), "Baron", 48},
		{"a Baron has no higher rung and keeps its own", royalHolder("Baron", 0), "Baron", 48},
		{"favor enough for the first rung claims through it", royalHolder("", 6), "Knight", 30},
	}
	for _, c := range cases {
		need, ok := NextThroneNeed(RoyaltyFacts{Ladder: throneLadder(), Holders: map[PawnID][]RoyalHolding{"Alice": c.holds}})
		if !ok || need.Title != c.want || need.MinArea != c.area || need.Holder != "Alice" || need.Titled != (c.holds[0].Title != "") {
			t.Errorf("%s: %+v %v", c.name, need, ok)
		}
	}
}

func TestThroneNeedGatedOnATitleOrTheFavorToClaimOne(t *testing.T) {
	f := RoyaltyFacts{Ladder: throneLadder(), Holders: map[PawnID][]RoyalHolding{"Alice": royalHolder("", 5)}}
	if _, ok := NextThroneNeed(f); ok {
		t.Error("favor short of the first rung owes no room")
	}
	f.Holders["Alice"] = []RoyalHolding{{FactionDef: "Empire"}}
	if _, ok := NextThroneNeed(f); ok {
		t.Error("unknown favor owes no room")
	}
	if _, ok := NextThroneNeed(RoyaltyFacts{Ladder: throneLadder()}); ok {
		t.Error("no colonist holds anything")
	}
	// A rung with an unknown area is no requirement.
	ladder := throneLadder()
	ladder[1].ThroneMinArea = domain.Unknown[int]()
	ladder[2].ThroneThings = nil
	if _, ok := NextThroneNeed(RoyaltyFacts{Ladder: ladder, Holders: map[PawnID][]RoyalHolding{"Alice": royalHolder("Yeoman", 0)}}); ok {
		t.Error("no rung names a known area and a throne")
	}
}

func TestThroneNeedTakesTheLargestRoom(t *testing.T) {
	f := RoyaltyFacts{Ladder: throneLadder(), Holders: map[PawnID][]RoyalHolding{
		"Alice": royalHolder("Yeoman", 0),
		"Bob":   royalHolder("Knight", 0),
	}}
	need, ok := NextThroneNeed(f)
	if !ok || need.Holder != "Bob" || need.Title != "Baron" {
		t.Fatalf("%+v", need)
	}
}

func TestThroneRoomSizeMeetsTheArea(t *testing.T) {
	for _, area := range []int{1, 25, 30, 48, 80, 120} {
		sizes := ThroneRoomSizes(area)
		if len(sizes) == 0 {
			t.Fatalf("area %d: no shape", area)
		}
		for _, size := range sizes {
			if int(size[0])*int(size[1]) < area || size[0] < throneMinSide || size[1] < throneMinSide || size[1] > coreMaxDepth {
				t.Errorf("area %d: %v", area, size)
			}
		}
	}
	sizes := ThroneRoomSizes(30)
	if sizes[0] != [2]int32{6, 5} || sizes[1] != [2]int32{5, 6} {
		t.Errorf("30 cells: %v", sizes)
	}
}

func throneFixture() (LayoutPlan, LayoutRoom, ThroneNeed) {
	room := LayoutRoom{Role: ModuleThrone, Interior: Rectangle{X: 10, Z: 20, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 19}, DoorRot: domain.North}
	need := ThroneNeed{Holder: "Alice", Title: "Knight", MinArea: 30, MinImpressiveness: 55, Things: []string{"Throne"}, Assigned: true}
	return LayoutPlan{Rooms: []LayoutRoom{room}}, room, need
}

func throneDefs(size Bounds) []FurnitureDefinition {
	return []FurnitureDefinition{{Name: "Throne", Available: domain.Known(true), Size: domain.Known(size)}}
}

func standingThrone(t *testing.T, p InteriorPiece) CurrentBuilding {
	t.Helper()
	b, err := domain.NewBuilding("Throne", p.Anchor(), p.Rot, "")
	if err != nil {
		t.Fatal(err)
	}
	return CurrentBuilding{ID: "Throne_1", Building: b, Cells: rectCells(p.Rect)}
}

func TestThroneStepShellsPlacesThenAssigns(t *testing.T) {
	plan, room, need := throneFixture()
	defs := throneDefs(Bounds{Width: 1, Height: 1})
	if step := NextThroneStep(plan, RoomObservation{Shapes: testShapes}, nil, need, defs, nil); step.Kind != ThroneShell || step.Room != room || !step.Owed() {
		t.Fatalf("unbuilt room: %+v", step)
	}
	step := NextThroneStep(plan, tombStanding(room), nil, need, defs, nil)
	if step.Kind != ThronePlace || step.Piece.Def != "Throne" || step.Piece.Slot != throneSlot || !step.Owed() {
		t.Fatalf("standing room: %+v", step)
	}
	// The throne sits centred against the back wall.
	if got := step.Piece.Rect; got.X != 12 || got.Z != 24 || got.Width != 1 || got.Height != 1 {
		t.Fatalf("throne footprint %+v", got)
	}
	built := []CurrentBuilding{standingThrone(t, step.Piece)}
	// A throne the royalty read has not listed yet waits for the next read.
	if step := NextThroneStep(plan, tombStanding(room), built, need, defs, nil); step.Kind != ThroneNone {
		t.Fatalf("throne built after the read: %+v", step)
	}
	need.Titled = true
	unowned := []RoyalThrone{{ID: "Throne_1", Def: "Throne"}}
	step = NextThroneStep(plan, tombStanding(room), built, need, defs, unowned)
	if step.Kind != ThroneAssign || step.Throne != "Throne_1" || step.PreviousThrone != "" || !step.Owed() {
		t.Fatalf("unowned throne is assigned to the holder: %+v", step)
	}
	// The holder's other throne is the expected previous assignment.
	moved := []RoyalThrone{{ID: "Throne_1", Def: "Throne"}, {ID: "Throne_0", Def: "Throne", Owner: need.Holder}}
	if step := NextThroneStep(plan, tombStanding(room), built, need, defs, moved); step.Kind != ThroneAssign || step.PreviousThrone != "Throne_0" {
		t.Fatalf("previous throne: %+v", step)
	}
	// Done once the read lists the holder as the owner; a throne someone
	// else owns is left alone.
	for _, owner := range []PawnID{need.Holder, "other"} {
		owned := []RoyalThrone{{ID: "Throne_1", Def: "Throne", Owner: owner}}
		if step := NextThroneStep(plan, tombStanding(room), built, need, defs, owned); step.Kind != ThroneNone || step.Owed() {
			t.Fatalf("owner %s: %+v", owner, step)
		}
	}
	// Native lets only a titled colonist own a throne.
	untitled := need
	untitled.Titled = false
	if step := NextThroneStep(plan, tombStanding(room), built, untitled, defs, unowned); step.Kind != ThroneNone {
		t.Fatalf("holder without a title: %+v", step)
	}
	need.Assigned = false
	if step := NextThroneStep(plan, tombStanding(room), built, need, defs, unowned); step.Kind != ThroneNone {
		t.Fatalf("nothing to assign: %+v", step)
	}
}

func TestThroneFootprintComesFromTheCatalog(t *testing.T) {
	plan, room, need := throneFixture()
	step := NextThroneStep(plan, tombStanding(room), nil, need, throneDefs(Bounds{Width: 2, Height: 1}), nil)
	if step.Kind != ThronePlace || step.Piece.Rect.Width != 2 || step.Piece.Rect.Height != 1 || step.Piece.Rect.X != 12 {
		t.Fatalf("a 2x1 throne: %+v", step.Piece.Rect)
	}
	unknown := []FurnitureDefinition{{Name: "Throne", Available: domain.Known(true), Size: domain.Unknown[Bounds]()}}
	if step := NextThroneStep(plan, tombStanding(room), nil, need, unknown, nil); step.Kind != ThroneNone {
		t.Fatalf("no footprint, no placement: %+v", step)
	}
	locked := []FurnitureDefinition{{Name: "Throne", Available: domain.Known(false), Size: domain.Known(Bounds{Width: 1, Height: 1})}}
	if step := NextThroneStep(plan, tombStanding(room), nil, need, locked, nil); step.Kind != ThroneNone {
		t.Fatalf("unavailable throne: %+v", step)
	}
}

func TestThroneStepWaitsForARoomOfTheTitlesArea(t *testing.T) {
	plan, _, need := throneFixture()
	need.MinArea = 48
	if step := NextThroneStep(plan, RoomObservation{Shapes: testShapes}, nil, need, throneDefs(Bounds{Width: 1, Height: 1}), nil); step.Kind != ThroneNone {
		t.Fatalf("a 30 cell room cannot meet 48: %+v", step)
	}
	if want := ThroneAreaOwed(plan, need, true); want != 48 {
		t.Fatalf("the plan owes a 48 cell room, got %d", want)
	}
	need.MinArea = 30
	if want := ThroneAreaOwed(plan, need, true); want != 0 {
		t.Fatalf("a 30 cell room stands in the plan, got %d", want)
	}
	if want := ThroneAreaOwed(plan, need, false); want != 0 {
		t.Fatalf("nobody owed, got %d", want)
	}
}

func TestThroneRoomTargetIsTheTitlesImpressiveness(t *testing.T) {
	plan, room, need := throneFixture()
	if got := ThroneRoomTargets(plan, RoomObservation{Shapes: testShapes}, need); got != nil {
		t.Fatalf("room not standing: %+v", got)
	}
	got := ThroneRoomTargets(plan, tombStanding(room), need)
	target, ok := got["r1"]
	if !ok || target.Min != 55 || len(target.Reasons) != 1 || target.Reasons[0] != "title" {
		t.Fatalf("%+v", got)
	}
	need.MinImpressiveness = 0
	if got := ThroneRoomTargets(plan, tombStanding(room), need); got != nil {
		t.Fatalf("no impressiveness asked: %+v", got)
	}
}

func TestThroneTemplateFurnishesAroundTheThrone(t *testing.T) {
	_, room, _ := throneFixture()
	in, ok := InteriorRoomFromLayout(room, testShapes)
	if !ok {
		t.Fatal("throne room has no interior role")
	}
	full, ok := PlanInterior(in, InteriorPieceDef{Def: "Throne", Size: domain.Cell{X: 1, Z: 1}})
	if !ok {
		t.Fatal("no plan")
	}
	slots := map[string]bool{}
	for _, p := range full.Pieces {
		slots[p.Slot] = true
	}
	for _, want := range []string{throneSlot, "end_table", "dresser", "lamp"} {
		if !slots[want] {
			t.Errorf("missing %s in %+v", want, full.Pieces)
		}
	}
	// The closer's plan names the furniture alone.
	bare, ok := PlanInterior(in, InteriorPieceDef{})
	if !ok {
		t.Fatal("no furnishing plan")
	}
	for _, p := range bare.Pieces {
		if p.Slot == throneSlot {
			t.Fatal("furnishing plan placed a throne")
		}
	}
}

func TestGrowThroneRoomAddsOneAndKeepsTheRest(t *testing.T) {
	base := Grow(LayoutPlan{Zones: coreTestZones()}, 6, 1, BuildTierCamp)
	have := len(base.Rooms)
	grown, added, _ := growThroneRoom(base, 30)
	if !added || len(grown.Rooms) != have+1 {
		t.Fatalf("added=%v rooms %d -> %d", added, have, len(grown.Rooms))
	}
	for i := range base.Rooms {
		if grown.Rooms[i] != base.Rooms[i] {
			t.Fatalf("room %d moved", i)
		}
	}
	room, ok := grown.ThroneRoomFor(30)
	if !ok || room.Interior.Width*room.Interior.Height < 30 {
		t.Fatalf("no throne room of 30 cells: %+v", room)
	}
	if _, err := CheckRoutes(grown); err != nil {
		t.Fatal(err)
	}
	if again, added, _ := growThroneRoom(grown, 30); added || len(again.Rooms) != len(grown.Rooms) {
		t.Fatal("a second room for the same area")
	}
	if bigger, added, _ := growThroneRoom(grown, 48); !added || len(bigger.Rooms) != len(grown.Rooms)+1 {
		t.Fatal("a title that outgrows the room adds a larger one")
	}
	if _, added, _ := growThroneRoom(grown, 0); added {
		t.Fatal("no area asked")
	}
}

func TestThroneRoomRoleTables(t *testing.T) {
	if role, ok := LayoutModule(RoomRoleThroneRoom); !ok || role != ModuleThrone {
		t.Fatal("the ThroneRoom role has no layout module")
	}
	if moduleRoomRoles[ModuleThrone] != RoomRoleThroneRoom || roomDistricts[ModuleThrone] != DistrictHousing {
		t.Fatal("throne room tables")
	}
	if f, err := Facility(RoomRoleThroneRoom); err != nil || f.Status != FacilityImplemented {
		t.Fatalf("catalog row: %+v %v", f, err)
	}
}
