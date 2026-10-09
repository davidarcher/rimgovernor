package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The barn plan carries the climate slot: one heater on the free
// floor, never counted as a bed, with every bed left a free neighbour.
func TestBarnPlanIncludesTheClimateHeater(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barn := plan.HerdRooms(PlannedBarn)[0]
	in := mustInterior(t, barn)
	laid, ok := PlanInterior(in, testHerdFurniture.Spot)
	if !ok {
		t.Fatal("no barn plan")
	}
	var heaters []InteriorPiece
	for _, p := range laid.Pieces {
		if p.Slot == herdHeaterSlot {
			heaters = append(heaters, p)
		}
	}
	if len(heaters) != 1 || heaters[0].Def != "Heater" {
		t.Fatal("one heater in the climate slot", heaters)
	}
	if beds := herdBedPieces(laid.Pieces); len(beds) != len(laid.Pieces)-1 || herdRoomBeds(barn, testShapes, testHerdFurniture.Spot) != len(beds) {
		t.Fatal("the heater is no bed", len(beds), len(laid.Pieces))
	}
	// The vet room has no climate slot, and a catalog without a heater shape
	// plans a barn without one.
	vet, ok := PlanInterior(mustInterior(t, plan.HerdRooms(PlannedVetRoom)[0]), testHerdFurniture.Bed)
	if !ok || len(herdBedPieces(vet.Pieces)) != len(vet.Pieces) {
		t.Fatal("vet room plans beds only")
	}
	bare := in
	bare.Shapes.Furniture.Heater = ""
	if bareLaid, ok := PlanInterior(bare, testHerdFurniture.Spot); !ok || len(bareLaid.Pieces) != len(laid.Pieces)-1 {
		t.Fatal("no heater in the catalog, no climate slot")
	}
}

// The heater is part of the barn's template: wanted in its climate slot beside
// the beds, never twice, and the beds never wait for a heater that is not
// buildable yet.
func TestHerdStepWantsTheBarnHeaterInItsSlot(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barn := plan.HerdRooms(PlannedBarn)[0]
	const animals = 12
	furniture := testHerdFurniture
	furniture.Heater = InteriorPieceDef{Def: "Heater", Size: domain.Cell{X: 1, Z: 1}}
	rooms := standing(barn)
	rooms.Shapes = testShapes
	ground := herdGround(plan, barn)
	step := NextHerdStep(plan, rooms, ground, nil, nil, animals, furniture)
	if step.Kind != HerdReconcile || herdTemplateCount(step, testAnimalSpot) != animals || herdTemplateCount(step, "Heater") != 1 {
		t.Fatal("beds and heater wanted", step)
	}
	var heater WantedPiece
	var built []CurrentBuilding
	for _, p := range step.Template {
		piece := InteriorPiece{Slot: p.Slot, Def: p.DefName, Size: p.Size, Rot: p.Rot, Rect: Rectangle{X: p.Minimum.X, Z: p.Minimum.Z, Width: 1, Height: 1}}
		if p.DefName == "Heater" {
			heater = p
			continue
		}
		built = append(built, bedAt(t, p.DefName, piece))
	}
	if heater.Slot != herdHeaterSlot || !rectInside(barn.Interior, Rectangle{X: heater.Minimum.X, Z: heater.Minimum.Z, Width: 1, Height: 1}) {
		t.Fatal("heater in the climate slot", heater)
	}
	// The beds stand: only the heater is owed.
	step = NextHerdStep(plan, rooms, ground, built, nil, animals, furniture)
	if step.Kind != HerdReconcile || herdTemplateCount(step, "Heater") != 1 {
		t.Fatal("heater after the beds", step)
	}
	built = append(built, bedAt(t, "Heater", InteriorPiece{Slot: heater.Slot, Def: "Heater", Size: heater.Size, Rot: heater.Rot, Rect: Rectangle{X: heater.Minimum.X, Z: heater.Minimum.Z, Width: 1, Height: 1}}))
	if step = NextHerdStep(plan, rooms, ground, built, nil, animals, furniture); step.Kind == HerdReconcile && step.Role == PlannedBarn {
		t.Fatal("one heater per barn", step)
	}
	if step = NextHerdStep(plan, rooms, ground, built[:animals], nil, animals, testHerdFurniture); step.Kind == HerdReconcile && step.Role == PlannedBarn {
		t.Fatal("no heater wanted while the heater is not buildable", step)
	}
}

// A barn holding a powered heater is a conditioned room; an unpowered
// one, another room's heater, or no heater in the catalog is not.
func TestTemperaturePlannerTreatsAPoweredHeaterRoomAsConditioned(t *testing.T) {
	room := Room{ID: "barn", Cells: []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}}
	cooling := func(powered bool, cell domain.Cell, heater string) TemperatureCooling {
		site := PowerSite{ID: "h", Definition: "Heater", Cell: cell, PowerBuilding: PowerBuilding{Powered: domain.Known(powered)}}
		return TemperatureCooling{Heater: heater, Power: domain.Known(PowerTopology{Buildings: []PowerSite{site}})}
	}
	if !cooling(true, domain.Cell{X: 2, Z: 1}, "Heater").Conditioned(room) {
		t.Fatal("a powered heater in the room conditions it")
	}
	for name, c := range map[string]TemperatureCooling{
		"unpowered":      cooling(false, domain.Cell{X: 2, Z: 1}, "Heater"),
		"other room":     cooling(true, domain.Cell{X: 9, Z: 9}, "Heater"),
		"no catalog def": cooling(true, domain.Cell{X: 2, Z: 1}, ""),
		"unknown power":  {Heater: "Heater"},
	} {
		if c.Conditioned(room) {
			t.Fatal("not conditioned:", name)
		}
	}
}
