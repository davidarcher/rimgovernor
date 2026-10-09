package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResourceClass groups resources by how fast a shortfall must refill. The
// refill window is the horizon over which a deficit is to be made good, so
// the demand rate is deficit / window.
type ResourceClass string

const (
	// ResourceFood spoils and is eaten daily: refill within a day.
	ResourceFood ResourceClass = "food"
	// ResourceMaterial is durable stock (steel, components, cloth, wood):
	// refill within a few days.
	ResourceMaterial ResourceClass = "material"
)

// Default refill windows, in game days.
const (
	FoodRefillWindowDays     = 1.0
	MaterialRefillWindowDays = 3.0
)

// RefillWindowDays is the class's default refill window; unknown for a class
// the model does not name.
func RefillWindowDays(c ResourceClass) domain.Fact[float64] {
	switch c {
	case ResourceFood:
		return domain.Known(FoodRefillWindowDays)
	case ResourceMaterial:
		return domain.Known(MaterialRefillWindowDays)
	}
	return domain.Unknown[float64]()
}

// BenchWorker is one pawn assigned to a bench: the game hours per day it
// works and its work speed stat for the recipe (1 is a baseline pawn).
type BenchWorker struct {
	WorkHoursPerDay domain.Fact[float64]
	WorkSpeed       domain.Fact[float64]
}

// BenchRecipe is the recipe and bench side of the capacity model: the work
// amount one unit of product takes (RimWorld work ticks at speed 1) and the
// bench's effective work speed factor.
type BenchRecipe struct {
	WorkPerUnit    domain.Fact[float64]
	BenchWorkSpeed domain.Fact[float64]
}

// DemandRate is units per day needed to clear deficit within the class's
// refill window. A non-positive deficit needs nothing. Unknown when the
// deficit or the class window is unknown.
func DemandRate(deficit domain.Fact[float64], class ResourceClass) domain.Fact[float64] {
	d, ok := deficit.Value()
	if !ok || math.IsNaN(d) || math.IsInf(d, 0) {
		return domain.Unknown[float64]()
	}
	w, ok := RefillWindowDays(class).Value()
	if !ok {
		return domain.Unknown[float64]()
	}
	return domain.Known(math.Max(d, 0) / w)
}

// BenchCapacity is the units per day one bench produces with the given
// workers at the table: sum over workers of hours x ticks per hour x worker
// speed, times bench speed, divided by the recipe's work per unit. Unknown
// when any input is unknown, the recipe work or bench speed is not positive,
// or a worker's hours or speed is negative or not finite. No workers is a
// known zero capacity.
func BenchCapacity(r BenchRecipe, workers []BenchWorker) domain.Fact[float64] {
	work, ok := r.WorkPerUnit.Value()
	if !ok || !positiveFinite(work) {
		return domain.Unknown[float64]()
	}
	bench, ok := r.BenchWorkSpeed.Value()
	if !ok || !positiveFinite(bench) {
		return domain.Unknown[float64]()
	}
	var ticks float64
	for _, w := range workers {
		h, ok := w.WorkHoursPerDay.Value()
		if !ok || !nonNegativeFinite(h) {
			return domain.Unknown[float64]()
		}
		s, ok := w.WorkSpeed.Value()
		if !ok || !nonNegativeFinite(s) {
			return domain.Unknown[float64]()
		}
		ticks += h * domain.TicksPerHour * s
	}
	return domain.Known(ticks * bench / work)
}

// BenchesWanted is ceil(demand / capacity) for demand and per-bench capacity
// in units per day. Zero demand wants no benches whatever the capacity;
// positive demand against zero capacity is unknown, not infinite.
func BenchesWanted(demand, capacity domain.Fact[float64]) domain.Fact[int] {
	d, ok := demand.Value()
	if !ok || !nonNegativeFinite(d) {
		return domain.Unknown[int]()
	}
	c, ok := capacity.Value()
	if !ok || !nonNegativeFinite(c) {
		return domain.Unknown[int]()
	}
	if d == 0 {
		return domain.Known(0)
	}
	if c == 0 {
		return domain.Unknown[int]()
	}
	n := math.Ceil(d/c - 1e-9)
	if n > math.MaxInt32 {
		return domain.Unknown[int]()
	}
	return domain.Known(int(n))
}

func positiveFinite(v float64) bool    { return v > 0 && !math.IsInf(v, 0) && !math.IsNaN(v) }
func nonNegativeFinite(v float64) bool { return v >= 0 && !math.IsInf(v, 0) && !math.IsNaN(v) }
