package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A tribal start sleeps on spots and bedrolls, so no standing bed room exists
// when the hot map owes its cooler: the planned shelter keys the proposal,
// and nothing is proven until a roofed enclosed room stands.
func TestTemperatureCoolerKeysOnPlannedShelterWithoutBeds(t *testing.T) {
	cells := []domain.Cell{{X: 5, Z: 6}, {X: 4, Z: 6}, {X: 4, Z: 7}, {X: 5, Z: 7}}
	bedless := domain.Known(RoomObservation{Shapes: testShapes, EligibleBeds: domain.Known([]string{})})
	for _, tc := range []struct {
		name     string
		shelters []ThermalShelter
		want     TemperatureMethod
	}{
		{"hot planned shelter", []ThermalShelter{{Cells: cells, Hot: true}}, TemperatureCool},
		{"hot shelter with cooler standing", []ThermalShelter{{Cells: cells, Hot: true, Standing: []string{"PassiveCooler"}}}, TemperatureWait},
		{"mild planned shelter", []ThermalShelter{{Cells: cells}}, TemperatureShelterNeeded},
		{"no planned shelter", nil, TemperatureShelterNeeded},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, err := SelectTemperatureMethod(bedless, TemperatureCooling{PlannedShelters: tc.shelters}, DefaultRoundsPolicy(), RoundsLatches{})
			if err != nil || got.Method != tc.want {
				t.Fatal(got, err)
			}
			if tc.want == TemperatureCool {
				if len(got.Cells) != len(cells) || got.Cells[0] != (domain.Cell{X: 4, Z: 6}) || got.Key == "" {
					t.Fatal("proposal does not cover the planned interior", got)
				}
			}
			if low, _ := TemperatureRange(bedless); lowKnown(low) {
				t.Fatal("a planned room proved a temperature")
			}
		})
	}
}

// Once beds stand, the census room takes over: the cooler is still proposed
// from the room, and the proof waits for enclosure and a full roof.
func TestTemperatureProofStaysWithRoofedEnclosedRoomAfterPlannedShelter(t *testing.T) {
	room := thermalRoom("room", "bed", 36, 2)
	room.Roofed = domain.Known(false)
	fact := domain.Known(RoomObservation{Shapes: testShapes, EligibleBeds: domain.Known([]string{"bed"}), Rooms: []Room{room}})
	cooling := TemperatureCooling{PlannedShelters: []ThermalShelter{{Cells: room.Cells, Hot: true}}}
	got, err := SelectTemperatureMethod(fact, cooling, DefaultRoundsPolicy(), RoundsLatches{})
	if err != nil || got.Method != TemperatureCool || got.Room != "room" {
		t.Fatal(got, err)
	}
	if low, _ := TemperatureRange(fact); lowKnown(low) {
		t.Fatal("proof granted on an open-roofed room")
	}
	room.Roofed = domain.Known(true)
	fact = domain.Known(RoomObservation{Shapes: testShapes, EligibleBeds: domain.Known([]string{"bed"}), Rooms: []Room{room}})
	if low, _ := TemperatureRange(fact); !lowKnown(low) {
		t.Fatal("proof withheld on a roofed enclosed room")
	}
}

func lowKnown(f domain.Fact[float64]) bool { _, ok := f.Value(); return ok }
