package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func isolatable(pawn PawnID, triggered bool, area string, hungry bool) CreepJoinerHand {
	h := inspectable(pawn, triggered)
	h.Area, h.Hungry = domain.Known(area), domain.Known(hungry)
	return h
}

// TestIsolatedAreTheCreepJoinersHeldApart: a creepjoiner is held
// apart while its downside is known hidden and the record holds no finished
// inspection of it; a revealed one, an inspected one and a colonist that is no
// creepjoiner are not; an unread downside leaves the census unknown.
func TestIsolatedAreTheCreepJoinersHeldApart(t *testing.T) {
	plain := CreepJoinerHand{Pawn: "plain", Facts: CreepJoinerPawn{CreepJoiner: domain.Known[*CreepJoiner](nil), Traits: defNames(), Hediffs: defNames()}}
	record := CreepJoinerRecord{Inspections: map[PawnID]InspectionStage{"done": InspectionDone, "ordered": InspectionOrdered}}
	hands := []CreepJoinerHand{plain, isolatable("shown", true, "", false), isolatable("done", false, "", false), isolatable("hidden", false, "", false), isolatable("ordered", false, "", false)}
	got, known := downsideDefs.Isolated(hands, record).Value()
	if !known || !slices.Equal(got, []PawnID{"hidden", "ordered"}) {
		t.Fatalf("isolated = %v known=%v", got, known)
	}
	unread := isolatable("unread", false, "", false)
	unread.Facts.Traits = domain.Unknown[[]string]()
	if _, known := downsideDefs.Isolated(append(hands, unread), record).Value(); known {
		t.Fatal("an unread downside left the census known")
	}
	if got, known := downsideDefs.Isolated(nil, record).Value(); !known || len(got) != 0 {
		t.Fatalf("no colonists: %v %v", got, known)
	}
}

// TestIsolationMovesHoldAndRelease: a held-apart creepjoiner moves into the
// area once the room is ready; one in the area is released when its downside
// shows, its inspection is done or it is hungry; a hungry one outside is not
// moved in; nothing moves without the area or with unread facts.
func TestIsolationMovesHoldAndRelease(t *testing.T) {
	record := CreepJoinerRecord{Inspections: map[PawnID]InspectionStage{"inspected": InspectionDone}}
	const area = "Area_Allowed_7"
	for _, tc := range []struct {
		name  string
		hand  CreepJoinerHand
		area  string
		ready bool
		want  []IsolationMove
	}{
		{"held apart moves in", isolatable("p", false, "", false), area, true, []IsolationMove{{"p", area}}},
		{"room not ready", isolatable("p", false, "", false), area, false, nil},
		{"no area", isolatable("p", false, "", false), "", true, nil},
		{"already inside", isolatable("p", false, area, false), area, true, nil},
		{"downside shown releases", isolatable("p", true, area, false), area, true, []IsolationMove{{"p", ""}}},
		{"inspected releases", isolatable("inspected", false, area, false), area, false, []IsolationMove{{"inspected", ""}}},
		{"hungry releases", isolatable("p", false, area, true), area, true, []IsolationMove{{"p", ""}}},
		{"hungry outside stays out", isolatable("p", false, "", true), area, true, nil},
		{"revealed outside is left alone", isolatable("p", true, "Other", false), area, true, nil},
	} {
		got := downsideDefs.IsolationMoves([]CreepJoinerHand{tc.hand}, record, tc.area, tc.ready)
		if !slices.Equal(got, tc.want) {
			t.Errorf("%s: moves = %v, want %v", tc.name, got, tc.want)
		}
	}
	busy := isolatable("p", false, "", false)
	busy.Available = domain.Known(false)
	unreadArea := isolatable("p", false, "", false)
	unreadArea.Area = domain.Unknown[string]()
	unreadHunger := isolatable("p", false, "", false)
	unreadHunger.Hungry = domain.Unknown[bool]()
	for name, h := range map[string]CreepJoinerHand{"unavailable": busy, "area unread": unreadArea, "hunger unread": unreadHunger} {
		if got := downsideDefs.IsolationMoves([]CreepJoinerHand{h}, record, area, true); len(got) != 0 {
			t.Errorf("%s: moved %v", name, got)
		}
	}
}

// TestIsolationRoomNeedAndStanding: the room is owed while a creepjoiner is
// held apart and a bed resolves; it stands once enclosed with the bed inside.
func TestIsolationRoomNeedAndStanding(t *testing.T) {
	pawns := IsolationPlanning{Pawns: domain.Known([]PawnID{"7"}), Beds: []string{"Bed", "SleepingSpot"}}
	defs := furnitureDefs(map[string]Bounds{"Bed": {Width: 1, Height: 2}})
	need, owed := IsolationRoomNeed(pawns, defs)
	if !owed || need.Role != RoomRoleIsolationRoom || need.Module != PlannedIsolationRoom || len(need.Furniture) != 1 || need.Furniture[0].Count != 1 {
		t.Fatalf("need = %+v owed=%v", need, owed)
	}
	for name, p := range map[string]IsolationPlanning{
		"none isolated": {Pawns: domain.Known([]PawnID(nil)), Beds: pawns.Beds},
		"unread":        {Pawns: domain.Unknown[[]PawnID](), Beds: pawns.Beds},
		"no beds":       {Pawns: pawns.Pawns},
		"bed unsized":   {Pawns: pawns.Pawns, Beds: []string{"Cot"}},
	} {
		if _, owed := IsolationRoomNeed(p, defs); owed {
			t.Errorf("%s owes a room", name)
		}
	}
	plan, room := childRoomFixture(PlannedIsolationRoom)
	if IsolationRoomStanding(plan, GroundCensus{}, nil, need, defs) {
		t.Fatal("an unbuilt room stands")
	}
	rooms := tombStanding(room)
	if IsolationRoomStanding(plan, ringWalls(plan, room), nil, need, defs) {
		t.Fatal("a room without its bed stands")
	}
	step := NextChildRoomStep(plan, rooms, ringWalls(plan, room), nil, []ChildRoomNeed{need}, defs)
	if step.Kind != ChildRoomReconcile || len(step.Template) == 0 || step.Template[0].DefName != "Bed" {
		t.Fatalf("bed step = %+v", step)
	}
	built := piecesOf(t, step)
	if !IsolationRoomStanding(plan, ringWalls(plan, room), built, need, defs) {
		t.Fatal("a furnished enclosed room does not stand")
	}
	if cells := plan.IsolationRoomCells(); len(cells) != int(room.Interior.Width*room.Interior.Height) {
		t.Fatalf("area cells = %d", len(cells))
	}
}
