package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestTemperaturePlacementIsUngatedAndProofNeedsARoofedEnclosedRoom(t *testing.T) {
	for _, tc := range []struct {
		name             string
		enclosed, roofed domain.Fact[bool]
		temp             float64
		want             TemperatureMethod
		proven           bool
	}{
		{"no walls cold", domain.Known(false), domain.Known(false), 5, TemperatureHeat, false},
		{"unknown enclosure cold", domain.Unknown[bool](), domain.Unknown[bool](), 5, TemperatureHeat, false},
		{"walled open roof cold", domain.Known(true), domain.Known(false), 5, TemperatureHeat, false},
		{"walled open roof mild", domain.Known(true), domain.Known(false), 20, TemperatureWait, false},
		{"roofless unenclosed mild", domain.Known(false), domain.Known(true), 20, TemperatureWait, false},
		{"unknown roof mild", domain.Known(true), domain.Unknown[bool](), 20, TemperatureWait, false},
		{"walled roofed mild", domain.Known(true), domain.Known(true), 20, TemperatureNoMethod, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			room := thermalRoom("room", "bed", tc.temp, 2)
			room.Enclosed, room.Roofed = tc.enclosed, tc.roofed
			fact := domain.Known(RoomObservation{Shapes: testShapes, EligibleBeds: domain.Known([]string{"bed"}), Rooms: []Room{room}})
			got, err := SelectTemperatureMethod(fact, TemperatureCooling{}, DefaultRoundsPolicy(), RoundsLatches{})
			if err != nil || got.Method != tc.want {
				t.Fatal(got, err)
			}
			low, _ := TemperatureRange(fact)
			if _, ok := low.Value(); ok != tc.proven {
				t.Fatalf("proof granted = %v, want %v", ok, tc.proven)
			}
		})
	}
}
