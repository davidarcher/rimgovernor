package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func coreTestZones() []LayoutZone {
	// Soil everywhere, a rock block in the east third.
	return Zone(zoningSurvey(120, func(x, z int32) SurveyCell {
		if x >= 80 {
			return SurveyCell{Rock: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	}))
}

// roomRect is a room with its walls.
func roomRect(r LayoutRoom) Rectangle {
	in := r.Interior
	return Rectangle{X: in.X - 1, Z: in.Z - 1, Width: in.Width + 2, Height: in.Height + 2}
}

func checkCore(t *testing.T, p LayoutPlan, pawns int) {
	t.Helper()
	if len(p.Spine) != 1 || !p.Valid() {
		t.Fatalf("plan %+v", p)
	}
	seg := p.Spine[0]
	hall := func(c domain.Cell) bool {
		return c.X >= seg.From.X && c.X <= seg.To.X && c.Z >= seg.From.Z-1 && c.Z <= seg.From.Z+1
	}
	count := map[ModuleRole]int{}
	for i, a := range p.Rooms {
		count[a.Role]++
		// The door is in the wall and opens on the hallway.
		step := int32(-1)
		if a.DoorRot == domain.North {
			step = 1
		}
		if !hall(domain.Cell{X: a.Door.X, Z: a.Door.Z + step}) {
			t.Fatal("room off the spine", a)
		}
		if a.Role == ModuleBedroom && a.Interior.Width*a.Interior.Height < 25 {
			t.Fatal("small bedroom", a)
		}
		for j, b := range p.Rooms {
			if i == j {
				continue
			}
			ra, rb := a.Interior, roomRect(b)
			// Interiors never overlap another room's walls or floor.
			if ra.X < rb.X+rb.Width && rb.X < ra.X+ra.Width && ra.Z < rb.Z+rb.Height && rb.Z < ra.Z+ra.Height {
				t.Fatal("overlap", a, b)
			}
		}
	}
	if count[ModuleBedroom] != pawns {
		t.Fatal("bedrooms", count)
	}
	for _, role := range coreBaseRooms {
		if count[role] != 1 {
			t.Fatal("missing", role)
		}
	}
}

func TestPlanCore(t *testing.T) {
	p := PlanCore(coreTestZones(), 3)
	checkCore(t, p, 3)
}

func TestGrowKeepsRooms(t *testing.T) {
	p := PlanCore(coreTestZones(), 3)
	g := Grow(p, 12)
	checkCore(t, g, 12)
	for i, r := range p.Rooms {
		if g.Rooms[i] != r {
			t.Fatal("moved", r, g.Rooms[i])
		}
	}
	dug := false
	for _, r := range g.Rooms {
		dug = dug || r.Dug
	}
	if !dug {
		t.Fatal("no room reached the rock")
	}
}

func TestGrowStopsAtEdge(t *testing.T) {
	g := PlanCore(coreTestZones(), 500)
	if len(g.Rooms) < 20 || len(g.Rooms) > 500 {
		t.Fatal("rooms", len(g.Rooms))
	}
	for _, r := range g.Rooms {
		if r.Interior.X < LayoutEdgeMargin || r.Interior.X+r.Interior.Width > 120-LayoutEdgeMargin {
			t.Fatal("off the core", r)
		}
	}
}
