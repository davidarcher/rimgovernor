package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Fuel runway. A refuelable building (a generator, a
// turret barrel) holds fuel units and burns them at its def's rate; the
// runway is the fuel it holds and the stock behind it over that burn, against
// ProjectionHorizonDays. The shortfall is a forward-projector domain and the
// items to stock ahead of it are a MaintainResource level in the Rounder's
// construction memory, like ClothingRunway.

// FuelBurn is the burn a refuelable def states (CompProperties_Refuelable),
// read from the catalog and never guessed. PerDay is fuelConsumptionRate, the
// fuel units a day the comp burns while it ticks (native divides it by 60000
// per tick) and UnitsPerItem the fuel units one item loads (fuelMultiplier).
// UseDriven (consumeFuelOnlyWhenUsed or externalTicking) is a comp that burns
// per use, a turret's per shot: it has no daily rate. InRain is a comp that
// also burns in rain. WhenPowered and Flickable gate the continuous burn:
// native ticks it only while the building is powered (WhenPowered) and
// switched on (Flickable). Difficulty scales UnitsPerItem for a comp with
// factorByDifficulty (the storyteller's maintenance cost factor, which no
// observation carries): UnitsPerItem is then the def's own multiplier.
type FuelBurn struct {
	PerDay       float64
	UnitsPerItem float64
	UseDriven    bool
	InRain       bool
	WhenPowered  bool
	Flickable    bool
	Difficulty   bool
}

// FuelConsumer is one refuelable building the power census shows: the fuel
// units it holds against its target, the facts its def's burn is gated on and
// the fuel definitions it accepts, in the census's order.
type FuelConsumer struct {
	ID, Definition string
	Fuel, Target   domain.Fact[float64]
	OutOfFuel      domain.Fact[bool]
	SwitchedOn     domain.Fact[bool]
	Powered        domain.Fact[bool]
	Burn           domain.Fact[FuelBurn]
	Fuels          []Resource
}

// FuelInputs are what the fuel runway is computed from.
type FuelInputs struct {
	Consumers domain.Fact[[]FuelConsumer]
	Stock     StockReader
}

// FuelResourceRunway is one fuel item's runway. Burn is the fuel units a day
// the continuous consumers burn, Need the items to hold ahead of them (the
// horizon's burn less what their tanks hold, plus the refill of the barrels
// already empty) and Short the items missing from stock.
type FuelResourceRunway struct {
	Resource      Resource
	Burn          float64
	Need, Stock   int64
	Short         int64
	ShortfallDays float64
}

// FuelProjection is the fuel domain of the forward projection.
// ShortfallDays is the largest per-resource shortfall.
type FuelProjection struct {
	Resources     []FuelResourceRunway
	ShortfallDays float64
	// Gaps lists the consumers that burn per use and so have no runway until
	// their barrel is empty.
	Gaps []string
}

// FuelRunway is the review's fuel runway: Needs the stock levels
// MaintainResource must reach, the projection beside them.
type FuelRunway struct {
	Needs      map[Resource]int64
	Projection domain.Fact[FuelProjection]
}

// perDay is the fuel units a day the building burns now, unknown for a comp
// that burns per use or in rain, and when a fact its gate reads is unknown.
func (c FuelConsumer) perDay() domain.Fact[float64] {
	burn, ok := c.Burn.Value()
	if !ok || burn.UseDriven || burn.InRain || !finite(burn.PerDay) || burn.PerDay < 0 {
		return domain.Unknown[float64]()
	}
	if burn.Flickable {
		on, known := c.SwitchedOn.Value()
		if !known {
			return domain.Unknown[float64]()
		}
		if !on {
			return domain.Known(0.0)
		}
	}
	if burn.WhenPowered {
		powered, known := c.Powered.Value()
		if !known {
			return domain.Unknown[float64]()
		}
		if !powered {
			return domain.Known(0.0)
		}
	}
	return domain.Known(burn.PerDay)
}

// fuelItem is the fuel definition the consumer's demand is named for: the
// first it accepts that is in stock, else its first (the rearm's choice).
func (c FuelConsumer) fuelItem(stock StockReader) (Resource, bool) {
	for _, r := range c.Fuels {
		if n, known := stock.Count(r).Value(); known && n > 0 {
			return r, true
		}
	}
	if len(c.Fuels) == 0 {
		return "", false
	}
	return c.Fuels[0], true
}

type fuelAccount struct {
	burn, tanks float64
	// continuous and empty are the items the two kinds of consumer need.
	continuous, empty float64
}

// PlanFuelRunway rolls every refuelable building forward over
// ProjectionHorizonDays. A building that burns at a known daily rate needs
// the horizon's burn less its tank; one already empty needs its refill
// (runway zero, whatever its rate). A building that burns per use and still
// holds fuel has no runway and is listed in Gaps. An unread census or stock,
// or a building whose fuel, rate gate, fuel item or unit conversion is
// unknown, makes the projection unknown: its demand is left out, never
// defaulted. The demand is an absolute stock level, counted only where the
// stock falls short of it.
func PlanFuelRunway(in FuelInputs) FuelRunway {
	consumers, known := in.Consumers.Value()
	if !known {
		return FuelRunway{Projection: domain.Unknown[FuelProjection]()}
	}
	accounts := map[Resource]*fuelAccount{}
	units := map[Resource]float64{}
	complete := true
	var gaps []string
	for _, c := range consumers {
		burn, bk := c.Burn.Value()
		fuel, fk := c.Fuel.Value()
		item, ik := c.fuelItem(in.Stock)
		if !bk || !fk || !ik || !finite(fuel) || !(burn.UnitsPerItem > 0) {
			complete = false
			continue
		}
		empty, ek := c.OutOfFuel.Value()
		perDay := c.perDay()
		rate, rk := perDay.Value()
		var need float64
		switch {
		case rk:
			need = math.Max(0, rate*ProjectionHorizonDays-fuel)
		case ek && empty:
			target, tk := c.Target.Value()
			if !tk || !finite(target) {
				complete = false
				continue
			}
			need = math.Max(0, target-fuel)
		case burn.UseDriven:
			gaps = append(gaps, c.ID)
			continue
		default:
			complete = false
			continue
		}
		a := accounts[item]
		if a == nil {
			a = &fuelAccount{}
			accounts[item] = a
		}
		units[item] = burn.UnitsPerItem
		if rk {
			a.burn += rate
			a.tanks += fuel
			a.continuous += need / burn.UnitsPerItem
		} else {
			a.empty += need / burn.UnitsPerItem
		}
	}
	out := FuelRunway{}
	projection := FuelProjection{Gaps: gaps}
	slices.Sort(projection.Gaps)
	stockKnown := true
	for item, a := range accounts {
		have, sk := in.Stock.Count(item).Value()
		if !sk {
			stockKnown = false
			continue
		}
		need := int64(math.Ceil(a.continuous + a.empty))
		row := FuelResourceRunway{Resource: item, Burn: a.burn, Need: min(need, maxResourceTarget), Stock: have}
		if need > have {
			row.Short = need - have
			row.ShortfallDays = ProjectionHorizonDays
			if a.empty == 0 && a.burn > 0 {
				row.ShortfallDays = math.Min(ProjectionHorizonDays, float64(row.Short)*units[item]/a.burn)
			}
			if out.Needs == nil {
				out.Needs = map[Resource]int64{}
			}
			out.Needs[item] = row.Need
		}
		projection.ShortfallDays = math.Max(projection.ShortfallDays, row.ShortfallDays)
		projection.Resources = append(projection.Resources, row)
	}
	sort.Slice(projection.Resources, func(i, j int) bool { return projection.Resources[i].Resource < projection.Resources[j].Resource })
	if !complete || !stockKnown {
		out.Projection = domain.Unknown[FuelProjection]()
		return out
	}
	out.Projection = domain.Known(projection)
	return out
}

// FuelRunway is PlanFuelRunway of the review's facts.
func (f RoundsFacts) FuelRunway() FuelRunway {
	return PlanFuelRunway(FuelInputs{Consumers: f.Fuel, Stock: StockReader{Resources: f.Resources, Wood: f.Wood}})
}
