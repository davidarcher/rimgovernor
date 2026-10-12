package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Cold exposure (#2880): whether a roofed cold room can be kept under rot by
// vents to the outdoors alone, with no cooler. The test simulates the room over
// a day-resolved year against the seasonal outdoor curve; it is advice beside a
// group's cooling station and the old planner stays authoritative.

// ExposureLiterals are the game literals the simulation states. They are in the
// game's code, not in a def: the vent's are Building_Vent (TickRare every 250
// ticks, EqualizeTemperaturesThroughBuilding rate 14) and the swing is
// GenTemperature.OffsetFromSunCycle, cos(2pi(dayPct+0.32))*7. They are a
// DefTables entry keyed by the Vent ThingDef so a renamed vent fails the
// dangling-reference gate; the swing rides on that row because no def holds it.
// Door traffic, wall and roof leakage, weather offsets and drift inside a
// twelfth are left out of the model.
type ExposureLiterals struct {
	VentIntervalTicks int
	VentRate          float64
	SwingC            float64
	SwingPhase        float64
}

var exposureLiterals = map[string]ExposureLiterals{
	"Vent": {VentIntervalTicks: 250, VentRate: 14, SwingC: 7, SwingPhase: 0.32},
}

// ExposureMaxVents bounds the vents the search tries; a room that needs more is
// not satisfied by exposure.
const ExposureMaxVents = 8

// ColdExposure is the exposure satisfier's answer for one cold room. Under rot
// means the peak simulated room temperature stays below the full-rot
// temperature: that slows rot, it does not stop it.
type ColdExposure struct {
	Satisfied bool
	// PeakC is the hottest the room gets over the second simulated year; with
	// no satisfying vent count it is the peak with one vent, the lowest there is.
	PeakC float64
	// Vents is the fewest vents that keep the room under rot; 0 unless Satisfied.
	Vents int
	// Cells and ThresholdC are the inputs the answer is for.
	Cells      int
	ThresholdC float64
}

// ColdExposureFor simulates a roofed room of cells floor cells against the
// outdoor curve (the twelfths' mean temperatures, C) and returns the fewest
// vents, up to maxVents, that keep its peak below fullRotRateC. A vent
// shares its room with the outdoors, so more vents follow the weather and its
// afternoon swing more closely and the peak never falls as vents are added: the
// satisfying counts are a prefix 1..k and the fewest is the first count found
// under rot (with this model one vent whenever any count is). A curve that is not twelve finite means, a non-positive room or a
// non-finite threshold is not satisfied.
func ColdExposureFor(temps []float64, cells, maxVents int, fullRotRateC float64) ColdExposure {
	out := ColdExposure{Cells: cells, ThresholdC: fullRotRateC}
	if len(temps) != 12 || cells <= 0 || maxVents <= 0 || math.IsNaN(fullRotRateC) || math.IsInf(fullRotRateC, 0) {
		return out
	}
	for _, t := range temps {
		if math.IsNaN(t) || math.IsInf(t, 0) {
			return out
		}
	}
	for v := 1; v <= maxVents; v++ {
		peak := exposurePeak(temps, cells, v)
		if v == 1 {
			out.PeakC = peak
		}
		if peak < fullRotRateC {
			out.Satisfied, out.Vents, out.PeakC = true, v, peak
			return out
		}
	}
	return out
}

// groupExposure is the exposure answer beside a cold group's cooling station:
// nil for a group that does not require cold, or while the curve or the
// full-rot temperature is unknown.
func groupExposure(f StationFacts, g RoomGroup) *ColdExposure {
	curve, known := f.OutdoorCurve.Value()
	if !known || f.FullRotRateC <= 0 || !slices.Contains(g.Requires, EnvCold) {
		return nil
	}
	exposure := ColdExposureFor(curve, g.Area, ExposureMaxVents, f.FullRotRateC)
	return &exposure
}

// exposurePeak is the hottest the room gets over the second of two laps of the
// year, starting at the outdoor temperature. Each vent moves the room
// (T_out-T_in)/2*min(rate/cells,1) of the way to outdoors every interval.
func exposurePeak(temps []float64, cells, vents int) float64 {
	var lit ExposureLiterals
	for _, row := range exposureLiterals { // the one row, the vent's
		lit = row
	}
	step := lit.VentIntervalTicks
	share := math.Min(lit.VentRate/float64(cells), 1) / 2
	room := outdoorAt(temps, lit, 0)
	peak := math.Inf(-1)
	for t := step; t <= 2*domain.TicksPerYear; t += step {
		out := outdoorAt(temps, lit, t)
		for range vents {
			room += (out - room) * share
		}
		if t > domain.TicksPerYear && room > peak {
			peak = room
		}
	}
	return peak
}

// outdoorAt is the outdoor temperature at a tick: the twelfths' means
// interpolated linearly between their midpoints (around the year), plus the
// diurnal swing.
func outdoorAt(temps []float64, lit ExposureLiterals, tick int) float64 {
	const twelfths = 12
	year := tick % domain.TicksPerYear
	pos := float64(year)/(float64(domain.TicksPerYear)/twelfths) - 0.5
	if pos < 0 {
		pos += twelfths
	}
	i := int(pos)
	frac := pos - float64(i)
	mean := temps[i%twelfths]*(1-frac) + temps[(i+1)%twelfths]*frac
	dayPct := float64(tick%domain.TicksPerDay) / domain.TicksPerDay
	return mean + math.Cos(2*math.Pi*(dayPct+lit.SwingPhase))*lit.SwingC
}
