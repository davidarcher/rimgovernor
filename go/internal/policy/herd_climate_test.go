package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The barn plan carries the climate slot (#1867): one heater on the free
// floor, never counted as a bed, with every bed left a free neighbour.
func TestBarnPlanIncludesTheClimateHeater(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barn := plan.HerdRooms(ModuleBarn)[0]
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
	vet, ok := PlanInterior(mustInterior(t, plan.HerdRooms(ModuleVetRoom)[0]), testHerdFurniture.Bed)
	if !ok || len(herdBedPieces(vet.Pieces)) != len(vet.Pieces) {
		t.Fatal("vet room plans beds only")
	}
	bare := in
	bare.Shapes.Furniture.Heater = ""
	if bareLaid, ok := PlanInterior(bare, testHerdFurniture.Spot); !ok || len(bareLaid.Pieces) != len(laid.Pieces)-1 {
		t.Fatal("no heater in the catalog, no climate slot")
	}
}

// The heater is the barn's last step: placed once the beds stand, never
// twice, and the beds never wait for a heater that is not buildable yet.
func TestHerdStepPlacesTheBarnHeaterAfterTheBeds(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barn := plan.HerdRooms(ModuleBarn)[0]
	const animals = 12
	furniture := testHerdFurniture
	furniture.Heater = InteriorPieceDef{Def: "Heater", Size: domain.Cell{X: 1, Z: 1}}
	rooms := standing(barn)
	rooms.Shapes = testShapes
	var built []CurrentBuilding
	for i := 0; i < animals; i++ {
		step := NextHerdStep(plan, rooms, built, nil, animals, furniture)
		if step.Kind != HerdPlace || step.Piece.Def != testAnimalSpot {
			t.Fatal("beds before the heater", i, step)
		}
		built = append(built, bedAt(t, step.Piece.Def, step.Piece))
	}
	step := NextHerdStep(plan, rooms, built, nil, animals, furniture)
	if step.Kind != HerdPlace || step.Role != ModuleBarn || step.Piece.Def != "Heater" || step.Piece.Slot != herdHeaterSlot || !rectInside(barn.Interior, step.Piece.Rect) {
		t.Fatal("heater after the beds", step)
	}
	built = append(built, bedAt(t, "Heater", step.Piece))
	if step = NextHerdStep(plan, rooms, built, nil, animals, furniture); step.Kind == HerdPlace && step.Role == ModuleBarn {
		t.Fatal("one heater per barn", step)
	}
	if step = NextHerdStep(plan, rooms, built[:animals], nil, animals, testHerdFurniture); step.Kind == HerdPlace && step.Role == ModuleBarn {
		t.Fatal("no heater step while the heater is not buildable", step)
	}
}

// A barn holding a powered heater is a conditioned room (#1867); an unpowered
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
