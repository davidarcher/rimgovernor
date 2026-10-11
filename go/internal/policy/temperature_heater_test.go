package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// heatingReady is a cold-map cooling reading: one 1,000 W net with spare
// watts, a placeable 175 W heater and a finite wood stock.
func heatingReady(spare float64) TemperatureCooling {
	c := coolingReady(nil, spare)
	c.Heater, c.HeaterAvailable, c.HeaterDrawW, c.HeatFuelFinite = "Heater", domain.Known(true), domain.Known(175.0), true
	return c
}

func frozenRoom(contents ...Amount) RoomObservation {
	room := thermalRoom("room", "bed", 2, 1)
	room.Contents = domain.Known(contents)
	return RoomObservation{Shapes: testShapes, EligibleBeds: domain.Known([]string{"bed"}), Rooms: []Room{room}}
}

func TestTemperatureElectricHeaterWhenWoodIsFinite(t *testing.T) {
	got, err := SelectTemperatureMethod(domain.Known(frozenRoom()), heatingReady(500), DefaultRoundsPolicy(), RoundsLatches{})
	if err != nil || got.Method != TemperatureHeatElectric || got.Room != "room" || len(got.Cells) != 1 {
		t.Fatal(got, err)
	}
	campfire, err := SelectTemperatureMethod(domain.Known(frozenRoom()), TemperatureCooling{}, DefaultRoundsPolicy(), RoundsLatches{})
	if err != nil || campfire.Method != TemperatureHeat || campfire.Key == got.Key {
		t.Fatal("campfire and heater must be distinct methods", campfire, err)
	}
	// A standing campfire does not hold a room the heater now serves, but a
	// standing heater (powered or not) does.
	got, err = SelectTemperatureMethod(domain.Known(frozenRoom(Amount{Resource: "Campfire", Count: 2})), heatingReady(500), DefaultRoundsPolicy(), RoundsLatches{})
	if err != nil || got.Method != TemperatureHeatElectric {
		t.Fatal(got, err)
	}
	got, err = SelectTemperatureMethod(domain.Known(frozenRoom(Amount{Resource: "Heater", Count: 1})), heatingReady(500), DefaultRoundsPolicy(), RoundsLatches{})
	if err != nil || got.Method != TemperatureWait {
		t.Fatal(got, err)
	}
}

func TestTemperatureElectricHeaterKeepsCampfireWhenUnavailable(t *testing.T) {
	for name, mutate := range map[string]func(*TemperatureCooling){
		"renewable fuel":   func(c *TemperatureCooling) { c.HeatFuelFinite = false },
		"no heater def":    func(c *TemperatureCooling) { c.Heater = "" },
		"research":         func(c *TemperatureCooling) { c.HeaterAvailable = domain.Known(false) },
		"availability":     func(c *TemperatureCooling) { c.HeaterAvailable = domain.Unknown[bool]() },
		"draw unknown":     func(c *TemperatureCooling) { c.HeaterDrawW = domain.Unknown[float64]() },
		"no spare power":   func(c *TemperatureCooling) { *c = heatingReady(100) },
		"power unknown":    func(c *TemperatureCooling) { c.Power = domain.Unknown[PowerTopology]() },
		"no network exist": func(c *TemperatureCooling) { c.Power = domain.Known(PowerTopology{}) },
	} {
		c := heatingReady(500)
		mutate(&c)
		got, err := SelectTemperatureMethod(domain.Known(frozenRoom()), c, DefaultRoundsPolicy(), RoundsLatches{})
		if err != nil || got.Method != TemperatureHeat {
			t.Fatalf("%s: %+v %v", name, got, err)
		}
	}
	// With renewable fuel a standing campfire still holds the room.
	c := heatingReady(500)
	c.HeatFuelFinite = false
	got, err := SelectTemperatureMethod(domain.Known(frozenRoom(Amount{Resource: "Campfire", Count: 1})), c, DefaultRoundsPolicy(), RoundsLatches{})
	if err != nil || got.Method != TemperatureWait {
		t.Fatal(got, err)
	}
}

func TestPowerBudgetCountsHeaterDraw(t *testing.T) {
	site := func(def string, w float64) PowerSite {
		return PowerSite{ID: def, Definition: def, PowerBuilding: PowerBuilding{BaseW: domain.Known(w), Connected: domain.Known(true), Network: domain.Known("net")}}
	}
	without := PowerTopology{Buildings: []PowerSite{site("WindTurbine", 400)}}
	with := PowerTopology{Buildings: append(append([]PowerSite{}, without.Buildings...), site("Heater", -175))}
	a, _ := without.SpareW().Value()
	b, _ := with.SpareW().Value()
	if a-b != 175 {
		t.Fatal("heater draw not subtracted from spare", a, b)
	}
	solar := PowerSourceProfile{Daily: 0.5, Night: 0, Solar: true}
	budget := func(demand float64) PowerBudget {
		return ComputePowerBudget(PowerBudgetInput{DemandW: demand, Producers: []PowerProducer{{Definition: "SolarGenerator", NominalW: 1000, Profile: solar}}, ChargeEfficiency: 0.5})
	}
	if budget(175).NightDeficitWD <= budget(0).NightDeficitWD {
		t.Fatal("heater demand must raise the night deficit")
	}
}
