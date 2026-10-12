package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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
	// Poisoning is what one poisoned meal costs the eater (the FoodPoisoning
	// hediff); the zero value prices no poisoning.
	Poisoning PoisoningModel
}

// FoodPoisonTable is the judgment behind the poisoning price (#2955), registered
// in DefTables. Hediff is the sickness a poisoned meal gives (FoodUtility.
// AddFoodPoisoningHediff). WorkCapacity is the capacity of its stages that
// stands for the sick colonist's work: Consciousness, the lowest of the
// capacities the hediff cuts that a worker uses (Manipulation 0.9 / 0.8 /
// 0.9 and Moving 0.8 / 0.5 / 0.8 are higher or equal in every stage), and the
// one the game scales all awake action by; the price is the work lost at that
// factor. VomitJob is the job a vomit starts (JobDriver_Vomit, read with
// ilspycmd) and VomitTicks its mean length, 300 to 900 ticks drawn uniformly,
// during which the colonist does no work.
type FoodPoisonTable struct {
	Hediff, WorkCapacity, VomitJob string
	VomitTicks                     float64
}

var foodPoisonGame = FoodPoisonTable{Hediff: "FoodPoisoning", WorkCapacity: "Consciousness", VomitJob: "Vomit", VomitTicks: 600}

// FoodPoisonGame is the table the bridge reads the poisoning model with.
func FoodPoisonGame() FoodPoisonTable { return foodPoisonGame }

// PoisonStage is one stage of the poisoning hediff from MinSeverity up:
// WorkFactor the stage's factor on the work capacity (1 when it cuts none) and
// VomitMtbDays the mean days between vomits (0 for none).
type PoisonStage struct{ MinSeverity, WorkFactor, VomitMtbDays float64 }

// PoisoningModel is the FoodPoisoning hediff: it starts at severity Initial and
// loses PerDay of it a day (HediffCompProperties_SeverityPerDay), passing
// through Stages from the top one down to zero.
type PoisoningModel struct {
	Initial, PerDay float64
	Stages          []PoisonStage
}

// WorkDays is the work one poisoning costs, in work-days: for each stage the
// days spent in it times the work capacity it takes away, plus the days of
// vomiting (the stage's days over its vomit interval, each VomitTicks long).
// Worked: from severity 1 at 1 a day, the initial stage (0.8 up) lasts 0.2 days
// at x0.6, the major (0.2 up) 0.6 days at x0.5 and the recovering 0.2 days at
// x0.6, 0.08 + 0.30 + 0.08 = 0.46 work-days; vomits every 0.3, 0.2 and 0.4 days
// are 0.667 + 3 + 0.5 = 4.17 vomits of 600 ticks, 0.042 work-days; 0.50 in all.
// Zero for a model that is not known.
func (m PoisoningModel) WorkDays() float64 {
	if !finitePositive(m.Initial) || !finitePositive(m.PerDay) || len(m.Stages) == 0 {
		return 0
	}
	stages := slices.Clone(m.Stages)
	sort.Slice(stages, func(i, j int) bool { return stages[i].MinSeverity < stages[j].MinSeverity })
	total, top := 0.0, m.Initial
	for i := len(stages) - 1; i >= 0; i-- {
		low := max(stages[i].MinSeverity, 0)
		if i == 0 {
			low = 0
		}
		if low >= top {
			continue
		}
		days := (top - low) / m.PerDay
		total += days * (1 - stages[i].WorkFactor)
		if stages[i].VomitMtbDays > 0 {
			total += days / stages[i].VomitMtbDays * foodPoisonGame.VomitTicks / domain.TicksPerDay
		}
		top = low
	}
	return total
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

	// MealsPerDay is the meals a day the exposed nodes' stations in the room
	// make (the graph's demand), each poisoned with chance FoodPoisonPerUnit
	// (CompFoodPoisonable.Notify_RecipeProduced) and each poisoned meal giving
	// its eater one poisoning (PostIngested). PoisoningsPerDay is their product
	// and PoisonWorkDays the work-days a day they cost (PoisoningModel.WorkDays);
	// the price of keeping the node in this room, in the #2945 work-day unit.
	MealsPerDay      float64
	PoisoningsPerDay float64
	PoisonWorkDays   float64
}

// filthCosts prices each filthy node of a room of cells cells that also holds
// stations the filth is not for; exposed are the nodes that read cleanliness
// and meals the meals a day they make. A model that prices nothing owes
// nothing.
func (m FilthModel) filthCosts(filthy, exposed []string, cells int, meals float64) []FilthCost {
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
	chance := evaluateCurve(m.FoodPoison, per)
	poisonings := meals * chance
	var out []FilthCost
	for _, node := range filthy {
		out = append(out, FilthCost{Node: node, Filth: m.Def, Cells: cells, Exposed: slices.Clone(exposed),
			CleanlinessPerUnit: per, FoodPoisonPerUnit: chance, PoisonOnsetCleanliness: onset,
			UnitsToPoison: units, ImpressivenessInputPerUnit: per / impressivenessCleanlinessScale,
			MealsPerDay: meals, PoisoningsPerDay: poisonings, PoisonWorkDays: poisonings * m.Poisoning.WorkDays()})
	}
	return out
}
