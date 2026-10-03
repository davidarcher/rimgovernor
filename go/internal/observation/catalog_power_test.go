package observation

import (
	"testing"

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
		"WindTurbine":              policy.WindPowerProfile,
		"WoodFiredGenerator":       policy.ConstantPowerProfile,
		"ChemfuelPoweredGenerator": policy.ConstantPowerProfile,
		"GeothermalGenerator":      policy.ConstantPowerProfile,
	} {
		if got, ok := sources[name]; !ok || got != want {
			t.Errorf("%s: profile %+v (listed %v), want %+v", name, got, ok, want)
		}
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
