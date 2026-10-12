package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestColdShelterHasAPorchTemperateNone(t *testing.T) {
	if p := coldCorePlan(false); len(p.roomsOf(PlannedPorch)) != 0 {
		t.Fatal("temperate shelter has a porch")
	}
	plan := coldCorePlan(true)
	s := shelterOf(t, plan)
	porch := childOf(t, plan, s, PlannedPorch)
	if got := len(porch.PorchSlots()); got != 2 {
		t.Fatalf("porch slots %d", got)
	}
	// The strip lies against the ring on the side opposite the door.
	strip := porch.Region.Bounds()
	ring := Rectangle{X: s.Region.Bounds().X - 1, Z: s.Region.Bounds().Z - 1, Width: s.Region.Bounds().Width + 2, Height: s.Region.Bounds().Height + 2}
	if rectsOverlap(strip, ring) || !rectsOverlap(pad(strip, 1), ring) {
		t.Fatalf("porch %+v not beside ring %+v", strip, ring)
	}
	if rectsOverlap(strip, childOf(t, plan, s, PlannedAirlock).AirlockGround()) {
		t.Fatal("porch overlaps the airlock")
	}
}

func TestPorchHoldsBatteriesUntilTheBatteryRoomIsPlanned(t *testing.T) {
	plan := coldCorePlan(true)
	s := shelterOf(t, plan)
	sites := PlannedPowerSites(plan, BatteryDefinition)
	if len(sites) != 2 || sites[0].Area != childOf(t, plan, s, PlannedPorch).PorchSlots()[0] || sites[0].Cell != (domain.Cell{X: sites[0].Area.X, Z: sites[0].Area.Z}) {
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
	plan, room := shelterWithKids(Rectangle{X: 10, Z: 10, Width: 5, Height: 4}, domain.Cell{X: 40, Z: 11}, false, true)
	porch := childOf(t, plan, room, PlannedPorch)
	if room.DoorRot != domain.East || porch.PorchGround() != (Rectangle{X: 8, Z: 10, Width: 1, Height: 4}) {
		t.Fatalf("room %+v porch %+v", room, porch)
	}
	if again := transposeRoom(transposeRoom(porch)); !again.Same(porch) {
		t.Fatalf("%+v != %+v", again, porch)
	}
	ground := retiredGround(plan, LayoutPlan{}, map[int32]bool{room.ID: true})
	if !slices.ContainsFunc(ground, func(g Region) bool {
		return g.Contains(domain.Cell{X: porch.PorchGround().X, Z: porch.PorchGround().Z})
	}) {
		t.Fatalf("retired ground %v lacks the porch %v", ground, porch.PorchGround())
	}
}
