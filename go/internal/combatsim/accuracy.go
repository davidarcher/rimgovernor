package combatsim

import "math"

// The ShootingAccuracyPawn stat (Core Stats_Pawns_Combat.xml) is computed by
// RimWorld.StatWorker.GetValueUnfinalized and FinalizeValue: a raw value of the
// skill term, the pawn's trait and gear offsets and the capacity offsets
// (Sight scale 12 max 2, Manipulation scale 8 max 1,
// PawnCapacityOffset.GetOffset = (min(level, max) - 1) x scale), then the
// stat's postProcessCurve. The native read gives the finished value of a
// healthy pawn; an injured pawn's Manipulation (limb damage, pain) and Sight
// (eye damage) fall and take the raw value down with them, so its accuracy per
// cell drops (A43). The importer's pawns are healthy at tick 0 (A28), so the
// capacity offsets are 0 there and the read value is the curve's output for
// the raw value the curve is inverted to.

// accuracyCurve is the stat's postProcessCurve as (raw, accuracy) points.
var accuracyCurve = [...][2]float64{
	{-20, 0.70}, {-10, 0.80}, {-6, 0.83}, {-4, 0.85}, {-2, 0.87}, {0, 0.89},
	{2, 0.93}, {4, 0.94}, {6, 0.95}, {8, 0.96}, {10, 0.97}, {12, 0.975},
	{14, 0.98}, {16, 0.98333}, {18, 0.98666}, {20, 0.99}, {22, 0.9925},
	{26, 0.995}, {30, 0.9965}, {40, 0.998}, {60, 0.999},
}

// accuracyFromRaw evaluates the postProcessCurve (SimpleCurve.Evaluate clamps
// outside the first and last point).
func accuracyFromRaw(raw float64) float64 {
	c := accuracyCurve[:]
	if raw <= c[0][0] {
		return c[0][1]
	}
	for i := 1; i < len(c); i++ {
		if raw <= c[i][0] {
			return lerp(c[i-1][1], c[i][1], (raw-c[i-1][0])/(c[i][0]-c[i-1][0]))
		}
	}
	return c[len(c)-1][1]
}

// rawFromAccuracy inverts the curve, which rises everywhere. A value outside
// the curve's range has no single raw value; it maps to the nearest end.
func rawFromAccuracy(acc float64) float64 {
	c := accuracyCurve[:]
	if acc <= c[0][1] {
		return c[0][0]
	}
	for i := 1; i < len(c); i++ {
		if acc <= c[i][1] {
			return c[i-1][0] + (acc-c[i-1][1])/(c[i][1]-c[i-1][1])*(c[i][0]-c[i-1][0])
		}
	}
	return c[len(c)-1][0]
}

// capacityOffset is PawnCapacityOffset.GetOffset.
func capacityOffset(level, max, scale float64) float64 { return (math.Min(level, max) - 1) * scale }

// refreshAccuracy sets the pawn's ShootingAccuracyPawn from its current
// Manipulation and Sight, as the stat is recomputed on every read. A pawn whose
// capacities are at full health keeps the value it was read with.
func (p *pawn) refreshAccuracy(caps capacities) {
	off := capacityOffset(caps.sight, 2, 12) + capacityOffset(caps.manipulation, 1, 8)
	if off == 0 {
		p.sh.acc = p.accHealthy
		return
	}
	p.sh.acc = accuracyFromRaw(rawFromAccuracy(p.accHealthy) + off)
}
