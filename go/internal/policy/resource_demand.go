package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResourceKey identifies an output definition and, when required, its stuff.
// Empty Stuff in demand means any stuff; in stock it means an unstuffed item.
type ResourceKey struct {
	Def   Resource
	Stuff Resource
}

type ResourceQuantity struct {
	Key   ResourceKey
	Count int64
}

// ResourceDemand.Count is an unmet quantity in output, a stock target or a
// remaining bill-of-materials cost in input. Larger Priority is more important.
type ResourceDemand struct {
	Key      ResourceKey
	Count    int64
	Priority int
}

const maxAcquisitionCount int64 = 1_000_000_000

// ResourceDemandInput combines acquisition targets and economic stock floors.
// EconomicFloors is EconomicReserves's result; PlannedConstruction must exclude
// commitments already included in those floors and materials already delivered.
// Stock is a complete usable census: an absent key means zero only when known.
// Pending acquisition is not stock and cannot certify a satisfied shortage.
type ResourceDemandInput struct {
	Targets             []ResourceDemand
	EconomicFloors      map[string]int64
	PlannedConstruction []ResourceDemand
	Stock               domain.Fact[[]ResourceQuantity]
}

func validAcquisitionKey(k ResourceKey) bool {
	return validResource(k.Def) && (k.Stuff == "" || validResource(k.Stuff))
}

func validAcquisitionCount(n int64) bool { return n >= 0 && n <= maxAcquisitionCount }

func acquisitionKeyLess(a, b ResourceKey) bool {
	if a.Def != b.Def {
		return a.Def < b.Def
	}
	// Exact stuff consumes stock/yield before the any-stuff remainder.
	if (a.Stuff == "") != (b.Stuff == "") {
		return a.Stuff != ""
	}
	return a.Stuff < b.Stuff
}

func validDemand(d ResourceDemand) bool {
	return validAcquisitionKey(d.Key) && validAcquisitionCount(d.Count) && d.Priority >= 1 && d.Priority <= 100
}

// BuildResourceDemand takes the maximum overlapping stock target/floor, adds
// planned costs, then spends each stock unit once. Exact-stuff obligations take
// precedence over any-stuff obligations. Floors have priority 1; merged rows
// retain their highest priority. Output is canonical and contains no zero rows.
func BuildResourceDemand(in ResourceDemandInput) (domain.Fact[[]ResourceDemand], error) {
	unknown := domain.Unknown[[]ResourceDemand]()
	stock, known := in.Stock.Value()
	if len(in.Targets) > 4096 || len(in.EconomicFloors) > 4096 || len(in.PlannedConstruction) > 4096 || len(stock) > 4096 {
		return unknown, errors.New("resource demand input exceeds bound")
	}
	wanted := map[ResourceKey]ResourceDemand{}
	for _, d := range in.Targets {
		if !validDemand(d) {
			return unknown, errors.New("invalid resource target")
		}
		prior := wanted[d.Key]
		d.Count, d.Priority = max(d.Count, prior.Count), max(d.Priority, prior.Priority)
		wanted[d.Key] = d
	}
	for def, count := range in.EconomicFloors {
		key := ResourceKey{Def: Resource(def)}
		if !validAcquisitionKey(key) || !validAcquisitionCount(count) {
			return unknown, errors.New("invalid economic floor")
		}
		prior := wanted[key]
		wanted[key] = ResourceDemand{key, max(count, prior.Count), max(1, prior.Priority)}
	}
	// A definition-wide floor includes its stuff-specific stock targets, rather
	// than asking for both the floor and the same units a second time.
	exact := map[Resource]int64{}
	for key, d := range wanted {
		if key.Stuff != "" {
			exact[key.Def] += d.Count
		}
	}
	for key, d := range wanted {
		if key.Stuff == "" {
			d.Count = max(0, d.Count-exact[key.Def])
			wanted[key] = d
		}
	}
	for _, d := range in.PlannedConstruction {
		if !validDemand(d) {
			return unknown, errors.New("invalid planned construction cost")
		}
		prior := wanted[d.Key]
		d.Count += prior.Count
		d.Priority = max(d.Priority, prior.Priority)
		if !validAcquisitionCount(d.Count) {
			return unknown, errors.New("combined resource demand exceeds bound")
		}
		wanted[d.Key] = d
	}
	if len(wanted) > 4096 {
		return unknown, errors.New("combined resource demand exceeds row bound")
	}
	available := map[ResourceKey]int64{}
	for _, s := range stock {
		if !validAcquisitionKey(s.Key) || !validAcquisitionCount(s.Count) {
			return unknown, errors.New("invalid acquisition stock")
		}
		if _, exists := available[s.Key]; exists {
			return unknown, errors.New("duplicate acquisition stock")
		}
		available[s.Key] = s.Count
	}
	if !known {
		return unknown, nil
	}
	keys := make([]ResourceKey, 0, len(wanted))
	for key := range wanted {
		keys = append(keys, key)
	}
	sort.Slice(keys, func(i, j int) bool { return acquisitionKeyLess(keys[i], keys[j]) })
	remainingByDef := map[Resource]int64{}
	for key, count := range available {
		remainingByDef[key.Def] += count
	}
	var result []ResourceDemand
	for _, key := range keys {
		d := wanted[key]
		have := available[key]
		if key.Stuff == "" {
			have = remainingByDef[key.Def]
		}
		used := min(d.Count, have)
		d.Count -= used
		remainingByDef[key.Def] -= used
		if d.Count > 0 {
			result = append(result, d)
		}
	}
	return domain.Known(result), nil
}
