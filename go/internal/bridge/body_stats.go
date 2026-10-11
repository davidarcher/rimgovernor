package bridge

import (
	"math"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// validateBodyStats refuses a body block with a non-finite number or a
// negative speed, capacity, resistance, severity or capacity level. The
// temperatures may be any finite value.
func validateBodyStats(b *o.PawnBodyStats) error {
	if b == nil {
		return nil
	}
	for _, v := range []*float64{b.ComfortableMinC, b.ComfortableMaxC, b.CellTemperatureC} {
		if v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0)) {
			return contract("invalid body temperature")
		}
	}
	for _, v := range []*float64{b.MoveSpeed, b.CarryCapacity, b.ToxicEnvironmentResistance, b.Hypothermia, b.Heatstroke, b.Frostbite, b.ToxicBuildup, b.Moving, b.Manipulation, b.Consciousness} {
		if v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0) || *v < 0) {
			return contract("invalid body stat")
		}
	}
	return nil
}
