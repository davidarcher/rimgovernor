package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Breeding pair MaintainHerd never culls below: one male and two females of
// each race, so a race can still recover by breeding after any removal.
const (
	herdPairMales   = 1
	herdPairFemales = 2
	herdPairSize    = herdPairMales + herdPairFemales
)

// Wealth-scaled per-race herd cap: herdCapCeiling animals at or below
// herdCapWealth colony wealth, shrinking in inverse proportion as wealth
// grows (raid points scale with wealth, and a bigger herd is more to feed and
// defend), never below herdCapFloor.
const (
	herdCapCeiling = 30
	herdCapFloor   = 6
	herdCapWealth  = 50000.0
)

// HerdWealthCap is the per-race population cap for colony wealth total:
// clamp(floor(30 * 50000 / max(total, 50000)), 6, 30). 50k wealth or less
// keeps 30 per race, 100k keeps 15, 250k and above keeps 6.
func HerdWealthCap(total float64) int64 {
	if math.IsNaN(total) || total < herdCapWealth {
		total = herdCapWealth
	}
	return int64(math.Max(herdCapFloor, math.Floor(herdCapCeiling*herdCapWealth/total)))
}

// HerdFor derives MaintainHerd's population band with no operator input:
// every observed race is capped at HerdWealthCap. Unknown wealth leaves every
// race uncapped, so nothing is culled for surplus on a guess. Floors come
// from FoodHerdPolicy.
func HerdFor(animals domain.Fact[[]UpkeepAnimal], wealth domain.Fact[WealthFacts]) HerdPolicy {
	herd := HerdPolicy{PopulationMin: map[Resource]int64{}, PopulationMax: map[Resource]int64{}}
	rows, rk := animals.Value()
	w, wk := wealth.Value()
	if !rk || !wk {
		return herd
	}
	limit := HerdWealthCap(w.Total)
	for _, a := range rows {
		herd.PopulationMax[a.Definition] = limit
	}
	return herd
}

// herdTrainables are the work trainables whose loss costs the colony labor:
// hauling, rescue and attack ("Release" in RimWorld's TrainableDefs).
var herdTrainables = map[string]bool{"Haul": true, "Rescue": true, "Release": true}

func herdTrained(a UpkeepAnimal) bool {
	for _, t := range a.Training {
		if learned, ok := t.Learned.Value(); ok && learned && herdTrainables[t.Def] {
			return true
		}
	}
	return false
}

type herdRemoval struct {
	animal UpkeepAnimal
	method domain.HusbandryMethod
}

// herdRemovalMethod is slaughter whenever native allows it, else release
// when native allows that (slaughter refused, e.g. by an ideoligion), else
// none. ok is false when a needed native fact is unknown.
func herdRemovalMethod(a UpkeepAnimal) (domain.HusbandryMethod, bool) {
	slaughter, sk := a.SafeToSlaughter.Value()
	if !sk {
		return "", false
	}
	if slaughter {
		return domain.HusbandrySlaughter, true
	}
	release, rk := a.SafeToRelease.Value()
	if !rk {
		return "", false
	}
	if release {
		return domain.HusbandryRelease, true
	}
	return "", true
}

// herdSurplusCandidates lists, per race in limits, the animals to remove
// so the kept count (not release/slaughter-designated) falls to the limit.
// Untrained animals go before trained ones, then lowest ID; an animal whose
// removal would leave its race under a breeding pair of its own sex (or
// whose sex is unknown) is kept. Any tracked animal with an unknown
// designation or eligibility fact makes the result unknown.
func herdSurplusCandidates(rows []UpkeepAnimal, limits map[Resource]int64) ([]herdRemoval, bool) {
	kept := map[Resource]int64{}
	sexes := map[Resource]map[string]int64{}
	eligible := map[Resource][]herdRemoval{}
	for _, a := range rows {
		if _, tracked := limits[a.Definition]; !tracked {
			continue
		}
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		if !rk || !sk {
			return nil, true
		}
		if release || slaughter {
			continue
		}
		kept[a.Definition]++
		if sexes[a.Definition] == nil {
			sexes[a.Definition] = map[string]int64{}
		}
		sexes[a.Definition][a.Gender]++
		method, ok := herdRemovalMethod(a)
		if !ok {
			return nil, true
		}
		if method != "" {
			eligible[a.Definition] = append(eligible[a.Definition], herdRemoval{a, method})
		}
	}
	var out []herdRemoval
	for race, limit := range limits {
		surplus := kept[race] - limit
		rows := eligible[race]
		sort.Slice(rows, func(i, j int) bool {
			ti, tj := herdTrained(rows[i].animal), herdTrained(rows[j].animal)
			if ti != tj {
				return !ti
			}
			return rows[i].animal.ID < rows[j].animal.ID
		})
		for _, r := range rows {
			if surplus <= 0 {
				break
			}
			switch g := r.animal.Gender; {
			case g == "Male" && sexes[race][g] > herdPairMales, g == "Female" && sexes[race][g] > herdPairFemales:
				sexes[race][g]--
			case g == "None": // asexual race: no pair to keep
			default:
				continue
			}
			out = append(out, r)
			surplus--
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].animal.ID < out[j].animal.ID })
	return out, false
}

// herdFoodLimits keeps max(floor, breeding pair) of every observed race: food
// slaughter never cuts below either.
func herdFoodLimits(rows []UpkeepAnimal, herd HerdPolicy) map[Resource]int64 {
	limits := map[Resource]int64{}
	for _, a := range rows {
		limits[a.Definition] = max(herd.PopulationMin[a.Definition], herdPairSize)
	}
	return limits
}
