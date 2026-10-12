package policy

import (
	"math"
	"sort"
)

// The pen food formula. The game scores a pen (PenFoodCalculator) as the sum,
// over the pen's cells that stand free of edifices, of what each cell's terrain
// grows for pasture animals per day in each quadrum (MapPastureNutritionCalculator),
// against the animals' summed daily consumption (SimplifiedPastureNutritionSimulator
// .NutritionConsumedPerDay); the pen feeds its animals in a quadrum where the sum
// of growth is at least the consumption. A pen that does not exist yet has no
// calculator, so native reads the per-cell term once (PastureTerrain, the worst
// of the four quadrums) and this file solves the same balance for the cell count.

// PastureTable is what one cell of each terrain def grows for grazing per day in
// the map's worst quadrum. A terrain absent from it, or at zero, grows nothing.
type PastureTable map[string]float64

// PerCell is the worst-quadrum nutrition per day one cell of terrain grows,
// false for a terrain that grows none.
func (t PastureTable) PerCell(terrain string) (float64, bool) {
	v, ok := t[terrain]
	return v, ok && v > 0 && !math.IsInf(v, 0)
}

// Terrains are the table's terrain defs that grow grazing, sorted.
func (t PastureTable) Terrains() []string {
	var out []string
	for name := range t {
		if _, grows := t.PerCell(name); grows {
			out = append(out, name)
		}
	}
	sort.Strings(out)
	return out
}

const (
	// penCellsFloor is the fewest cells a pen is sized to, whatever the herd
	// eats. Judgment: a pen of a few cells cannot be fenced with a flap and still
	// leave the animals room to turn.
	penCellsFloor = 16
	// penCellsCap bounds a pen's cells where the worst quadrum grows (almost)
	// nothing: grass cannot carry the herd then, and the hay fields
	// (HayNutritionNeed) cover the gap, so a pen is never sized past what the
	// plan can wall.
	penCellsCap = 900
)

// PenDemand is the nutrition per day the animals of ceilings eat: the adult
// feed of each race (the figure PenFoodCalculator's example animals use) times
// its count. False when a race's feed is unread.
func PenDemand(races AnimalRaceCatalog, ceilings map[Resource]int64) (float64, bool) {
	total := 0.0
	for def, n := range ceilings {
		if n <= 0 {
			continue
		}
		race, ok := races.Race(def)
		if !ok {
			return 0, false
		}
		feed, known := race.AdultFeedPerDay.Value()
		if !known || !foodNumber(feed) {
			return 0, false
		}
		total += feed * float64(n)
	}
	return total, true
}

// PenCells is the cells of a terrain that grows perCell nutrition per day that
// balance demand per day (the game's pen food sum solved for the cell count),
// bounded by penCellsFloor and penCellsCap. perCell at or below zero gives the
// cap, the pen the plan can wall.
func PenCells(demandPerDay, perCell float64) int {
	if !foodNumber(demandPerDay) || !foodNumber(perCell) || perCell <= 0 {
		return penCellsCap
	}
	return min(penCellsCap, max(penCellsFloor, int(math.Ceil(demandPerDay/perCell))))
}

// Weakest is the least nutrition per day a cell of any grazing terrain grows:
// the per-cell term a pen is sized with before its site, hence its terrain, is
// known, so a pen on any of them holds the herd. False when no terrain grows.
func (t PastureTable) Weakest() (float64, bool) {
	least, found := 0.0, false
	for _, name := range t.Terrains() {
		if v, _ := t.PerCell(name); !found || v < least {
			least, found = v, true
		}
	}
	return least, found
}
