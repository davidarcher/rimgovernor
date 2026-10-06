package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FishingWorkTicksPerDay is the planning assumption for one fisher or
// researcher day: eight work hours of 2500 ticks.
const FishingWorkTicksPerDay = 8 * 2500

// FishingCell is a fishable water cell with whether any available fisher can
// reach it, and its squared distance to the colony centre.
type FishingCell struct {
	Cell            domain.Cell
	Reachable       bool
	DistanceSquared float64
}

// FishingFootprint proposes the zone footprint: the reachable fishable cells
// grown by cardinal flood fill from the cell nearest the colony centre (ties by
// x then z), at most one cell per available fisher. It is access capacity,
// never a population or yield multiplier.
func FishingFootprint(cells []FishingCell, fishers int) []domain.Cell {
	var free []FishingCell
	for _, c := range cells {
		if c.Reachable {
			free = append(free, c)
		}
	}
	if len(free) == 0 || fishers <= 0 {
		return nil
	}
	sort.Slice(free, func(i, j int) bool {
		a, b := free[i], free[j]
		if a.DistanceSquared != b.DistanceSquared {
			return a.DistanceSquared < b.DistanceSquared
		}
		if a.Cell.X != b.Cell.X {
			return a.Cell.X < b.Cell.X
		}
		return a.Cell.Z < b.Cell.Z
	})
	available := make(map[domain.Cell]bool, len(free))
	for _, c := range free {
		available[c.Cell] = true
	}
	first := free[0].Cell
	delete(available, first)
	pending := []domain.Cell{first}
	var out []domain.Cell
	for len(pending) > 0 && len(out) < fishers {
		c := pending[0]
		pending = pending[1:]
		out = append(out, c)
		for _, d := range [4]domain.Cell{{X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0}} {
			n := domain.Cell{X: c.X + d.X, Z: c.Z + d.Z}
			if available[n] {
				delete(available, n)
				pending = append(pending, n)
			}
		}
	}
	return out
}

// FishingReachable is the region verdict: some fisher reaches the water, and an
// unzoned body also offers a footprint of one cell per fisher.
func FishingReachable(reaches, zoned domain.Fact[bool], footprint, fishers int) domain.Fact[bool] {
	if z, known := zoned.Value(); known && !z && footprint < fishers {
		return domain.Known(false)
	}
	return reaches
}

// FishingZoneState is a fishing zone touching a water body.
type FishingZoneState struct{ Allowed, DoForever, HasFishableCells bool }

// FishingDelivering is the observed zone state: some zone is allowed, fishes
// forever and has fishable cells.
func FishingDelivering(zones []FishingZoneState) bool {
	for _, z := range zones {
		if z.Allowed && z.DoForever && z.HasFishableCells {
			return true
		}
	}
	return false
}

// FishingResearchInputs are the raw native facts of the Fishing project.
type FishingResearchInputs struct {
	Researched                     domain.Fact[bool]
	BaseCost, Progress, CostFactor domain.Fact[float64]
	ResearcherSpeeds               []float64
	PointsPerWorkTick, Difficulty  domain.Fact[float64]
}

// FishingResearchLeadDays is zero once researched; otherwise the remaining
// project cost over the fastest researcher's daily points. It is unknown without
// a researcher or an unread input.
func FishingResearchLeadDays(in FishingResearchInputs) domain.Fact[float64] {
	researched, known := in.Researched.Value()
	if !known {
		return domain.Unknown[float64]()
	}
	if researched {
		return domain.Known(0.0)
	}
	var speed float64
	for _, s := range in.ResearcherSpeeds {
		speed = math.Max(speed, s)
	}
	base, bk := in.BaseCost.Value()
	progress, pk := in.Progress.Value()
	factor, fk := in.CostFactor.Value()
	points, tk := in.PointsPerWorkTick.Value()
	scale, sk := in.Difficulty.Value()
	if !bk || !pk || !fk || !tk || !sk || speed <= 0 {
		return domain.Unknown[float64]()
	}
	lead := math.Max(0, base-progress) * factor / (speed * points * FishingWorkTicksPerDay * scale)
	if math.IsNaN(lead) || math.IsInf(lead, 0) {
		return domain.Unknown[float64]()
	}
	return domain.Known(lead)
}

// Fisher is one available fisher's native rates.
type Fisher struct{ Yield, Speed float64 }

// FishingWorkCapacity is the raw nutrition per day the fishers can draw from a
// body: per fisher, a day of batches at the population's yield. It is unknown
// without a known nutrition per fish, yield curve value or batch duration.
func FishingWorkCapacity(fishers []Fisher, yieldCurve, nutritionPerFish, baseTicks domain.Fact[float64]) domain.Fact[float64] {
	curve, ck := yieldCurve.Value()
	nutrition, nk := nutritionPerFish.Value()
	base, bk := baseTicks.Value()
	if !ck || !nk || !bk || base <= 0 {
		return domain.Unknown[float64]()
	}
	var sum float64
	for _, f := range fishers {
		sum += FishingWorkTicksPerDay * math.Max(1, math.RoundToEven(curve*f.Yield)) * nutrition * f.Speed / base
	}
	if math.IsNaN(sum) || math.IsInf(sum, 0) {
		return domain.Unknown[float64]()
	}
	return domain.Known(sum)
}
