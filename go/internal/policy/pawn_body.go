package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// PawnBody is the colonist's native body stats (observations PawnBodyStats):
// the game's own stat and hediff values at the read tick. Each field is
// unknown where native could not read it, never zero. Severities are 0 when
// the pawn has no such hediff; capacity levels are 1 at full.
type PawnBody struct {
	// ComfortableMinC and ComfortableMaxC are the comfortable temperature
	// range with the worn apparel, in degrees C.
	ComfortableMinC, ComfortableMaxC domain.Fact[float64]
	// MoveSpeed is cells per second, with the current Moving capacity.
	MoveSpeed domain.Fact[float64]
	// CarryCapacity is the mass the pawn carries, in kg.
	CarryCapacity domain.Fact[float64]
	// ToxicResistance is the toxic environment resistance, 0-1.
	ToxicResistance domain.Fact[float64]
	// CellTemperatureC is the temperature at the pawn's cell.
	CellTemperatureC domain.Fact[float64]
	// Hypothermia, Heatstroke, Frostbite (highest part) and ToxicBuildup
	// are hediff severities.
	Hypothermia, Heatstroke, Frostbite, ToxicBuildup domain.Fact[float64]
	// Moving, Manipulation and Consciousness are capacity levels.
	Moving, Manipulation, Consciousness domain.Fact[float64]
}
