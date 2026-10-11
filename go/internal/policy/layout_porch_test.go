package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestColdShelterHasAPorchTemperateNone(t *testing.T) {
	if s := shelterOf(t, coldCorePlan(false)); s.Porch != nil {
		t.Fatalf("temperate shelter has a porch: %+v", s.Porch)
	}
	plan := coldCorePlan(true)
	s := shelterOf(t, plan)
	if s.Porch == nil {
		t.Fatal("cold shelter has no porch")
	}
	if got := len(s.PorchSlots()); got != 2 {
		t.Fatalf("porch slots %d", got)
	}
	// The strip lies against the ring on the side opposite the door.
	ring := Rectangle{X: s.Region.Bounds().X - 1, Z: s.Region.Bounds().Z - 1, Width: s.Region.Bounds().Width + 2, Height: s.Region.Bounds().Height + 2}
	if rectsOverlap(s.Porch.Strip, ring) || !rectsOverlap(pad(s.Porch.Strip, 1), ring) {
		t.Fatalf("porch %+v not beside ring %+v", s.Porch.Strip, ring)
	}
	if rectsOverlap(s.Porch.Strip, s.AirlockGround()) {
		t.Fatal("porch overlaps the airlock")
	}
}

func TestPorchHoldsBatteriesUntilTheBatteryRoomIsPlanned(t *testing.T) {
	plan := coldCorePlan(true)
	s := shelterOf(t, plan)
	sites := PlannedPowerSites(plan, BatteryDefinition)
	if len(sites) != 2 || sites[0].Area != s.PorchSlots()[0] || sites[0].Cell != (domain.Cell{X: sites[0].Area.X, Z: sites[0].Area.Z}) {
		t.Fatalf("porch sites %+v", sites)
	}
	for _, c := range RectangleCells(sites[0].Area) {
		if !slices.Contains(PorchRoofCells(plan), c) {
			t.Fatalf("battery cell %v not in the porch roof", c)
		}
	}
	if got := PlannedPowerSites(coldCorePlan(false), BatteryDefinition); len(got) != 0 {
		t.Fatalf("temperate map lists battery sites %+v", got)
	}
	plan.Rooms = append(plan.Rooms, PlannedRoom{Role: PlannedBattery, Region: RectRegion(Rectangle{X: 100, Z: 100, Width: 5, Height: 7}), Door: domain.Cell{X: 102, Z: 99}, DoorRot: domain.South})
	for _, site := range PlannedPowerSites(plan, BatteryDefinition) {
		if slices.Contains(PorchRoofCells(plan), domain.Cell{X: site.Area.X, Z: site.Area.Z}) {
			t.Fatalf("porch site %+v listed beside a battery room", site)
		}
	}
}

func TestPorchSurvivesTransposeAndRetiresWithTheShelter(t *testing.T) {
	room := shelterRoom(Rectangle{X: 10, Z: 10, Width: 5, Height: 4}, domain.Cell{X: 40, Z: 11}, false, true)
	if room.DoorRot != domain.East || room.Porch.Strip != (Rectangle{X: 8, Z: 10, Width: 1, Height: 4}) {
		t.Fatalf("room %+v porch %+v", room, room.Porch)
	}
	if again := transposeRoom(transposeRoom(room)); !again.Same(room) {
		t.Fatalf("%+v != %+v", again, room)
	}
	plan := LayoutPlan{Rooms: []PlannedRoom{room}}
	next, dropped := retireShelter(plan, RoomGrowth{RetireShelter: true})
	if !dropped || !slices.ContainsFunc(RetiredGroundOf(next).Ground, RectRegion(room.PorchGround()).Equal) {
		t.Fatalf("retired ground %v lacks the porch %v", next.RetiredGround, room.PorchGround())
	}
}
