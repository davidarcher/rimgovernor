package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// Nine worship rooms of mixed size and orientation replan to one: the built
// room when one stands, else the smallest that holds the shape (#1823).
func TestReplanRetiresDuplicateWorshipRooms(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	need, _ := WorshipRoomNeed(worshipIdeoligion())
	defs := furnitureDefs(map[string]Bounds{"TestAltar": {Width: 1, Height: 2}, "TestIdeogram": {Width: 1, Height: 1}})
	shape, _ := need.shape(defs)
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	var worship []PlannedRoom
	for i := int32(0); i < 9; i++ {
		in := Rectangle{X: 20 + 12*(i%3), Z: 20 + 12*(i/3), Width: 3 + i%4, Height: 3 + i%3}
		r := PlannedRoom{Role: PlannedWorship, Interior: in, Door: domain.Cell{X: in.X + 1, Z: in.Z - 1}, DoorRot: domain.South}
		if i%2 == 1 {
			r.Door, r.DoorRot = domain.Cell{X: in.X + in.Width, Z: in.Z + 1}, domain.East
		}
		worship = append(worship, r)
	}
	plan.Rooms = append(plan.Rooms, worship...)
	other := len(plan.Rooms) - len(worship)
	if DuplicateRooms(plan) != 8 {
		t.Fatal("duplicates", DuplicateRooms(plan))
	}
	count := func(p LayoutPlan) (n int) {
		for _, r := range p.AllRooms() {
			if r.Role == PlannedWorship {
				n++
			}
		}
		return n
	}
	growth := RoomGrowth{Shapes: []ChildRoomShape{shape}, Built: map[Rectangle]bool{}}
	next, changed, _ := ReplanLayoutWithRooms(plan, s, growth, 0, 3, 0, BuildTierCamp, nil, nil)
	if !changed || count(next) != 1 || len(next.Rooms) != other+1 {
		t.Fatalf("changed=%v worship=%d rooms=%d", changed, count(next), len(next.Rooms))
	}
	kept := next.roomsOf(PlannedWorship)[0]
	if w, d := frameDims(kept); !shape.holds(w, d) {
		t.Fatalf("kept room %+v does not hold the shape", kept)
	}
	for _, r := range worship {
		if w, d := frameDims(r); shape.holds(w, d) && r.Interior.Width*r.Interior.Height < kept.Interior.Width*kept.Interior.Height {
			t.Fatalf("kept %+v, smaller room %+v holds the shape", kept.Interior, r.Interior)
		}
	}
	if again, changed, _ := ReplanLayoutWithRooms(next, s, growth, 0, 3, 0, BuildTierCamp, nil, nil); changed || count(again) != 1 {
		t.Fatal("a reconciled plan replanned", changed)
	}

	// A built room wins, whatever its size, and the dropped built one is
	// no longer planned ground.
	built := worship[8]
	growth.Built = map[Rectangle]bool{built.Interior: true}
	next, _, _ = ReplanLayoutWithRooms(plan, s, growth, 0, 3, 0, BuildTierCamp, nil, nil)
	if got := next.roomsOf(PlannedWorship); len(got) != 1 || got[0].Interior != built.Interior {
		t.Fatalf("built room not kept: %+v", got)
	}
	growth.Built = map[Rectangle]bool{worship[0].Interior: true}
	next, _, _ = ReplanLayoutWithRooms(plan, s, growth, 0, 3, 0, BuildTierCamp, nil, nil)
	// The sited core may legitimately plan another room over the spot.
	planned := map[Rectangle]bool{}
	for _, r := range next.AllRooms() {
		if r.Role != PlannedWorship {
			planned[pad(r.Interior, 1)] = true
		}
	}
	for _, g := range PlannedGround(next, GroundCensus{}) {
		if planned[g] {
			continue
		}
		if g.X <= built.Interior.X && built.Interior.X < g.X+g.Width && g.Z <= built.Interior.Z && built.Interior.Z < g.Z+g.Height {
			t.Fatal("a dropped room is still planned ground", g)
		}
	}
}
