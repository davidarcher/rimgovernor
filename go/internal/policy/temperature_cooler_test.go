package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// hotRoomSite is a 3x3 enclosed sleeping room at (1..3, 1..3) ringed by
// walls at 0 and 4 with open ground beyond, so the west wall cell (0,1) is
// the lowest-sorted vented wall.
func hotRoomSite(temperature float64) (RoomObservation, []SiteCell) {
	room := Room{ID: "room", Beds: []string{"bed"}, Temperature: domain.Known(temperature), Enclosed: domain.Known(true), Contents: domain.Known([]Amount{})}
	var cells []SiteCell
	for x := int32(-1); x <= 5; x++ {
		for z := int32(-1); z <= 5; z++ {
			c := domain.Cell{X: x, Z: z}
			switch {
			case x >= 1 && x <= 3 && z >= 1 && z <= 3:
				room.Cells = append(room.Cells, c)
				cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Indoors: domain.Known(true), Roofed: domain.Known(true)})
			case x >= 0 && x <= 4 && z >= 0 && z <= 4:
				cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(false), Occupied: domain.Known(true), Indoors: domain.Known(false), Roofed: domain.Known(true)})
			default:
				cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Indoors: domain.Known(false), Roofed: domain.Known(false)})
			}
		}
	}
	return RoomObservation{EligibleBeds: domain.Known([]string{"bed"}), Rooms: []Room{room}}, cells
}

func coolingReady(cells []SiteCell, spare float64) TemperatureCooling {
	topology := PowerTopology{Buildings: []PowerSite{
		{ID: "gen", Definition: "WoodFiredGenerator", Cell: domain.Cell{X: 9, Z: 9}, PowerBuilding: PowerBuilding{BaseW: domain.Known(1000.0), Connected: domain.Known(true), Network: domain.Known("net")}},
		{ID: "lamp", Definition: "StandingLamp", Cell: domain.Cell{X: 9, Z: 8}, PowerBuilding: PowerBuilding{BaseW: domain.Known(spare - 1000), Connected: domain.Known(true), Network: domain.Known("net")}},
	}}
	return TemperatureCooling{CoolerAvailable: domain.Known(true), CoolerDrawW: domain.Known(200.0), Power: domain.Known(topology), Cells: cells}
}

func TestTemperaturePoweredCoolerThroughVentedWall(t *testing.T) {
	v, cells := hotRoomSite(36)
	got, err := SelectTemperatureMethod(domain.Known(v), coolingReady(cells, 500), DefaultRoutinePolicy(), RoutineLatches{})
	if err != nil || got.Method != TemperatureCoolPowered || got.Room != "room" {
		t.Fatal(got, err)
	}
	if got.Cell != (domain.Cell{X: 0, Z: 1}) || got.Rotation != domain.West {
		t.Fatalf("cooler not on the lowest vented wall facing out: %+v", got)
	}
	if len(got.Cells) != 9 {
		t.Fatal("room cells not carried", got.Cells)
	}
	passive, err := SelectTemperatureMethod(domain.Known(v), TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{})
	if err != nil || passive.Method != TemperatureCool || passive.Key == got.Key {
		t.Fatal("powered and passive methods must be distinct methods", passive, err)
	}
}

func TestTemperaturePoweredCoolerFallsBackToPassive(t *testing.T) {
	v, cells := hotRoomSite(36)
	for name, cooling := range map[string]TemperatureCooling{
		"research": func() TemperatureCooling {
			c := coolingReady(cells, 500)
			c.CoolerAvailable = domain.Known(false)
			return c
		}(),
		"unknown": func() TemperatureCooling {
			c := coolingReady(cells, 500)
			c.CoolerAvailable = domain.Unknown[bool]()
			return c
		}(),
		"no-spare": coolingReady(cells, 150),
		"no-power": func() TemperatureCooling {
			c := coolingReady(cells, 500)
			c.Power = domain.Unknown[PowerTopology]()
			return c
		}(),
		"draw-unknown": func() TemperatureCooling {
			c := coolingReady(cells, 500)
			c.CoolerDrawW = domain.Unknown[float64]()
			return c
		}(),
		"blackout": func() TemperatureCooling {
			c := coolingReady(cells, 500)
			topology, _ := c.Power.Value()
			topology.Blackout = domain.Known(true)
			c.Power = domain.Known(topology)
			return c
		}(),
		"no-wall": coolingReady(nil, 500),
	} {
		got, err := SelectTemperatureMethod(domain.Known(v), cooling, DefaultRoutinePolicy(), RoutineLatches{})
		if err != nil || got.Method != TemperatureCool {
			t.Fatalf("%s: %+v %v", name, got, err)
		}
	}
	// A cold room never gets a powered cooler.
	cold, _ := hotRoomSite(5)
	got, err := SelectTemperatureMethod(domain.Known(cold), coolingReady(cells, 500), DefaultRoutinePolicy(), RoutineLatches{})
	if err != nil || got.Method != TemperatureHeat {
		t.Fatal(got, err)
	}
}

