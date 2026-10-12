package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestOutskirtsSlotsAreDisjointInsideTheOutline(t *testing.T) {
	size := OutskirtsSize()
	area := Rectangle{X: 40, Z: 70, Width: size[0], Height: size[1]}
	l, ok := OutskirtsSlots(area)
	if !ok {
		t.Fatal("no slots for an outline of OutskirtsSize")
	}
	if _, small := OutskirtsSlots(Rectangle{Width: size[0] - 1, Height: size[1]}); small {
		t.Fatal("slots for an outline too narrow")
	}
	slots := map[string]Rectangle{"tomb": l.Tomb.Outline, "morgue": l.Morgue.Outline, "graveyard": l.Graveyard.Outline, "waste yard": l.WasteYard.Outline}
	for name, r := range slots {
		if r.X < area.X || r.Z < area.Z || r.X+r.Width > area.X+area.Width || r.Z+r.Height > area.Z+area.Height {
			t.Errorf("%s outside the outline: %+v", name, r)
		}
		for other, o := range slots {
			if name < other && rectsOverlap(r, o) {
				t.Errorf("%s overlaps %s", name, other)
			}
		}
	}
	if l.Tomb.Interior.Width != 5 || l.Tomb.Interior.Height != 5 || l.Morgue.Interior.Width != 5 || l.Morgue.Interior.Height != 4 {
		t.Errorf("tomb %+v morgue %+v", l.Tomb.Interior, l.Morgue.Interior)
	}
	if g := l.Graveyard.Interior; g.Width != GraveyardW || g.Height != GraveyardH {
		t.Errorf("graveyard %+v", g)
	}
	y := l.WasteYard.Interior
	if y.Width != WasteYardW || y.Height != WasteYardH {
		t.Errorf("waste yard %+v", y)
	}
	inc := l.Incinerator
	if inc.Width != IncineratorOutline || inc.X < y.X || inc.Z < y.Z || inc.X+inc.Width > y.X+y.Width || inc.Z+inc.Height > y.Z+y.Height {
		t.Errorf("incinerator %+v outside the yard %+v", inc, y)
	}
	// Every door stands in its own outline's wall, with the lane cell outside it open.
	// The unfenced yards have no door; they stand with their ring margin clear of the lane.
	for name, s := range map[string]OutskirtsSlot{"tomb": l.Tomb, "morgue": l.Morgue} {
		if !onRing(s.Door, s.Outline) {
			t.Errorf("%s door %+v off its wall", name, s.Door)
		}
		out := domain.Cell{X: s.Door.X, Z: s.Door.Z + 1}
		if s.DoorRot == domain.South {
			out.Z = s.Door.Z - 1
		}
		for other, o := range slots {
			if contains(o, out) {
				t.Errorf("%s door opens into %s", name, other)
			}
		}
	}
}

func TestMorgueIsNoDemandCoreRoom(t *testing.T) {
	if containsRole(demandCoreRooms, PlannedMorgue) {
		t.Fatal("the morgue is planned in the outskirts, not grown on demand")
	}
}
