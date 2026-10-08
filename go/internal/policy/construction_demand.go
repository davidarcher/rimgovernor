package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StockReader is the one reader of usable resource stock: the item census, with
// WoodLog taken from the wood fact the latch reads so a floor and the latch
// agree.
type StockReader struct {
	Resources domain.Fact[[]Amount]
	Wood      domain.Fact[int64]
}

// Count is resource's usable units, unknown when the census (or, for WoodLog
// without a wood fact, the census) is unknown.
func (s StockReader) Count(resource Resource) domain.Fact[int64] {
	if resource == "WoodLog" {
		if n, known := s.Wood.Value(); known {
			return domain.Known(n)
		}
	}
	rows, known := s.Resources.Value()
	if !known {
		return domain.Unknown[int64]()
	}
	var n int64
	for _, a := range rows {
		if a.Resource == resource {
			n += a.Count
		}
	}
	return domain.Known(n)
}

// Units is Count with an unknown census read as zero.
func (s StockReader) Units(resource Resource) int64 {
	n, _ := s.Count(resource).Value()
	return n
}

// Census is the census with its WoodLog row taken from the wood fact. An
// unknown census stays unknown unless wood is the only target.
func (s StockReader) Census(targets map[Resource]int64) domain.Fact[[]Amount] {
	n, known := s.Wood.Value()
	if !known {
		return s.Resources
	}
	rows, rk := s.Resources.Value()
	if !rk {
		if _, only := targets["WoodLog"]; !only || len(targets) != 1 {
			return s.Resources
		}
	}
	out := make([]Amount, 0, len(rows)+1)
	for _, row := range rows {
		if row.Resource != "WoodLog" {
			out = append(out, row)
		}
	}
	return domain.Known(append(out, Amount{Resource: "WoodLog", Count: n}))
}

// AdmittedCost is one still-open action of an admitted method (a shelter
// shell is admitted without a stock check, #602) and the units of Resource
// its preview costs.
type AdmittedCost struct {
	Resource Resource
	Action   domain.ActionID
	Count    int64
}

// ConstructionDemandInput is every source of construction material demand.
type ConstructionDemandInput struct {
	Stock StockReader
	// Owed is the material standing blueprints and frames are still owed.
	Owed domain.Fact[map[Resource]int64]
	// Admitted are the open costs of admitted-but-unplaced methods.
	Admitted []AdmittedCost
	// WoodFloor is the wood latch's WoodLog floor, 0 while the latch is off.
	WoodFloor int64
}

// ConstructionDemand is the stock MaintainResource must reach for
// construction, per resource: a blueprint's owed material, the open costs of
// admitted methods (each action once) and the wood latch's floor, each
// counted where the stock falls short of it (the latch whenever it is on),
// merged by maximum. It is an absolute stock level; the supply plan measures
// its shortfall against the stock of the moment.
func ConstructionDemand(in ConstructionDemandInput) map[Resource]int64 {
	var out map[Resource]int64
	raise := func(resource Resource, need int64) {
		if need <= 0 || need <= out[resource] {
			return
		}
		if out == nil {
			out = map[Resource]int64{}
		}
		out[resource] = need
	}
	short := func(resource Resource, need int64) bool {
		have, known := in.Stock.Count(resource).Value()
		return known && need > have
	}
	if owed, known := in.Owed.Value(); known {
		for resource, n := range owed {
			if short(resource, n) {
				raise(resource, n)
			}
		}
	}
	for resource, need := range admittedNeeds(in.Admitted) {
		if short(resource, need) {
			raise(resource, need)
		}
	}
	raise("WoodLog", in.WoodFloor)
	return out
}

// admittedNeeds is the open cost of admitted methods per resource, each
// action once.
func admittedNeeds(admitted []AdmittedCost) map[Resource]int64 {
	costs := map[Resource]map[domain.ActionID]int64{}
	for _, c := range admitted {
		if c.Count <= 0 || c.Resource == "" {
			continue
		}
		if costs[c.Resource] == nil {
			costs[c.Resource] = map[domain.ActionID]int64{}
		}
		costs[c.Resource][c.Action] = max(costs[c.Resource][c.Action], c.Count)
	}
	out := make(map[Resource]int64, len(costs))
	for resource, actions := range costs {
		for _, n := range actions {
			out[resource] += n
		}
	}
	return out
}

// ConstructionDemandOf is ConstructionDemand over a review's facts.
func ConstructionDemandOf(f RoundsFacts, p RoundsPolicy, l RoundsLatches) map[Resource]int64 {
	return ConstructionDemand(ConstructionDemandInput{
		Stock:     StockReader{f.Resources, f.Wood},
		Owed:      f.ConstructionDeficit,
		Admitted:  f.Admitted,
		WoodFloor: woodLatchFloor(l.Wood, p),
	})
}

// woodLatchFloor is the WoodLog floor the wood latch asks for: WoodTarget
// while latched, 0 otherwise.
func woodLatchFloor(latched bool, p RoundsPolicy) int64 {
	if !latched {
		return 0
	}
	return p.WoodTarget
}
