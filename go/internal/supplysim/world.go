// Package supplysim is a day-by-day stock-flow simulator for colony supply.
// It models flows and constraints, never pawn behaviour: goods
// have stock, consumers draw them down, sources deliver while opened and within
// a labor budget, and shocks change capacity or destroy stock. A Planner opens
// and closes sources each day; Run returns a deterministic Report.
//
// The package imports only domain, so planner adapters live in their own test
// packages. Goods are plain strings so a resource world can register more.
package supplysim

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Good names a stocked good ("Nutrition", later "WoodLog", "Steel").
type Good string

const Nutrition Good = "Nutrition"

// Silver is the trade currency good.
const Silver Good = "Silver"

// LaborPerWorkerDay is the planning labor per worker per game day in pawn
// ticks, as in buildingruntime/rounds_food_plan.go (eight hours of a day).
const LaborPerWorkerDay = 20000.0

// Runway forecast constants from policy/resource_runway.go.
const (
	ForecastHistoryDays = 15
	ForecastHorizonDays = 5.0
)

// Work estimates per unit in pawn ticks, from policy/food_plan.go.
const (
	ForageWorkTicks = 2500.0
	HuntWorkTicks   = 7500.0
)

// Consumer draws PerDay of a good each day, growing linearly by GrowthPerDay.
type Consumer struct {
	Name         string
	Good         Good
	PerDay       float64
	GrowthPerDay float64
	// WindowFactor multiplies demand while Window is active (winter wood); it
	// applies only with WindowFactor > 0 and a Window.Period > 0.
	Window       Window
	WindowFactor float64
}

// Demand returns the consumer's draw on the given day.
func (c Consumer) Demand(day int) float64 {
	d := c.PerDay + c.GrowthPerDay*float64(day)
	if c.WindowFactor > 0 && c.Window.Period > 0 && c.Window.Active(day) {
		d *= c.WindowFactor
	}
	return d
}

// World is the simulator input. Run works on a copy; it never mutates it.
type World struct {
	Seed      uint64
	Workers   int
	Stock     map[Good]float64
	Consumers []Consumer
	Sources   []Source
	Shocks    []Shock
	// Spoilage is the fraction of a good's stock lost per day.
	Spoilage map[Good]float64
	// Power is the grid supply shared by sources with a PowerDraw.
	Power float64
	// Capacity is the stock ceiling per good; absent means unbounded.
	Capacity map[Good]float64
	// Floors are latched stock floors a planner reads (MaintainResource).
	Floors []Floor
	// Builds are construction shortfall edges.
	Builds []Build
}

// Floor is a latched stock floor: it latches when stock falls below Min and
// asks for Target until stock exceeds Target. Max is the ceiling above which
// the good is over-stocked.
type Floor struct {
	Good             Good
	Min, Target, Max float64
}

// Build is an open construction action: from Day it draws Costs from stock
// each day until paid in full, and is visible to the planner while open.
type Build struct {
	Name  string
	Day   int
	Costs []Yield
}

type buildState struct {
	Build
	left map[Good]float64
	done bool
}

type surge struct {
	factor float64
	until  int
}

// FloorView is a floor's state at the start of a day.
type FloorView struct {
	Good             Good
	Latched          bool
	Ask              float64 // Target - stock while latched
	Over             bool    // stock above Max
	Min, Target, Max float64
}

// BuildView is an open build's remaining cost.
type BuildView struct {
	Name string
	Left map[Good]float64
}

// Forecast is the runway forecast for a good over the last
// ForecastHistoryDays of consumption, as policy.ForecastResourceRunway.
type Forecast struct {
	ConsumptionPerDay float64
	DaysLeft          float64 // stock above the floor reserve plus unopened ore; -1 when no consumption
	Deficit           bool
}

// LaborBudget is the pawn ticks available per day.
func (w World) LaborBudget() float64 { return float64(w.Workers) * LaborPerWorkerDay }

// CommandKind is what a planner asks of a source.
type CommandKind string

const (
	Open  CommandKind = "Open"
	Close CommandKind = "Close"
)

// Command opens or closes one source. Opening restarts its lead.
type Command struct {
	Kind   CommandKind
	Source string
}

// SourceView is what a planner sees of a source.
type SourceView struct {
	ID         string
	Open       bool
	Delivering bool // open, past its lead and not removed
	Removed    bool
	LeadLeft   int
	Stock      float64
	Max        float64
	Last       float64 // units delivered on the previous day
	// Rate is the units the source could deliver today at full labor (a
	// crop: its harvest burst), after shocks; zero outside its window.
	Rate float64
	// Crop marks a crop field; GrowLeft is its remaining growing days.
	Crop     bool
	GrowLeft int
}

// WorldView is the planner's read of the world at the start of a day.
type WorldView struct {
	Day         int
	Tick        domain.Tick
	Stock       map[Good]float64
	Demand      map[Good]float64 // today's draw per good
	Runway      map[Good]float64 // days of stock at today's demand
	LaborBudget float64
	Sources     []SourceView
	Threat      bool
	Floors      []FloorView
	Builds      []BuildView
	// Forecast is present for floored goods once a full day of history exists.
	Forecast map[Good]Forecast
}

// Planner decides which sources to open or close.
type Planner interface {
	Plan(view WorldView) []Command
}

// PlannerFunc adapts a function to Planner.
type PlannerFunc func(WorldView) []Command

func (f PlannerFunc) Plan(v WorldView) []Command { return f(v) }
