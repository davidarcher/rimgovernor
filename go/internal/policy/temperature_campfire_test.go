package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A heat campfire in a sleeping room (#1180): a cold room gets a campfire,
// the warm room switches its refuel off instead of proposing a cooler,
// and a cold room again switches it back on.
func TestTemperatureMethodHeatCampfireRefuel(t *testing.T) {
	room := func(temp float64, campfire bool) RoomObservation {
		r := thermalRoom("room", "bed", temp, 2)
		if campfire {
			r.Contents = domain.Known([]Amount{{Resource: "Campfire", Count: 1}})
		}
		return RoomObservation{EligibleBeds: domain.Known([]string{"bed"}), Rooms: []Room{r}}
	}
	fire := func(on bool) TemperatureCooling {
		return TemperatureCooling{HeatCampfires: []HeatCampfire{{ID: "Campfire1", Room: domain.Known("room"), AutoRefuel: domain.Known(on)}}}
	}
	for _, tc := range []struct {
		name     string
		temp     float64
		campfire bool
		cooling  TemperatureCooling
		want     TemperatureMethod
	}{
		{"cold room heats by campfire", 5, false, TemperatureCooling{}, TemperatureHeat},
		{"warm room stops refuelling", 26, true, fire(true), TemperatureRefuelOff},
		{"hot room stops refuelling, no cooler", 35, true, fire(true), TemperatureRefuelOff},
		{"hot room burning out waits", 35, true, fire(false), TemperatureWait},
		{"comfortable room owes nothing", 20, true, fire(false), TemperatureNoMethod},
		{"cold again refuels", 15, true, fire(false), TemperatureRefuelOn},
		{"cold room with refuel on waits", 5, true, fire(true), TemperatureWait},
		{"unknown toggle owes nothing", 30, true, TemperatureCooling{HeatCampfires: []HeatCampfire{{ID: "Campfire1", Room: domain.Known("room")}}}, TemperatureNoMethod},
	} {
		got, err := SelectTemperatureMethod(domain.Known(room(tc.temp, tc.campfire)), tc.cooling, DefaultRoutinePolicy(), RoutineLatches{})
		if err != nil || got.Method != tc.want {
			t.Fatalf("%s: %+v %v", tc.name, got, err)
		}
		refuel := tc.want == TemperatureRefuelOff || tc.want == TemperatureRefuelOn
		if refuel && (got.Thing != "Campfire1" || got.Key == "") {
			t.Fatalf("%s: %+v", tc.name, got)
		}
		if _, owed := CampfireRefuel(domain.Known(room(tc.temp, tc.campfire)), tc.cooling.HeatCampfires); owed != refuel {
			t.Fatalf("%s: owed %v", tc.name, owed)
		}
	}
	off, _ := CampfireRefuel(domain.Known(room(30, true)), fire(true).HeatCampfires)
	on, _ := CampfireRefuel(domain.Known(room(10, true)), fire(false).HeatCampfires)
	if off.Key == on.Key {
		t.Fatal("refuel off and on share a method identity")
	}
}
