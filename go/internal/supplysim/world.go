// Package supplysim is a day-by-day stock-flow simulator for colony supply
// (epic #2140). It models flows and constraints, never pawn behaviour: goods
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
}

// Demand returns the consumer's draw on the given day.
func (c Consumer) Demand(day int) float64 {
	return c.PerDay + c.GrowthPerDay*float64(day)
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
}

// Planner decides which sources to open or close.
type Planner interface {
	Plan(view WorldView) []Command
}

// PlannerFunc adapts a function to Planner.
type PlannerFunc func(WorldView) []Command

func (f PlannerFunc) Plan(v WorldView) []Command { return f(v) }
