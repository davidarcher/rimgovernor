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
		return RoomObservation{Shapes: testShapes, EligibleBeds: domain.Known([]string{"bed"}), Rooms: []Room{r}}
	}
	// The sleeper's own comfortable range is the band the refuel switch
	// follows; a room with no sleeper whose range is known owes nothing.
	sleeper := SleepingPerson{ID: "a", OwnedBed: domain.Known("bed"), ComfortableMin: domain.Known(16.0), ComfortableMax: domain.Known(26.0)}
	fire := func(on bool) TemperatureCooling {
		return TemperatureCooling{Sleepers: []SleepingPerson{sleeper}, HeatCampfires: []HeatCampfire{{ID: "Campfire1", Room: domain.Known("room"), AutoRefuel: domain.Known(on)}}}
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
		{"no known sleeper range owes nothing", 30, true, TemperatureCooling{HeatCampfires: []HeatCampfire{{ID: "Campfire1", Room: domain.Known("room"), AutoRefuel: domain.Known(true)}}}, TemperatureNoMethod},
		{"unknown toggle owes nothing", 30, true, TemperatureCooling{Sleepers: []SleepingPerson{sleeper}, HeatCampfires: []HeatCampfire{{ID: "Campfire1", Room: domain.Known("room")}}}, TemperatureNoMethod},
	} {
		got, err := SelectTemperatureMethod(domain.Known(room(tc.temp, tc.campfire)), tc.cooling, DefaultRoutinePolicy(), RoutineLatches{})
		if err != nil || got.Method != tc.want {
			t.Fatalf("%s: %+v %v", tc.name, got, err)
		}
		refuel := tc.want == TemperatureRefuelOff || tc.want == TemperatureRefuelOn
		if refuel && (got.Thing != "Campfire1" || got.Key == "") {
			t.Fatalf("%s: %+v", tc.name, got)
		}
		if _, owed := CampfireRefuel(domain.Known(room(tc.temp, tc.campfire)), tc.cooling); owed != refuel {
			t.Fatalf("%s: owed %v", tc.name, owed)
		}
	}
	off, _ := CampfireRefuel(domain.Known(room(30, true)), fire(true))
	on, _ := CampfireRefuel(domain.Known(room(10, true)), fire(false))
	if off.Key == on.Key {
		t.Fatal("refuel off and on share a method identity")
	}
}

// The heat campfire reads its sleepers' own comfortable range (#1199):
// the intersection of every bed owner's band in the room.
func TestTemperatureMethodSleeperComfortBand(t *testing.T) {
	sleeper := func(id, bed string, min, max float64) SleepingPerson {
		return SleepingPerson{ID: PawnID(id), OwnedBed: domain.Known(bed), ComfortableMin: domain.Known(min), ComfortableMax: domain.Known(max)}
	}
	room := func(temp float64, campfire bool) domain.Fact[RoomObservation] {
		r := thermalRoom("room", "bed", temp, 2)
		r.Beds = []string{"bed", "bed2"}
		if campfire {
			r.Contents = domain.Known([]Amount{{Resource: "Campfire", Count: 1}})
		}
		return domain.Known(RoomObservation{Shapes: testShapes, EligibleBeds: domain.Known([]string{"bed", "bed2"}), Rooms: []Room{r}})
	}
	fire := func(on bool, sleepers ...SleepingPerson) TemperatureCooling {
		return TemperatureCooling{Sleepers: sleepers, HeatCampfires: []HeatCampfire{{ID: "Campfire1", Room: domain.Known("room"), AutoRefuel: domain.Known(on)}}}
	}
	parka := sleeper("a", "bed", 5, 26)
	vanilla := sleeper("b", "bed", 16, 26)
	for _, tc := range []struct {
		name     string
		temp     float64
		campfire bool
		cooling  TemperatureCooling
		want     TemperatureMethod
		owed     bool
	}{
		{"parka sleeper at 10 C needs no heat", 10, false, TemperatureCooling{Sleepers: []SleepingPerson{parka}}, TemperatureNoMethod, false},
		{"parka sleeper keeps the fire out at 10 C", 10, true, fire(false, parka), TemperatureNoMethod, false},
		{"comfort max 20 stops refuelling at 21 C", 21, true, fire(true, sleeper("a", "bed", 16, 20)), TemperatureRefuelOff, true},
		{"default sleepers at 14 C get heat", 14, false, TemperatureCooling{Sleepers: []SleepingPerson{vanilla, sleeper("c", "bed2", 16, 26)}}, TemperatureHeat, true},
		{"default sleepers at 14 C refuel", 14, true, fire(false, vanilla), TemperatureRefuelOn, true},
		{"the band intersects its sleepers", 10, false, TemperatureCooling{Sleepers: []SleepingPerson{parka, sleeper("c", "bed2", 16, 26)}}, TemperatureHeat, true},
		{"another room's sleeper does not band it", 14, false, TemperatureCooling{Sleepers: []SleepingPerson{sleeper("d", "elsewhere", 16, 26)}}, TemperatureNoMethod, false},
	} {
		got, err := SelectTemperatureMethod(room(tc.temp, tc.campfire), tc.cooling, DefaultRoutinePolicy(), RoutineLatches{})
		if err != nil || got.Method != tc.want {
			t.Fatalf("%s: %+v %v", tc.name, got, err)
		}
		if owed := TemperatureOwed(room(tc.temp, tc.campfire), tc.cooling); owed != tc.owed {
			t.Fatalf("%s: owed %v", tc.name, owed)
		}
	}
}
