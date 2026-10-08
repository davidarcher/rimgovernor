package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
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
	for name, s := range map[string]OutskirtsSlot{"tomb": l.Tomb, "morgue": l.Morgue, "graveyard": l.Graveyard, "waste yard": l.WasteYard} {
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

func TestOutskirtsPlanHoldsTombAndMorgueFromTheStart(t *testing.T) {
	plan := outskirtsPlan(150)
	size := OutskirtsSize()
	plan, grown := growOutskirts(plan, size)
	if !grown || !OutskirtsOwed(plan) {
		t.Fatalf("cluster grown=%v owed=%v", grown, OutskirtsOwed(plan))
	}
	plan, added := growOutskirtsRooms(plan, nil)
	if !added || OutskirtsOwed(plan) {
		t.Fatalf("rooms added=%v owed=%v", added, OutskirtsOwed(plan))
	}
	area, _ := plan.OutskirtsArea()
	l, _ := OutskirtsSlots(area)
	tombs, morgues := plan.roomsOf(PlannedTomb), plan.roomsOf(PlannedMorgue)
	if len(tombs) != 1 || tombs[0].Interior != l.Tomb.Interior || tombs[0].Door != l.Tomb.Door {
		t.Fatalf("tomb %+v want slot %+v", tombs, l.Tomb)
	}
	if len(morgues) != 1 || morgues[0].Interior != l.Morgue.Interior {
		t.Fatalf("morgue %+v want slot %+v", morgues, l.Morgue)
	}
	for _, r := range []PlannedRoom{tombs[0], morgues[0]} {
		if _, _, has := plan.CoolerExhaust(r); !has {
			t.Errorf("%s has no cooler exhaust", r.Role)
		}
		for _, other := range plan.AllRooms() {
			if other.Role != PlannedTomb && other.Role != PlannedMorgue && other.Role != PlannedGraveyard && other.Role != PlannedWasteYard && other.Role != PlannedIncinerator && rectsOverlap(pad(roomWalls(r), outskirtsGap-1), roomWalls(other)) {
				t.Errorf("%s within the gap of %s", r.Role, other.Role)
			}
		}
	}
	if again, added := growOutskirtsRooms(plan, nil); added || len(again.Rooms) != len(plan.Rooms) {
		t.Fatal("rooms grown twice")
	}
	// A plan that already holds a tomb elsewhere is owed no second one.
	core := outskirtsPlan(150)
	core.Rooms = append(core.Rooms, PlannedRoom{Role: PlannedTomb, Interior: Rectangle{X: 1, Z: 1, Width: 5, Height: 5}})
	core, _ = growOutskirts(core, size)
	core, _ = growOutskirtsRooms(core, nil)
	if got := len(core.roomsOf(PlannedTomb)); got != 1 {
		t.Fatalf("tombs %d", got)
	}
}

// The waste yard is an Outdoor room (a fence and gate, no roof, no floor owed)
// planned from the start, with the incinerator's walled room in its far
// corner and the dump's ground, 52 cells, beside it.
func TestOutskirtsPlanHoldsWasteYardWithIncineratorInside(t *testing.T) {
	plan, _ := growOutskirts(outskirtsPlan(150), OutskirtsSize())
	plan, _ = growOutskirtsRooms(plan, nil)
	area, _ := plan.OutskirtsArea()
	l, _ := OutskirtsSlots(area)
	yards, incs := plan.roomsOf(PlannedWasteYard), plan.IncineratorRooms()
	if len(yards) != 1 || len(incs) != 1 {
		t.Fatalf("yards %d incinerators %d", len(yards), len(incs))
	}
	yard, inc := yards[0], incs[0]
	if !yard.Outdoor || yard.Interior != l.WasteYard.Interior || yard.Door != l.WasteYard.Door || yard.DoorRot != domain.South {
		t.Errorf("yard %+v want slot %+v", yard, l.WasteYard)
	}
	if wall, door := yard.RingDefs(); wall != PenFenceDefinition || door != PenGateDefinition {
		t.Errorf("yard ring %s %s", wall, door)
	}
	if inc.Outdoor || inc.Interior.Width != 3 || inc.Interior.Height != 3 || roomWalls(inc) != l.Incinerator {
		t.Errorf("incinerator %+v outline %+v", inc, l.Incinerator)
	}
	if wall, door := inc.RingDefs(); wall != ShellWallDefinition || door != ShellDoorDefinition {
		t.Errorf("incinerator ring %s %s", wall, door)
	}
	if !contains(yard.Interior, inc.Door) || !onRing(inc.Door, roomWalls(inc)) {
		t.Errorf("incinerator door %+v not inside the yard", inc.Door)
	}
	free := 0
	for _, c := range rectCells(yard.Interior) {
		if !contains(roomWalls(inc), c) {
			free++
		}
	}
	if free != 52 {
		t.Errorf("dump ground %d cells, want 52", free)
	}
	if again, added := growOutskirtsRooms(plan, nil); added || len(again.Rooms) != len(plan.Rooms) {
		t.Fatal("yard or incinerator planned twice")
	}
	// The incinerator is permanent: a replan with no incinerator need keeps it.
	if len(plan.roomsOf(PlannedIncinerator)) != 1 {
		t.Fatal("incinerator dropped")
	}
}

func TestDeriveLayoutPlanPlansTheOutskirtsRooms(t *testing.T) {
	slowtest.Skip(t, "two full layout derives on a 220-cell map; runs under cmd/test -full and nightly")
	survey := zoningSurvey(220, func(x, z int32) SurveyCell {
		if x >= 150 {
			return SurveyCell{Rock: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
	plan, known := DeriveLayoutPlan(survey, 3, BuildTierCamp, nil, 0, 0).Value()
	if !known {
		t.Fatal("no plan")
	}
	if OutskirtsOwed(plan) {
		t.Fatalf("outskirts not planned: %s", plan.Summary())
	}
	again, _ := DeriveLayoutPlan(survey, 3, BuildTierCamp, nil, 0, 0).Value()
	a1, _ := plan.OutskirtsArea()
	a2, _ := again.OutskirtsArea()
	if a1 != a2 {
		t.Fatal("not deterministic", a1, a2)
	}
}

func TestMorgueIsNoDemandCoreRoom(t *testing.T) {
	if containsRole(demandCoreRooms, PlannedMorgue) {
		t.Fatal("the morgue is planned in the outskirts, not grown on demand")
	}
}
