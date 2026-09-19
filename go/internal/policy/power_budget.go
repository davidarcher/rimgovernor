package policy

import "math"

// Wiki figures the energy budget reasons from (rimworldwiki.com Power,
// Battery, Solar generator, Wind turbine). Nominal wattage per producer comes
// from the native census; only the shape of a day is assumed here.
const (
	// BatteryCapacityWD is one battery's usable storage.
	BatteryCapacityWD = 600.0
	// BatteryChargeEfficiency is the stored share of a charging surplus: one
	// watt of surplus for a day stores half a watt-day.
	BatteryChargeEfficiency = 0.5
	// BatterySelfDischargeWD is what each battery loses per day on its own.
	BatterySelfDischargeWD = 5.0
	// SolarNightFraction is the share of a day a solar generator makes
	// nothing (about eight hours near the equator).
	SolarNightFraction = 8.0 / 24.0
	// SolarDailyFraction is a solar generator's average output over a whole
	// day relative to its nominal wattage: ~9 h at full output and ~7 h at
	// partial output, none at night.
	SolarDailyFraction = (9.0 + 7.0*0.5) / 24.0
	// WindAverageFraction is a wind turbine's average output relative to its
	// nominal 2300 W across ordinary weather (~1200 W).
	WindAverageFraction = 1200.0 / 2300.0
	// BatteryDefinition is the storage definition the power family compiles.
	BatteryDefinition = "Battery"
)

// PowerSourceProfile is how a producer definition delivers its nominal wattage
// over a day. Daily is the average share of nominal across 24 h; Night is the
// share delivered while the sun is down. Solar marks a source that stops in
// an eclipse.
type PowerSourceProfile struct {
	Daily, Night float64
	Solar        bool
}

// SourceProfile returns the profile for a producer definition. Fuel-burning,
// geothermal and watermill generators hold nominal output around the clock
// while served; every unlisted producer is treated the same way.
func SourceProfile(definition string) PowerSourceProfile {
	switch definition {
	case "SolarGenerator":
		return PowerSourceProfile{Daily: SolarDailyFraction, Night: 0, Solar: true}
	case "WindTurbine":
		return PowerSourceProfile{Daily: WindAverageFraction, Night: WindAverageFraction}
	}
	return PowerSourceProfile{Daily: 1, Night: 1}
}

// PowerProducer is one producer's nominal wattage and definition as the budget
// sees it.
type PowerProducer struct {
	Definition string
	NominalW   float64
}

// PowerBudgetInput is one network's demand, producers and installed storage
// over the coming day. Eclipse zeroes every solar producer for the day.
type PowerBudgetInput struct {
	DemandW    float64
	Producers  []PowerProducer
	CapacityWD float64
	Eclipse    bool
	// StorageMargin is the share of the night deficit added to the storage
	// target so the bank does not run flat at dawn.
	StorageMargin float64
}

// PowerBudget is a network's 24 h energy balance. Generation and storage
// shortfalls are independent: a solar colony draining at night with spare
// daytime surplus is short of storage only; a colony whose producers cannot
// cover its consumers over a day is short of generation, and no bank fixes
// that.
type PowerBudget struct {
	DemandWD, SupplyWD float64
	// NightDeficitWD is the energy consumers draw during the solar night
	// beyond what still generates then; DaySurplusWD is the spare daytime
	// generation available to charge a bank.
	NightDeficitWD, DaySurplusWD float64
	// StorageNeededWD is the bank the budget wants: the night deficit the
	// day surplus can refill, plus margin.
	StorageNeededWD float64
	// GenerationShortfallW is the constant generation to add so the bank the
	// surplus can fill covers the night; StorageShortfallWD is the storage to
	// add beyond the installed capacity.
	GenerationShortfallW, StorageShortfallWD float64
}

// ComputePowerBudget sizes a network against a 24 h day. A constant source
// added at g watts cuts the night deficit by g x night share and raises the
// day surplus by g x day share; the generation shortfall is the smallest g
// for which the surplus, at battery charge efficiency, refills the night.
// Inputs must be finite and non-negative.
func ComputePowerBudget(in PowerBudgetInput) PowerBudget {
	night, day := SolarNightFraction, 1-SolarNightFraction
	var supplyWD, nightW, dayW float64
	for _, p := range in.Producers {
		profile := SourceProfile(p.Definition)
		if profile.Solar && in.Eclipse {
			continue
		}
		supplyWD += p.NominalW * profile.Daily
		nightW += p.NominalW * profile.Night
		// Daytime output is whatever the daily average leaves after the night.
		dayW += p.NominalW * (profile.Daily - profile.Night*night) / day
	}
	b := PowerBudget{DemandWD: in.DemandW, SupplyWD: supplyWD}
	b.NightDeficitWD = math.Max(0, (in.DemandW-nightW)*night)
	b.DaySurplusWD = math.Max(0, (dayW-in.DemandW)*day)
	// A daytime deficit needs constant generation outright; only then can
	// the remaining night deficit be traded against surplus and storage.
	if dayW < in.DemandW {
		b.GenerationShortfallW = in.DemandW - dayW
		nightW += b.GenerationShortfallW
	}
	fillable := b.DaySurplusWD * BatteryChargeEfficiency
	if remaining := math.Max(0, (in.DemandW-nightW)*night); remaining > fillable {
		b.GenerationShortfallW += (remaining - fillable) / (night + BatteryChargeEfficiency*day)
	}
	covered := math.Min(b.NightDeficitWD, fillable)
	if covered > 0 {
		b.StorageNeededWD = covered * (1 + math.Max(0, in.StorageMargin))
	}
	b.StorageShortfallWD = math.Max(0, b.StorageNeededWD-in.CapacityWD)
	return b
}

// Batteries is the number of batteries that close the storage shortfall.
func (b PowerBudget) Batteries() int {
	if b.StorageShortfallWD <= 0 {
		return 0
	}
	return int(math.Ceil(b.StorageShortfallWD / BatteryCapacityWD))
}
