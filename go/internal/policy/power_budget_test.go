package policy

import (
	"math"
	"testing"
)

func TestPowerBudgetSizesGenerationAndStorageIndependently(t *testing.T) {
	solar := PowerProducer{Definition: "SolarGenerator", NominalW: 1700}
	wood := PowerProducer{Definition: "WoodFiredGenerator", NominalW: 1000}
	wind := PowerProducer{Definition: "WindTurbine", NominalW: 2300}
	geo := PowerProducer{Definition: "GeothermalGenerator", NominalW: 3600}
	near := func(a, b float64) bool { return math.Abs(a-b) < 0.5 }
	solarDayW := 1700 * SolarDailyFraction / (1 - SolarNightFraction)
	for _, tc := range []struct {
		name                     string
		in                       PowerBudgetInput
		generationW, storageWD   float64
		batteries                int
		nightDeficit, daySurplus float64
	}{
		// No producer at all: the whole load is a generation shortfall and
		// storage has nothing to bank.
		{name: "nothing", in: PowerBudgetInput{DemandW: 200}, generationW: 200, nightDeficit: 200 * SolarNightFraction},
		// One wood generator covers a lamp around the clock.
		{name: "wood-covers", in: PowerBudgetInput{DemandW: 200, Producers: []PowerProducer{wood}}},
		// A sun lamp (2900 W) on one wood generator: 1900 W short by day and
		// night; the reserve case fixture.
		{name: "wood-short", in: PowerBudgetInput{DemandW: 2900, Producers: []PowerProducer{wood}}, generationW: 1900, nightDeficit: 1900 * SolarNightFraction},
		// Solar alone over a 200 W load: the day surplus refills the night
		// at half efficiency, so one battery plus margin is all that is short.
		{name: "solar-night", in: PowerBudgetInput{DemandW: 200, Producers: []PowerProducer{solar}, StorageMargin: 0.25}, storageWD: 200 * SolarNightFraction * 1.25, batteries: 1, nightDeficit: 200 * SolarNightFraction, daySurplus: (solarDayW - 200) * (1 - SolarNightFraction)},
		// The same colony with a bank installed is covered.
		{name: "solar-banked", in: PowerBudgetInput{DemandW: 200, Producers: []PowerProducer{solar}, CapacityWD: 600, StorageMargin: 0.25}, storageWD: 0, nightDeficit: 200 * SolarNightFraction},
		// 1500 W on one panel: the daytime average (~1327 W) is short, so
		// the answer is generation, and a bank the surplus could fill is
		// nothing since there is no surplus.
		{name: "solar-overdrawn", in: PowerBudgetInput{DemandW: 1500, Producers: []PowerProducer{solar}}, generationW: (1500 - solarDayW) + (1500-(1500-solarDayW))*SolarNightFraction/(SolarNightFraction+0.5*(1-SolarNightFraction)), nightDeficit: 1500 * SolarNightFraction},
		// Eclipse zeroes the panel: back to the no-producer case.
		{name: "eclipse", in: PowerBudgetInput{DemandW: 200, Producers: []PowerProducer{solar}, CapacityWD: 600, Eclipse: true}, generationW: 200, nightDeficit: 200 * SolarNightFraction},
		// Wind averages ~1200 W day and night: 1000 W rides on it with no
		// night deficit to bank, so nothing is short.
		{name: "wind-average", in: PowerBudgetInput{DemandW: 1000, Producers: []PowerProducer{wind}}},
		// Geothermal is constant.
		{name: "geothermal", in: PowerBudgetInput{DemandW: 3000, Producers: []PowerProducer{geo}}},
		// Solar plus wood: the generator carries the night, the panel the
		// day; nothing is short even without a bank.
		{name: "solar-and-wood", in: PowerBudgetInput{DemandW: 900, Producers: []PowerProducer{solar, wood}}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			b := ComputePowerBudget(tc.in)
			if !near(b.GenerationShortfallW, tc.generationW) || !near(b.StorageShortfallWD, tc.storageWD) || b.Batteries() != tc.batteries {
				t.Fatalf("%+v want generation %.1f W storage %.1f Wd batteries %d", b, tc.generationW, tc.storageWD, tc.batteries)
			}
			if !near(b.NightDeficitWD, tc.nightDeficit) || tc.daySurplus > 0 && !near(b.DaySurplusWD, tc.daySurplus) {
				t.Fatalf("%+v want night deficit %.1f day surplus %.1f", b, tc.nightDeficit, tc.daySurplus)
			}
			if b.GenerationShortfallW > 0 && b.StorageShortfallWD > 0 && b.DaySurplusWD == 0 {
				t.Fatal("storage proposed without a surplus to fill it", b)
			}
		})
	}
	// A constant source sized to the generation shortfall balances the
	// overdrawn solar net exactly: the remaining night deficit equals what
	// the surplus stores at charge efficiency.
	first := ComputePowerBudget(PowerBudgetInput{DemandW: 1500, Producers: []PowerProducer{solar}})
	second := ComputePowerBudget(PowerBudgetInput{DemandW: 1500, Producers: []PowerProducer{solar, {Definition: "ChemfuelPoweredGenerator", NominalW: first.GenerationShortfallW}}})
	if !near(second.GenerationShortfallW, 0) || !near(second.NightDeficitWD, second.DaySurplusWD*BatteryChargeEfficiency) {
		t.Fatal(first, second)
	}
	if p := SourceProfile("Unknown"); p.Daily != 1 || p.Night != 1 || p.Solar {
		t.Fatal(p)
	}
}
