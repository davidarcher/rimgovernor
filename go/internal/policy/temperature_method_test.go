package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func thermalRoom(id, bed string, temp float64, x int32) Room {
	return Room{ID: id, Beds: []string{bed}, Temperature: domain.Known(temp), Enclosed: domain.Known(true), Contents: domain.Known([]Amount{}), Cells: []domain.Cell{{X: x, Z: 2}}}
}

func TestTemperatureMethodThresholdsAndHysteresis(t *testing.T) {
	for _, tc := range []struct {
		temp      float64
		cold, hot bool
		want      TemperatureMethod
	}{
		{11.9, false, false, TemperatureHeat}, {12, false, false, TemperatureNoMethod}, {15.9, true, false, TemperatureHeat}, {16, true, false, TemperatureNoMethod},
		{32, false, false, TemperatureNoMethod}, {32.1, false, false, TemperatureCool}, {28.1, false, true, TemperatureCool}, {28, false, true, TemperatureNoMethod},
	} {
		v := RoomObservation{EligibleBeds: domain.Known([]string{"bed"}), Rooms: []Room{thermalRoom("room", "bed", tc.temp, 2)}}
		got, err := SelectTemperatureMethod(domain.Known(v), TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{Cold: tc.cold, Hot: tc.hot})
		if err != nil || got.Method != tc.want {
			t.Fatalf("%+v: %+v %v", tc, got, err)
		}
	}
}

func TestTemperatureMethodTargetsPlayerSleepingRoomAndReusesFacilities(t *testing.T) {
	v := RoomObservation{EligibleBeds: domain.Known([]string{"hot-bed", "cold-bed"}), Rooms: []Room{thermalRoom("hot", "hot-bed", 36, 1), thermalRoom("cold", "cold-bed", 5, 2), thermalRoom("enemy", "enemy-bed", -30, 3)}}
	first, err := SelectTemperatureMethod(domain.Known(v), TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{})
	if err != nil || first.Method != TemperatureHeat || first.Room != "cold" || len(first.Cells) != 1 || first.Cells[0].X != 2 {
		t.Fatal(first, err)
	}
	v.Rooms[1].ID = "regenerated-native-room"
	next, err := SelectTemperatureMethod(domain.Known(v), TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{})
	if err != nil || next.Key != first.Key {
		t.Fatal("room rebuild changed durable method identity", next, err)
	}
	first.Cells[0].X = 99
	if v.Rooms[1].Cells[0].X != 2 {
		t.Fatal("proposal aliases observation")
	}
	for _, def := range []Resource{"Campfire", "Heater"} {
		v.Rooms[1].Contents = domain.Known([]Amount{{Resource: def, Count: 1}})
		got, err := SelectTemperatureMethod(domain.Known(v), TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{})
		if err != nil || got.Method != TemperatureCool || got.Room != "hot" {
			t.Fatal(got, err)
		}
	}
	for _, def := range []Resource{"PassiveCooler", "Cooler"} {
		v.Rooms[0].Contents = domain.Known([]Amount{{Resource: def, Count: 1}})
		got, err := SelectTemperatureMethod(domain.Known(v), TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{})
		if err != nil || got.Method != TemperatureWait {
			t.Fatal(got, err)
		}
		low, high := TemperatureRange(domain.Known(v))
		l, _ := low.Value()
		h, _ := high.Value()
		if l != 5 || h != 36 {
			t.Fatal("facility counted as temperature recovery", l, h)
		}
	}
	v.Rooms[0].Temperature, v.Rooms[1].Temperature = domain.Known(24.0), domain.Known(18.0)
	got, err := SelectTemperatureMethod(domain.Known(v), TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{Cold: true, Hot: true})
	if err != nil || got.Method != TemperatureNoMethod {
		t.Fatal(got, err)
	}
}

func TestTemperatureUnknownAndInvalidEvidence(t *testing.T) {
	for _, mode := range []string{"unknown", "beds", "missing-room", "temperature", "enclosure", "contents", "no-beds", "unroofed", "duplicate-room", "duplicate-bed", "overlap", "nan"} {
		t.Run(mode, func(t *testing.T) {
			v := RoomObservation{EligibleBeds: domain.Known([]string{"bed"}), Rooms: []Room{thermalRoom("room", "bed", 5, 2)}}
			invalid := false
			want := TemperatureUnknown
			switch mode {
			case "beds":
				v.EligibleBeds = domain.Unknown[[]string]()
			case "missing-room":
				v.Rooms = nil
			case "temperature":
				v.Rooms[0].Temperature = domain.Unknown[float64]()
			case "enclosure":
				v.Rooms[0].Enclosed = domain.Unknown[bool]()
			case "contents":
				v.Rooms[0].Contents = domain.Unknown[[]Amount]()
			case "no-beds":
				v.EligibleBeds = domain.Known([]string{})
				want = TemperatureShelterNeeded
			case "unroofed":
				v.Rooms[0].Enclosed = domain.Known(false)
				want = TemperatureShelterNeeded
			case "duplicate-room":
				v.Rooms = append(v.Rooms, thermalRoom("room", "other", 5, 3))
				invalid = true
			case "duplicate-bed":
				v.Rooms = append(v.Rooms, thermalRoom("other", "bed", 5, 3))
				invalid = true
			case "overlap":
				v.Rooms = append(v.Rooms, thermalRoom("other", "other", 5, 2))
				invalid = true
			case "nan":
				v.Rooms[0].Temperature = domain.Known(math.NaN())
				invalid = true
			}
			fact := domain.Known(v)
			if mode == "unknown" {
				fact = domain.Unknown[RoomObservation]()
			}
			got, err := SelectTemperatureMethod(fact, TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{})
			if invalid {
				if err == nil {
					t.Fatal("accepted invalid evidence")
				}
				return
			}
			if err != nil || got.Method != want {
				t.Fatal(got, err)
			}
			if mode != "contents" {
				low, high := TemperatureRange(fact)
				if _, ok := low.Value(); ok {
					t.Fatal(low)
				}
				if _, ok := high.Value(); ok {
					t.Fatal(high)
				}
			}
		})
	}
}
