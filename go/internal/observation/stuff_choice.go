package observation

import (
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// StuffCriterion is the stat a planner ranks the allowed stuffs of a def by
// (#1731). Every criterion reads the stuff options' stat values from the
// game's stat table, so no stuff is named in Go.
type StuffCriterion int

const (
	// CheapestStuff is the least market value of the adjusted cost list.
	CheapestStuff StuffCriterion = iota
	// MaxHitPointsPerCost is the most MaxHitPoints per unit of cost value.
	MaxHitPointsPerCost
	// LowestFlammability is the least Flammability.
	LowestFlammability
	// MaxRestEffectiveness is the most BedRestEffectiveness.
	MaxRestEffectiveness
)

func (c StuffCriterion) String() string {
	switch c {
	case CheapestStuff:
		return "cheapest"
	case MaxHitPointsPerCost:
		return "max hit points per cost"
	case LowestFlammability:
		return "lowest flammability"
	case MaxRestEffectiveness:
		return "max rest effectiveness"
	}
	return fmt.Sprintf("criterion %d", int(c))
}

// ErrNoStuffInStock is StuffChoice's refusal when no allowed stuff of the def
// is stocked to build it once: the planner decides what to do (hold, or wait
// for the native frame to collect material).
var ErrNoStuffInStock = errors.New("no allowed stuff of the definition is in stock")

// ErrNoStuffData is StuffChoice's refusal when the catalog row cannot state
// the choice: a stuffed def with no allowed stuff, a missing cost list or a
// criterion stat the game shows no value of.
var ErrNoStuffData = errors.New("the definition's rows do not state the stuff choice")

// StuffPrice is a def's chosen stuff with its adjusted cost list; Stuff is
// empty for a def not made from stuff.
type StuffPrice struct {
	Stuff string
	Costs []policy.Amount
}

// StuffChoice prices a def: a def not made from stuff is priced from its cost
// list directly; a stuffed def takes the allowed stuff that the criterion
// ranks best among those the stock covers once (nil stock covers none). A
// tie goes to the lower stuff name. The def's rows lacking what the choice
// needs is ErrNoStuffData; nothing stocked is ErrNoStuffInStock.
func (d PlanningDefinition) StuffChoice(criterion StuffCriterion, stock map[policy.Resource]int64) (StuffPrice, error) {
	return d.chooseStuff(criterion, func(option StuffOption) bool { return stocked(option.Costs, stock) })
}

// UnstockedStuffChoice is StuffChoice over every allowed stuff, stocked or
// not: the choice for a planner whose frame the game fills natively.
func (d PlanningDefinition) UnstockedStuffChoice(criterion StuffCriterion) (StuffPrice, error) {
	return d.chooseStuff(criterion, func(StuffOption) bool { return true })
}

func (d PlanningDefinition) chooseStuff(criterion StuffCriterion, allowed func(StuffOption) bool) (StuffPrice, error) {
	if len(d.StuffOptions) == 0 {
		costs, known := d.Costs.Value()
		if !known || d.Stuffed {
			return StuffPrice{}, fmt.Errorf("%w: %s has no stuff options and no cost list", ErrNoStuffData, d.Name)
		}
		return StuffPrice{Costs: slices.Clone(costs)}, nil
	}
	var best *StuffOption
	var bestScore float64
	for i := range d.StuffOptions {
		option := &d.StuffOptions[i]
		if !allowed(*option) {
			continue
		}
		score, err := d.score(criterion, *option)
		if err != nil {
			return StuffPrice{}, err
		}
		if best == nil || score > bestScore || score == bestScore && option.Stuff < best.Stuff {
			best, bestScore = option, score
		}
	}
	if best == nil {
		return StuffPrice{}, fmt.Errorf("%w: %s", ErrNoStuffInStock, d.Name)
	}
	return StuffPrice{Stuff: best.Stuff, Costs: slices.Clone(best.Costs)}, nil
}

// score is the option's rank under criterion, higher better.
func (d PlanningDefinition) score(criterion StuffCriterion, option StuffOption) (float64, error) {
	stat := func(name string) (float64, error) {
		value, shown := option.Stats[name]
		if !shown {
			return 0, fmt.Errorf("%w: %s made of %s shows no %s", ErrNoStuffData, d.Name, option.Stuff, name)
		}
		return value, nil
	}
	switch criterion {
	case CheapestStuff:
		return -option.Value, nil
	case MaxHitPointsPerCost:
		hp, err := stat(bridge.StatMaxHitPoints)
		if err != nil {
			return 0, err
		}
		if option.Value <= 0 {
			return 0, fmt.Errorf("%w: %s made of %s has no cost value", ErrNoStuffData, d.Name, option.Stuff)
		}
		return hp / option.Value, nil
	case LowestFlammability:
		flammability, err := stat(bridge.StatFlammability)
		return -flammability, err
	case MaxRestEffectiveness:
		return stat(bridge.StatBedRestEffectiveness)
	}
	return 0, fmt.Errorf("%w: unknown stuff criterion %d", ErrNoStuffData, int(criterion))
}

// stocked is whether the stock covers the cost list at least once.
func stocked(costs []policy.Amount, stock map[policy.Resource]int64) bool {
	if len(costs) == 0 {
		return false
	}
	for _, cost := range costs {
		if cost.Count > 0 && stock[cost.Resource] < cost.Count {
			return false
		}
	}
	return true
}

// MakeableFrom is whether the def can be built from stuff: any def can be
// built from no stuff when it is not made from stuff, and a stuffed def from
// each of its allowed stuffs.
func (d PlanningDefinition) MakeableFrom(stuff string) bool {
	if len(d.StuffOptions) == 0 {
		return stuff == ""
	}
	return slices.ContainsFunc(d.StuffOptions, func(o StuffOption) bool { return o.Stuff == stuff })
}

// CheapestCosts is the cost list of the cheapest way to build the def,
// whatever is in stock: the planning price of infrastructure a planner has
// not yet chosen material for; unknown when the rows cannot state it.
func (d PlanningDefinition) CheapestCosts() domain.Fact[[]policy.Amount] {
	price, err := d.UnstockedStuffChoice(CheapestStuff)
	if err != nil {
		return domain.Unknown[[]policy.Amount]()
	}
	return domain.Known(price.Costs)
}

// CostsMadeOf is the cost list of the def built from stuff (empty for a def
// not made from stuff); ok is false when the def has no such cost list.
func (d PlanningDefinition) CostsMadeOf(stuff string) ([]policy.Amount, bool) {
	if len(d.StuffOptions) == 0 {
		costs, known := d.Costs.Value()
		return costs, known && !d.Stuffed && stuff == ""
	}
	for _, option := range d.StuffOptions {
		if option.Stuff == stuff {
			return option.Costs, true
		}
	}
	return nil, false
}

// SharedStuff is the one stuff a set of defs built together is made from: the
// first def's cheapest allowed stuff, which every other def must be makeable
// from too (defs not made from stuff share the empty stuff). ok is false when
// they cannot share one.
func SharedStuff(first PlanningDefinition, others ...PlanningDefinition) (stuff string, ok bool) {
	if len(first.StuffOptions) > 0 {
		price, err := first.UnstockedStuffChoice(CheapestStuff)
		if err != nil {
			return "", false
		}
		stuff = price.Stuff
	}
	return stuff, first.MakeableFrom(stuff) && !slices.ContainsFunc(others, func(d PlanningDefinition) bool { return !d.MakeableFrom(stuff) })
}

// Definition is name's resolved planning row.
func (r ColonyProjection) Definition(name string) (PlanningDefinition, bool) {
	for _, d := range r.Definitions {
		if d.Name == name {
			return d, true
		}
	}
	return PlanningDefinition{}, false
}

// Stock is the colony's resource census; nil when it is unknown.
func (r ColonyProjection) Stock() (map[policy.Resource]int64, bool) {
	stock, known := r.Resources.Value()
	return stock, known
}

// BuildStuff is the stuff an ordinary placement builds the def from: the
// cheapest allowed stuff the colony stocks, else the cheapest allowed stuff
// whatever the stock (RimWorld places the frame and holds it natively for
// material, #602). A def not made from stuff, an unknown stock and a def the
// rows cannot choose for yield the empty stuff, the way an unknown stuff did.
func (r ColonyProjection) BuildStuff(name string) string {
	d, ok := r.Definition(name)
	if !ok || len(d.StuffOptions) == 0 {
		return ""
	}
	if stock, known := r.Stock(); known {
		if price, err := d.StuffChoice(CheapestStuff, stock); err == nil {
			return price.Stuff
		}
	}
	price, err := d.UnstockedStuffChoice(CheapestStuff)
	if err != nil {
		return ""
	}
	return price.Stuff
}
