package observation

import (
	"errors"
	"fmt"
	"math"
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
	// MaxDoorOpenSpeed is the most DoorOpenSpeed.
	MaxDoorOpenSpeed
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
	case MaxDoorOpenSpeed:
		return "max door open speed"
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
	return d.chooseStuff([]StuffCriterion{criterion}, func(option StuffOption) bool { return stocked(option.Costs, stock) })
}

// ErrFlammableStuff is FireproofStuff's refusal when even the least flammable
// allowed stuff of the def burns.
var ErrFlammableStuff = errors.New("every allowed stuff of the definition is flammable")

// FireproofStuff is the allowed stuff with the least Flammability, read from
// the game's stat table (#1814), and refused when that stuff still burns: a
// wall or door that must hold a fire in is never wood. A def not made from
// stuff has no stuff to choose and no flammability to read, so it is refused
// as ErrNoStuffData.
func (d PlanningDefinition) FireproofStuff() (string, error) {
	if len(d.StuffOptions) == 0 {
		return "", fmt.Errorf("%w: %s has no stuff options", ErrNoStuffData, d.Name)
	}
	price, err := d.UnstockedStuffChoice(LowestFlammability)
	if err != nil {
		return "", err
	}
	for _, option := range d.StuffOptions {
		if option.Stuff == price.Stuff && option.Stats[bridge.StatFlammability] > 0 {
			return "", fmt.Errorf("%w: %s, least flammable %s", ErrFlammableStuff, d.Name, price.Stuff)
		}
	}
	return price.Stuff, nil
}

// UnstockedStuffChoice is StuffChoice over every allowed stuff, stocked or
// not: the choice for a planner whose frame the game fills natively.
func (d PlanningDefinition) UnstockedStuffChoice(criterion StuffCriterion) (StuffPrice, error) {
	return d.chooseStuff([]StuffCriterion{criterion}, func(StuffOption) bool { return true })
}

