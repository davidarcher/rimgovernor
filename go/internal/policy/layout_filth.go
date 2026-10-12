package policy

import "slices"

// impressivenessCleanlinessScale is the divisor RoomStatWorker_Impressiveness
// puts under the room's cleanliness (1 + min(cleanliness, 0) / 2.5) before it
// takes the cleanliness factor of the stat.
const impressivenessCleanlinessScale = 2.5

// CurvePoint is one point of a game SimpleCurve.
type CurvePoint struct{ X, Y float64 }

// FilthModel is what a butchering costs a room in game units, read from the
// defs: the filth a butchered corpse spawns (Corpse.ButcherProducts calls
// FilthMaker.TryMakeFilth once with the race's BloodDef), that filth's
// Cleanliness stat per unit of thickness, and the FoodPoisonChance room stat's
// curve over the room's cleanliness. The zero value prices nothing.
type FilthModel struct {
	Def         string
	Cleanliness float64
	// FoodPoison is the curve of RoomStatDefOf.FoodPoisonChance (a
	// RoomStatWorker_FromStatByCurve over Cleanliness), points by rising X.
	FoodPoison []CurvePoint
}

// evaluateCurve is SimpleCurve.Evaluate: linear between points, the end values
// outside them.
func evaluateCurve(points []CurvePoint, x float64) float64 {
	switch {
	case len(points) == 0:
		return 0
	case x <= points[0].X:
		return points[0].Y
	case x >= points[len(points)-1].X:
		return points[len(points)-1].Y
	}
	for i := 1; i < len(points); i++ {
		if x <= points[i].X {
			a, b := points[i-1], points[i]
			return a.Y + (b.Y-a.Y)*(x-a.X)/(b.X-a.X)
		}
	}
	return 0
}

// FilthCost is the price of a filth node standing in a room that holds other
// stations, in the units the game reads it in. Each filth unit adds its
// Cleanliness stat to the room's sum, which the room's cell count divides
// (RoomStatWorker_Cleanliness), so one unit moves the room's cleanliness by
// CleanlinessPerUnit. Food poison chance is the curve of the room's
// cleanliness, zero from PoisonOnsetCleanliness up, so FoodPoisonPerUnit is the
// chance one standing unit alone gives and UnitsToPoison the units it takes to
// leave the zero. ImpressivenessInputPerUnit is the change one unit makes to
// the cleanliness input of the Impressiveness stat (1 + cleanliness / 2.5,
// while the room is not clean), which the stat's combination then weighs.
// Everything is read from a room at cleanliness 0, the clamp of that input.
// Exposed are the nodes in the room whose effect scales with cleanliness (a
// cook's meals read the food poison chance).
type FilthCost struct {
	Node, Filth string
	Cells       int
	Exposed     []string

	CleanlinessPerUnit         float64
	FoodPoisonPerUnit          float64
	PoisonOnsetCleanliness     float64
	UnitsToPoison              float64
	ImpressivenessInputPerUnit float64
}

// filthCosts prices each filthy node of a room of cells cells that also holds
// stations the filth is not for; exposed are the nodes that read cleanliness.
// A model that prices nothing owes nothing.
func (m FilthModel) filthCosts(filthy, exposed []string, cells int) []FilthCost {
	if m.Def == "" || cells <= 0 || len(filthy) == 0 {
		return nil
	}
	per := m.Cleanliness / float64(cells)
	onset := 0.0
	if n := len(m.FoodPoison); n > 0 && m.FoodPoison[n-1].Y == 0 {
		onset = m.FoodPoison[n-1].X
	}
	units := 0.0
	if per < 0 && onset < 0 {
		units = onset / per
	}
	var out []FilthCost
	for _, node := range filthy {
		out = append(out, FilthCost{Node: node, Filth: m.Def, Cells: cells, Exposed: slices.Clone(exposed),
			CleanlinessPerUnit: per, FoodPoisonPerUnit: evaluateCurve(m.FoodPoison, per), PoisonOnsetCleanliness: onset,
			UnitsToPoison: units, ImpressivenessInputPerUnit: per / impressivenessCleanlinessScale})
	}
	return out
}
