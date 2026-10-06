package supplysim

import (
	"math/rand/v2"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Window is a repeating calendar window: active on days d where d mod Period
// is in [From, To). A zero Period is always active.
type Window struct{ Period, From, To int }

// Active reports whether the window is open on day.
func (w Window) Active(day int) bool {
	if w.Period <= 0 {
		return true
	}
	d := day % w.Period
	return d >= w.From && d < w.To
}

// Yield is an amount of a good per unit a source delivers (or, in Costs, per
// unit it consumes).
type Yield struct {
	Good    Good
	PerUnit float64
}

// CropField is a field of cells that grow only inside the Window, mature after
// GrowDays growing days and are harvested in a burst of up to HarvestCells per
// day. It replants while the source is open.
type CropField struct {
	Cells        float64
	GrowDays     int
	HarvestCells float64
	Window       Window

	Planted  float64 // cells in the ground
	Progress int     // growing days accrued
}

// Source delivers goods while opened. Capacity is units per day at full labor;
// Stock (when Finite) is the units left, regenerating Regen per day up to Max;
// deliveries pause while Stock/Max is below Floor. Labor is pawn ticks per day
// of work while delivering. Open and Stock are the initial state.
type Source struct {
	ID       string
	Yields   []Yield
	Costs    []Yield
	Open     bool
	Lead     int // days from opening to the first delivery
	Labor    float64
	Capacity float64
	Finite   bool
	Stock    float64
	Max      float64
	Regen    float64
	Floor    float64
	Window   Window
	// Restock refills Stock on the first day of each Window (trade arrival).
	Restock float64
	// Jitter varies daily capacity by up to +-Jitter, drawn from the world seed.
	Jitter float64
	Crop   *CropField

	openedDay   int
	removed     bool
	capScale    float64
	pausedUntil int
	delivered   float64
	last        float64
	rng         *rand.Rand
}

func (s Source) clone() Source {
	if s.Crop != nil {
		c := *s.Crop
		s.Crop = &c
	}
	s.Yields = append([]Yield(nil), s.Yields...)
	s.Costs = append([]Yield(nil), s.Costs...)
	return s
}

func (s *Source) delivering(day int) bool {
	return s.Open && !s.removed && day >= s.openedDay+s.Lead
}

func (s *Source) paused(day int) bool { return day < s.pausedUntil }

// available is the units deliverable on day at full labor, before costs.
func (s *Source) available(day int) float64 {
	if !s.delivering(day) {
		return 0
	}
	if c := s.Crop; c != nil {
		if c.Progress < c.GrowDays || c.Planted <= 0 {
			return 0
		}
		return min(c.Planted, c.HarvestCells) * s.capScale
	}
	if !s.Window.Active(day) {
		return 0
	}
	if s.Finite && s.Max > 0 && s.Stock < s.Floor*s.Max {
		return 0
	}
	v := s.Capacity * s.capScale
	if s.Jitter > 0 {
		v *= 1 + s.Jitter*(2*s.rng.Float64()-1)
	}
	if s.Finite {
		v = min(v, s.Stock)
	}
	return max(v, 0)
}

// take records taken units delivered.
func (s *Source) take(taken float64) {
	s.delivered += taken
	s.last = taken
	if s.Crop != nil {
		s.Crop.Planted -= taken
	} else if s.Finite {
		s.Stock -= taken
	}
}

// arrive restocks a trader on the first day of its window, before deliveries.
func (s *Source) arrive(day int) {
	if s.Restock > 0 && !s.removed && s.Window.Active(day) && (day == 0 || !s.Window.Active(day-1)) {
		s.Stock = s.Restock
	}
}

// grow runs regeneration and crop growth at the end of day.
func (s *Source) grow(day int) {
	if s.removed || s.paused(day) {
		return
	}
	if s.Max > 0 && s.Regen > 0 {
		s.Stock = min(s.Max, s.Stock+s.Regen)
	}
	if c := s.Crop; c != nil && s.Open {
		if c.Planted <= 1e-9 {
			c.Planted, c.Progress = 0, 0
			if c.Window.Active(day) {
				c.Planted = c.Cells
			}
		} else if c.Window.Active(day) && c.Progress < c.GrowDays {
			c.Progress++
		}
	}
}

func (s *Source) view(day int) SourceView {
	return SourceView{ID: s.ID, Open: s.Open, Delivering: s.delivering(day), Removed: s.removed,
		LeadLeft: max(0, s.openedDay+s.Lead-day), Stock: s.Stock, Max: s.Max, Last: s.last}
}

func newRNG(seed uint64, i int) *rand.Rand { return rand.New(rand.NewPCG(seed, uint64(i)+1)) }

// Per-unit constants for the constructors below.
const (
	FishingRegenFraction = 0.025 // of Max per day
	FishPerFisherDay     = 3.0
	FishLaborPerFisher   = 15000.0
	CropHarvestWork      = 400.0 // pawn ticks per cell harvested
	KillsPerHunterDay    = 1.0
	ProductWork          = 300.0 // pawn ticks per animal per day
)

// NewFishing is a fishing region: population regenerating 0.025 x max per
// day, paused below domain.FishingPopulationFloor, with catch limited by
// fishers.
func NewFishing(id string, population, max float64, fishers int, nutritionPerFish float64) Source {
	return Source{ID: id, Yields: []Yield{{Nutrition, nutritionPerFish}},
		Capacity: float64(fishers) * FishPerFisherDay, Labor: float64(fishers) * FishLaborPerFisher,
		Finite: true, Stock: population, Max: max, Regen: FishingRegenFraction * max,
		Floor: domain.FishingPopulationFloor}
}

// NewCrop is a crop field growing inside window; the first harvest comes
// growDays growing days after the first planting.
func NewCrop(id string, cells float64, growDays int, nutritionPerCell float64, window Window, harvestCellsPerDay float64) Source {
	return Source{ID: id, Yields: []Yield{{Nutrition, nutritionPerCell}}, Labor: harvestCellsPerDay * CropHarvestWork,
		Crop: &CropField{Cells: cells, GrowDays: growDays, HarvestCells: harvestCellsPerDay, Window: window}}
}

// NewHerd is a hunt herd: animals worth nutritionEach, killed by hunters and
// not regrowing. Danger in [0,1] cuts the kill rate and raises labor.
func NewHerd(id string, animals, nutritionEach float64, hunters int, danger float64) Source {
	capacity := float64(hunters) * KillsPerHunterDay * (1 - clamp01(danger))
	return Source{ID: id, Yields: []Yield{{Nutrition, nutritionEach}}, Capacity: capacity,
		Labor: capacity * HuntWorkTicks * (1 + clamp01(danger)), Finite: true, Stock: animals, Max: animals}
}

// NewForage is a seasonal forage patch regenerating regen units per day.
func NewForage(id string, stock, regen, nutritionPerUnit float64, window Window, foragers int) Source {
	return Source{ID: id, Yields: []Yield{{Nutrition, nutritionPerUnit}}, Capacity: float64(foragers) * 4,
		Labor: float64(foragers) * 4 * ForageWorkTicks, Finite: true, Stock: stock, Max: stock, Regen: regen, Window: window}
}

// NewProducts is animal products (milk, eggs) at perAnimal units per day.
func NewProducts(id string, animals, perAnimal, nutritionPerUnit float64) Source {
	return Source{ID: id, Yields: []Yield{{Nutrition, nutritionPerUnit}}, Capacity: animals * perAnimal, Labor: animals * ProductWork}
}

// NewTrade is a trader visiting inside window with restock units, paid for in
// Silver at price per unit.
func NewTrade(id string, window Window, restock, nutritionPerUnit, price float64) Source {
	return Source{ID: id, Yields: []Yield{{Nutrition, nutritionPerUnit}}, Costs: []Yield{{Silver, price}},
		Capacity: restock, Finite: true, Max: restock, Restock: restock, Window: window}
}
