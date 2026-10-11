package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// HypothermiaTuning is the vanilla hypothermia model, one step per
// hypothermiaInterval ticks. The values mirror Verse.HealthTuning
// (HypothermiaGrowthPerDegreeUnder, MinHypothermiaProgress,
// HarmfulTemperatureOffset; defs.proto HealthTuningConstants), which no
// Go reader consumes yet: PublishedHypothermiaTuning is the RimWorld wiki
// formula, and the native probe checks it against the game.
type HypothermiaTuning struct {
	GrowthPerDegreeUnder float64
	MinProgress          float64
	HarmfulOffsetC       float64
}

// PublishedHypothermiaTuning is the published formula: onset below
// comfortable minimum minus 10 C, growth max(0.00075, (degrees below
// comfortable - 10) x 0.0000645) per step.
var PublishedHypothermiaTuning = HypothermiaTuning{
	GrowthPerDegreeUnder: 0.0000645,
	MinProgress:          0.00075,
	HarmfulOffsetC:       10,
}

// hypothermiaInterval is the ticks between severity updates.
const hypothermiaInterval = 60

// Hypothermia stage floors (severity): a trip target is one of these.
const (
	HypothermiaShivering = 0.04
	HypothermiaMinor     = 0.20
	HypothermiaSerious   = 0.35
	HypothermiaExtreme   = 0.62
	HypothermiaDeath     = 1.0
)

// OutdoorBudget is how long a pawn may stay outdoors. Unbounded means the
// hazard does not grow at this temperature.
type OutdoorBudget struct {
	Ticks     int
	Unbounded bool
}

// growth is the severity added per step at tempC, 0 when the temperature
// is not harmful for a pawn comfortable down to comfortMinC.
func (t HypothermiaTuning) growth(tempC, comfortMinC float64) float64 {
	below := comfortMinC - tempC
	if below <= t.HarmfulOffsetC {
		return 0
	}
	return max(t.MinProgress, (below-t.HarmfulOffsetC)*t.GrowthPerDegreeUnder)
}

// HypothermiaRecovery is the severity shed per step while not harmed:
// max(0.0015, min(0.015, 0.027 x severity)). Recovery is slow, so a trip
// starts from the severity the previous one left.
func HypothermiaRecovery(severity float64) float64 {
	return max(0.0015, min(0.015, 0.027*severity))
}

// OutdoorBudgetFor returns the ticks a pawn can stay outdoors before its
// hypothermia reaches target (a stage floor), starting at severity. Only
// cold is modelled; heat and fallout are unknown. Unknown temperature,
// comfort or severity is unknown. A severity already at target gets zero.
func OutdoorBudgetFor(kind HazardKind, tempC, comfortMinC, severity domain.Fact[float64], target float64, tuning HypothermiaTuning) domain.Fact[OutdoorBudget] {
	if kind != HazardExtremeCold {
		return domain.Unknown[OutdoorBudget]()
	}
	temp, tk := tempC.Value()
	comfort, ck := comfortMinC.Value()
	sev, sk := severity.Value()
	if !tk || !ck || !sk {
		return domain.Unknown[OutdoorBudget]()
	}
	g := tuning.growth(temp, comfort)
	if g <= 0 {
		return domain.Known(OutdoorBudget{Unbounded: true})
	}
	steps := 0
	if sev < target {
		for s := sev + g; s < target; s += g {
			steps++
		}
	}
	return domain.Known(OutdoorBudget{Ticks: steps * hypothermiaInterval})
}
