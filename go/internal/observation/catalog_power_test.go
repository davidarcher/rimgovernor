package observation

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The power budget reads its producer delivery profiles and the battery's
// storage from the recorded catalog's comp rows, not from def names.
func TestRecordedCatalogPowerRows(t *testing.T) {
	catalog := recordedCatalog(t)
	sources, err := catalog.PowerSources()
	if err != nil {
		t.Fatal(err)
	}
	for name, want := range map[string]policy.PowerSourceProfile{
		"SolarGenerator":           policy.SolarPowerProfile,
		"WoodFiredGenerator":       policy.ConstantPowerProfile,
		"ChemfuelPoweredGenerator": policy.ConstantPowerProfile,
		"GeothermalGenerator":      policy.ConstantPowerProfile,
	} {
		if got, ok := sources[name]; !ok || got != want {
			t.Errorf("%s: profile %+v (listed %v), want %+v", name, got, ok, want)
		}
	}
	// The turbine's profile comes from the catalog's wind range and the clear
	// weather: 2199 W of 2300 W, not the 1200 W the repo once assumed.
	if wind := sources["WindTurbine"]; math.Abs(wind.Daily*2300-2199) > 1 || wind.Night != wind.Daily || wind.Solar {
		t.Errorf("WindTurbine: profile %+v, want about 0.956 around the clock", wind)
	}
	if _, listed := sources["Battery"]; listed {
		t.Error("the battery is a store, not a producer")
	}
	battery, err := catalog.PowerBattery(policy.BatteryDefinition)
	if err != nil {
		t.Fatal(err)
	}
	if battery != (policy.PowerBattery{CapacityWD: 600, Efficiency: 0.5}) {
		t.Errorf("battery %+v, want the stock 600 Wd at 0.5", battery)
	}
	if _, err := catalog.PowerBattery("SolarGenerator"); err == nil {
		t.Error("a def without a battery comp named a battery")
	}
}

// The generator ranking reads cost, watts, fuel and tank from the generator
// rows and the steel per component from the component recipe.
func TestRecordedCatalogGeneratorRows(t *testing.T) {
	catalog := recordedCatalog(t)
	steel := func(steel, components int64) []policy.Amount {
		return []policy.Amount{{Resource: policy.SteelResource, Count: steel}, {Resource: policy.ComponentResource, Count: components}}
	}
	for name, want := range map[string]policy.GeneratorRow{
		"SolarGenerator":           {NominalW: 1700, Costs: steel(100, 3)},
		"WindTurbine":              {NominalW: 2300, Costs: steel(100, 2)},
		"WoodFiredGenerator":       {NominalW: 1000, Costs: steel(100, 2), Fuel: "WoodLog", FuelCapacity: 75},
		"ChemfuelPoweredGenerator": {NominalW: 1000, Costs: steel(100, 3), Fuel: "Chemfuel", FuelCapacity: 30},
	} {
		got, err := catalog.GeneratorRow(name)
		if err != nil || !reflect.DeepEqual(got, want) {
			t.Errorf("%s: %+v, %v; want %+v", name, got, err, want)
		}
	}
	if _, err := catalog.GeneratorRow("Battery"); err == nil {
		t.Error("a battery is not a generator")
	}
	if rate, err := catalog.ProductInputCost(string(policy.ComponentResource), string(policy.SteelResource)); err != nil || !reflect.DeepEqual(rate, domain.Known(12.0)) {
		t.Errorf("component steel %v, %v", rate, err)
	}
	if rate, err := catalog.ProductInputCost(string(policy.ComponentResource), "Gold"); err != nil || rate != domain.Unknown[float64]() {
		t.Errorf("component gold %v, %v", rate, err)
	}
}
