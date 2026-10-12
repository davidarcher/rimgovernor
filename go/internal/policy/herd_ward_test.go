package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func wardHospital() PlannedRoom {
	return PlannedRoom{Role: PlannedHospital, Region: RectRegion(Rectangle{X: 10, Z: 10, Width: 7, Height: 5}), Door: domain.Cell{X: 13, Z: 15}, DoorRot: domain.South}
}

func wardPlan() LayoutPlan { return LayoutPlan{Rooms: []PlannedRoom{wardHospital()}} }

func hurtAnimal(id string, tended bool) UpkeepAnimal {
	a := planAnimal(id, "Cow", "Female")
	a.Conditions = domain.Known([]CareCondition{{Tended: domain.Known(tended)}})
	a.LifeThreatening = domain.Known(false)
	return a
}

func TestHospitalCellsAreTheHospitalInterior(t *testing.T) {
	cells := wardPlan().HospitalCells()
	if len(cells) != 35 {
		t.Fatal("the hospital interior", len(cells))
	}
	if len((LayoutPlan{}).HospitalCells()) != 0 {
		t.Fatal("no hospital, no cells")
	}
}

func TestWardBedsFollowTheInjuredAnimals(t *testing.T) {
	herd := HerdPolicy{}
	if WardBeds(nil, herd) != 0 {
		t.Fatal("a healthy herd owes no bed")
	}
	rows := []UpkeepAnimal{hurtAnimal("a", false), hurtAnimal("b", true), hurtAnimal("c", false)}
	if got := WardBeds(rows, herd); got != 2 {
		t.Fatal("one bed per untended animal", got)
	}
	threat := planAnimal("d", "Cow", "Male")
	threat.LifeThreatening = domain.Known(true)
	if got := WardBeds([]UpkeepAnimal{threat}, herd); got != 1 {
		t.Fatal("a life threat owes a bed", got)
	}
	unread := planAnimal("e", "Cow", "Male")
	if got := WardBeds([]UpkeepAnimal{unread}, herd); got != 0 {
		t.Fatal("unread care facts owe nothing", got)
	}
	herd = PlanHerd(milkInput(goatsAwaitingCows(), wildOf("Cow"))).Policy
	if got := WardBeds(goatsAwaitingCows(), herd); got != 1 {
		t.Fatal("a wanted sterilize owes one bed", got)
	}
}

func TestWardSlotsStayOutsideTheHumanTemplateAndKeepTheDoorOpen(t *testing.T) {
	room := wardHospital()
	bed := testHerdFurniture.Bed
	slots := WardSlots(room, testShapes, bed)
	if len(slots) == 0 {
		t.Fatal("the hospital has no free floor for a ward bed")
	}
	in, ok := InteriorRoomFromLayout(room, testShapes)
	if !ok {
		t.Fatal("no interior")
	}
	human, ok := PlanInterior(in, InteriorPieceDef{})
	if !ok {
		t.Fatal("no hospital template")
	}
	used := map[domain.Cell]bool{}
	for _, p := range human.Pieces {
		for _, c := range rectCells(p.Rect) {
			used[c] = true
		}
	}
	for _, s := range slots {
		for _, c := range rectCells(s.Rect) {
			if used[c] {
				t.Fatal("a ward bed overlaps another piece", s.Rect)
			}
			used[c] = true
		}
		if !rectInside(room.Region.Bounds(), s.Rect) {
			t.Fatal("a ward bed outside the hospital", s.Rect)
		}
	}
	if WardSlots(PlannedRoom{Role: PlannedBarn, Region: room.Region, Door: room.Door}, testShapes, bed) != nil {
		t.Fatal("only a hospital has a ward")
	}
	if WardSlots(room, testShapes, InteriorPieceDef{}) != nil {
		t.Fatal("an unread bed shape has no slots")
	}
}

func TestHerdStepFurnishesTheWardOnceTheHospitalStands(t *testing.T) {
	plan := wardPlan()
	room := wardHospital()
	ground := herdGround(plan, room)
	// The hospital does not stand: nothing is owed.
	if step := NextHerdStep(plan, RoomObservation{Shapes: testShapes}, ground, nil, nil, 0, 2, testHerdFurniture); step.Kind != HerdNone {
		t.Fatal("a hospital that does not stand is not furnished by the herd", step)
	}
	rooms := standing(room)
	rooms.Shapes = testShapes
	if step := NextHerdStep(plan, rooms, ground, nil, nil, 0, 0, testHerdFurniture); step.Kind != HerdNone {
		t.Fatal("no injured animal owes no ward bed", step)
	}
	step := NextHerdStep(plan, rooms, ground, nil, nil, 0, 2, testHerdFurniture)
	if step.Kind != HerdReconcile || step.Role != PlannedHospital || herdTemplateCount(step, testAnimalBed) != 2 {
		t.Fatal("two ward beds wanted", step)
	}
	var built []CurrentBuilding
	for _, p := range step.Template {
		built = append(built, bedAt(t, p.DefName, InteriorPiece{Slot: p.Slot, Def: p.DefName, Size: p.Size, Rot: p.Rot, Rect: Rectangle{X: p.Minimum.X, Z: p.Minimum.Z, Width: 1, Height: 1}}))
	}
	census := func(medical bool) []SleepingBed {
		var out []SleepingBed
		for _, b := range built {
			out = append(out, SleepingBed{ID: b.ID, Medical: domain.Known(medical)})
		}
		return out
	}
	step = NextHerdStep(plan, rooms, ground, built, census(false), 0, 2, testHerdFurniture)
	if step.Kind != HerdMedical || step.Role != PlannedHospital {
		t.Fatal("a standing ward bed is flagged medical", step)
	}
	if step = NextHerdStep(plan, rooms, ground, built, census(true), 0, 2, testHerdFurniture); step.Kind != HerdNone {
		t.Fatal("a flagged ward owes nothing", step)
	}
	ready := WardReady(plan, rooms, built, census(true), testAnimalBed)
	if v, ok := ready.Value(); !ok || !v {
		t.Fatal("a flagged bed in a standing hospital is a ready ward", ready)
	}
	if v, ok := WardReady(plan, rooms, built, census(false), testAnimalBed).Value(); !ok || v {
		t.Fatal("an unflagged bed is not ready")
	}
	if v, ok := WardReady(plan, RoomObservation{}, built, census(true), testAnimalBed).Value(); !ok || v {
		t.Fatal("a hospital that does not stand is not ready")
	}
}