func TestTemperatureWallCoolerCountsAsExisting(t *testing.T) {
	v, cells := hotRoomSite(36)
	cooling := coolingReady(cells, 500)
	topology, _ := cooling.Power.Value()
	topology.Buildings = append(topology.Buildings, PowerSite{ID: "cooler", Definition: "Cooler", Cell: domain.Cell{X: 0, Z: 1}, PowerBuilding: PowerBuilding{BaseW: domain.Known(-200.0), Connected: domain.Known(true), Network: domain.Known("net")}})
	cooling.Power = domain.Known(topology)
	got, err := SelectTemperatureMethod(domain.Known(v), cooling, DefaultRoutinePolicy(), RoutineLatches{})
	if err != nil || got.Method != TemperatureWait {
		t.Fatal("a cooler through the wall should hold the room as cooling", got, err)
	}
	// A cooler elsewhere on the map does not serve this room.
	topology.Buildings[len(topology.Buildings)-1].Cell = domain.Cell{X: 20, Z: 20}
	cooling.Power = domain.Known(topology)
	got, err = SelectTemperatureMethod(domain.Known(v), cooling, DefaultRoutinePolicy(), RoutineLatches{})
	if err != nil || got.Method != TemperatureCoolPowered {
		t.Fatal(got, err)
	}
}

func TestTemperatureHottestRoomFirst(t *testing.T) {
	v := RoomObservation{EligibleBeds: domain.Known([]string{"a", "b"}), Rooms: []Room{thermalRoom("warm", "a", 33, 1), thermalRoom("hot", "b", 40, 2)}}
	got, err := SelectTemperatureMethod(domain.Known(v), TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{})
	if err != nil || got.Room != "hot" {
		t.Fatal(got, err)
	}
	v = RoomObservation{EligibleBeds: domain.Known([]string{"a", "b"}), Rooms: []Room{thermalRoom("cool", "a", 10, 1), thermalRoom("cold", "b", -5, 2)}}
	got, err = SelectTemperatureMethod(domain.Known(v), TemperatureCooling{}, DefaultRoutinePolicy(), RoutineLatches{})
	if err != nil || got.Room != "cold" {
		t.Fatal(got, err)
	}
}

func TestPowerTopologySpareW(t *testing.T) {
	known := func(w float64, net string, connected bool) PowerSite {
		return PowerSite{PowerBuilding: PowerBuilding{BaseW: domain.Known(w), Connected: domain.Known(connected), Network: domain.Known(net)}}
	}
	spare, ok := (PowerTopology{Buildings: []PowerSite{known(1000, "a", true), known(-300, "a", true), known(-900, "a", false), known(-50, "b", true)}}).SpareW().Value()
	if !ok || spare != 700 {
		t.Fatal(spare, ok)
	}
	if _, ok = (PowerTopology{Buildings: []PowerSite{known(-50, "b", true)}}).SpareW().Value(); ok {
		t.Fatal("a network without a producer has no spare")
	}
	if _, ok = (PowerTopology{}).SpareW().Value(); ok {
		t.Fatal("no buildings, no spare")
	}
	unknown := PowerSite{PowerBuilding: PowerBuilding{BaseW: domain.Unknown[float64](), Connected: domain.Known(true), Network: domain.Known("a")}}
	if _, ok = (PowerTopology{Buildings: []PowerSite{known(1000, "a", true), unknown}}).SpareW().Value(); ok {
		t.Fatal("an unknown draw blanks the surplus")
	}
}