// chooseStuff is the allowed option the criteria rank best, each criterion
// breaking the ties of the one before it, the lower stuff name the last.
func (d PlanningDefinition) chooseStuff(criteria []StuffCriterion, allowed func(StuffOption) bool) (StuffPrice, error) {
	if len(d.StuffOptions) == 0 {
		costs, known := d.Costs.Value()
		if !known || d.Stuffed {
			return StuffPrice{}, fmt.Errorf("%w: %s has no stuff options and no cost list", ErrNoStuffData, d.Name)
		}
		return StuffPrice{Costs: slices.Clone(costs)}, nil
	}
	var best *StuffOption
	var bestScores []float64
	for i := range d.StuffOptions {
		option := &d.StuffOptions[i]
		if !allowed(*option) {
			continue
		}
		scores := make([]float64, len(criteria))
		for j, criterion := range criteria {
			var err error
			if scores[j], err = d.score(criterion, *option); err != nil {
				return StuffPrice{}, err
			}
		}
		if best == nil {
			best, bestScores = option, scores
			continue
		}
		if c := slices.Compare(bestScores, scores); c < 0 || c == 0 && option.Stuff < best.Stuff {
			best, bestScores = option, scores
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
	case MaxDoorOpenSpeed:
		return stat(bridge.StatDoorOpenSpeed)
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

// BuildCriteria is how an ordinary placement ranks the def's allowed stuffs,
// read from the stats the game shows for the def made of each: a bed by rest
// effectiveness and an unpowered door by open speed first, then every def by
// hit points per cost; flammability breaks the ties. Beauty is no criterion:
// ranked blind to cost it would spend trade gold on tables. A stat any option
// lacks leaves its criterion out, and a def whose rows state none ranks by
// cheapest.
func (d PlanningDefinition) BuildCriteria() []StuffCriterion {
	shown := func(stat string) bool {
		return !slices.ContainsFunc(d.StuffOptions, func(o StuffOption) bool { _, ok := o.Stats[stat]; return !ok })
	}
	var criteria []StuffCriterion
	switch powered, _ := d.NeedsPower.Value(); {
	case shown(bridge.StatBedRestEffectiveness):
		criteria = append(criteria, MaxRestEffectiveness)
	case shown(bridge.StatDoorOpenSpeed) && !powered:
		criteria = append(criteria, MaxDoorOpenSpeed)
	}
	if shown(bridge.StatMaxHitPoints) && !slices.ContainsFunc(d.StuffOptions, func(o StuffOption) bool { return o.Value <= 0 }) {
		criteria = append(criteria, MaxHitPointsPerCost)
	}
	if shown(bridge.StatFlammability) {
		criteria = append(criteria, LowestFlammability)
	}
	if len(criteria) == 0 {
		return []StuffCriterion{CheapestStuff}
	}
	return criteria
}

// ordinaryStuff is the stuffs a def is planned from: the ordinary ones while
// the def allows one (Bioferrite is cheap and durable, but a colony never
// builds a bench, a bed or a wall from it), else any.
func (d PlanningDefinition) ordinaryStuff() func(StuffOption) bool {
	if slices.ContainsFunc(d.StuffOptions, func(o StuffOption) bool { return o.Common }) {
		return func(o StuffOption) bool { return o.Common }
	}
	return func(StuffOption) bool { return true }
}

// BuildStuff is the stuff an ordinary placement builds the def from: the best
// ranked allowed stuff the colony stocks (BuildCriteria), else the best ranked
// allowed stuff whatever the stock (RimWorld places the frame and holds it
// natively for material, #602). A def not made from stuff, an unknown stock and
// a def the rows cannot choose for yield the empty stuff.
func (r ColonyProjection) BuildStuff(name string) string {
	if stuff, ok := r.StockedBuildStuff(name); ok {
		return stuff
	}
	d, ok := r.Definition(name)
	if !ok || len(d.StuffOptions) == 0 {
		return ""
	}
	price, err := d.chooseStuff(d.BuildCriteria(), d.ordinaryStuff())
	if err != nil {
		return ""
	}
	return price.Stuff
}

// StockedBuildStuff is BuildStuff over the stuffs the colony stocks to build
// the def once; false when none is stocked or the def has no stuff to choose.
func (r ColonyProjection) StockedBuildStuff(name string) (string, bool) {
	d, ok := r.Definition(name)
	stock, known := r.Stock()
	if !ok || len(d.StuffOptions) == 0 || !known {
		return "", false
	}
	ordinary := d.ordinaryStuff()
	price, err := d.chooseStuff(d.BuildCriteria(), func(o StuffOption) bool { return ordinary(o) && stocked(o.Costs, stock) })
	return price.Stuff, err == nil
}

// BulkBuildStuff is BuildStuff for a def raised in bulk (a shell's walls): a
// stuff competes only when the stock covers `units` placements of it, the
// wood standing as trees counting toward wood (an unread acquisition census
// reads as wooded). With none covered it is the best ranked ordinary stuff
// whatever the stock, so the ring waits for it to be gathered: the stone
// blocks of a map short of wood, once they are quarried.
func (r ColonyProjection) BulkBuildStuff(name string, units int64) string {
	d, ok := r.Definition(name)
	if !ok || len(d.StuffOptions) == 0 {
		return ""
	}
	criteria, ordinary := d.BuildCriteria(), d.ordinaryStuff()
	if stock, known := r.Stock(); known {
		have := func(resource policy.Resource) float64 {
			held := float64(stock[resource])
			sources, read := r.Acquisition.Value()
			if resource != "WoodLog" {
				return held
			}
			if !read {
				return math.Inf(1)
			}
			for _, s := range sources {
				if s.Tree && s.Resource == string(resource) {
					held += s.Yield
				}
			}
			return held
		}
		covered := func(o StuffOption) bool {
			return len(o.Costs) > 0 && !slices.ContainsFunc(o.Costs, func(a policy.Amount) bool { return have(a.Resource) < float64(a.Count*units) })
		}
		if price, err := d.chooseStuff(criteria, func(o StuffOption) bool { return ordinary(o) && covered(o) }); err == nil {
			return price.Stuff
		}
	}
	price, err := d.chooseStuff(criteria, ordinary)
	if err != nil {
		return ""
	}
	return price.Stuff
}

// StuffUpgrade is whether replacing a built def made of have by want is an
// upgrade the colony can afford (#2111): want ranks strictly above have under
// the def's build criteria (equal ranks are no upgrade, so the tie-break by name
// never swaps a wall back and forth) and the stock covers one of it. False when
// either stuff is not an allowed option or the stock is unknown.
func (r ColonyProjection) StuffUpgrade(name, have, want string) bool {
	d, ok := r.Definition(name)
	stock, known := r.Stock()
	if !ok || !known || have == want {
		return false
	}
	var haveOption, wantOption *StuffOption
	for i := range d.StuffOptions {
		switch d.StuffOptions[i].Stuff {
		case have:
			haveOption = &d.StuffOptions[i]
		case want:
			wantOption = &d.StuffOptions[i]
		}
	}
	if haveOption == nil || wantOption == nil || !stocked(wantOption.Costs, stock) {
		return false
	}
	var haveScores, wantScores []float64
	for _, criterion := range d.BuildCriteria() {
		h, herr := d.score(criterion, *haveOption)
		w, werr := d.score(criterion, *wantOption)
		if herr != nil || werr != nil {
			return false
		}
		haveScores, wantScores = append(haveScores, h), append(wantScores, w)
	}
	return slices.Compare(haveScores, wantScores) < 0
}
