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

// Budget-scaled per-race herd cap (#1189): herdCapCeiling animals while the
// wealth budget has headroom, shrinking with the share of wealth the defense
// can hold when it is negative, never below herdCapFloor.
const (
	herdCapCeiling = 30
	herdCapFloor   = 6
)

// herdBudgetCap is the per-race cap for wealth budget headroom over colony
// wealth total: 30 at non-negative headroom, else
// clamp(floor(30 × (total + headroom) / total), 6, 30).
func herdBudgetCap(headroom, total float64) int64 {
	if headroom >= 0 || total <= 0 {
		return herdCapCeiling
	}
	return int64(math.Max(herdCapFloor, math.Floor(herdCapCeiling*(total+headroom)/total)))
}

// HerdFacts are the per-animal facts MaintainHerd sizes and culls by
// (#875). The census fills age, life expectancy, sickness, adulthood,
// precepts and tame danger natively; MeatNutrition, FeedPerDay and Product
// merge in from the colony food channels by animal ID. Unknown facts rank an
// animal as ordinary (not old, not sick, adult) and never bar a removal.
type HerdFacts struct {
	AgeYears, LifeExpectancy, ManhunterOnTameFail, MeatNutrition, FeedPerDay domain.Fact[float64]
	Sick, Adult, SlaughterBarred, Venerated                                  domain.Fact[bool]
	// Predator is RaceProps.predator; Product is true for a milk, wool,
	// chemfuel or egg producer.
	Predator, Product bool
}

// herdOldFraction of RaceProps.lifeExpectancy marks an animal as old.
const herdOldFraction = 0.8

// herdMalesPerFemales: one breeding male per this many females; males
// beyond max(1, ceil(females/5)) are excess.
const herdMalesPerFemales = 5

// herdStoredFeedDays spreads stored pen feed over one quadrum, the span the
// worst-quadrum pasture rate describes.
const herdStoredFeedDays = 15.0

// herdTameDanger is the RaceProps.manhunterOnTameFailChance at or above
// which a race is dangerous to tame (lynx 0.2; big cats and bears 0.3,
// warg 0.4, tiger 0.5, emu 1.0).
const herdTameDanger = 0.2

// herdDangerousAllowed are the dangerous races worth taming anyway: bears
// and wargs are strong fighters that repay the tame risk.
var herdDangerousAllowed = map[Resource]bool{"Bear_Grizzly": true, "Bear_Polar": true, "Warg": true}

func herdOld(a UpkeepAnimal) bool {
	age, ak := a.Herd.AgeYears.Value()
	life, lk := a.Herd.LifeExpectancy.Value()
	return ak && lk && life > 0 && age > herdOldFraction*life
}

func herdJuvenile(a UpkeepAnimal) bool {
	adult, known := a.Herd.Adult.Value()
	return known && !adult
}

// herdDangerous is a predator, or a race whose failed tame turns manhunter
// at least herdTameDanger of the time, outside herdDangerousAllowed.
func herdDangerous(a UpkeepAnimal) bool {
	chance, _ := a.Herd.ManhunterOnTameFail.Value()
	return (a.Herd.Predator || chance >= herdTameDanger) && !herdDangerousAllowed[a.Definition]
}

// HerdFor derives MaintainHerd's population band with no operator input.
// Every observed race's max is min(budget cap, feed cap); the budget cap is
// herdBudgetCap of the WealthBudget headroom (30 unless defense lags wealth). The feed
// cap applies to pen animals only: with pasture supply B = Σ worst-quadrum
// pasture + Σ stored feed / 15 days and demand D = Σ pen grazing demand, a
// race with n penned animals keeps floor(n × B/D). Pen capacity in RimWorld
// is this same nutrition balance (PenFoodCalculator), so it is not a separate
// term. Predators are never penned and eat meat: they stay under the stored
// food feed gate, not the pasture cap. B < D sets FeedShort, which lets
// juveniles be culled. Product races (milk, wool, chemfuel, eggs) get a
// breeding pair as their min. Unknown budget, wealth or pens leave that term out,
// so nothing is culled on a guess; FoodHerdPolicy adds food floors.
func HerdFor(animals domain.Fact[[]UpkeepAnimal], budget domain.Fact[float64], wealth domain.Fact[WealthFacts], pens domain.Fact[[]PenGrazing]) HerdPolicy {
	herd := HerdPolicy{PopulationMin: map[Resource]int64{}, PopulationMax: map[Resource]int64{}}
	rows, rk := animals.Value()
	if !rk {
		return herd
	}
	headroom, bk := budget.Value()
	if w, wk := wealth.Value(); bk && wk && finite(headroom) && finite(w.Total) {
		limit := herdBudgetCap(headroom, w.Total)
		for _, a := range rows {
			herd.PopulationMax[a.Definition] = limit
		}
	}
	for _, a := range rows {
		if a.Herd.Product {
			herd.PopulationMin[a.Definition] = herdPairSize
		}
	}
	ratio, known := herdPastureRatio(pens)
	if !known {
		return herd
	}
	herd.FeedShort = ratio < 1
	penned := map[Resource]int64{}
	for _, a := range rows {
		if pen, ok := a.RequiresPen.Value(); ok && pen && !a.Herd.Predator {
			penned[a.Definition]++
		}
	}
	for race, n := range penned {
		feed := int64(math.Floor(float64(n) * ratio))
		if cap, ok := herd.PopulationMax[race]; !ok || feed < cap {
			herd.PopulationMax[race] = feed
		}
	}
	return herd
}

// herdPastureRatio is pasture supply over pen demand; unknown when any pen
// fact is, or no pen has demand.
func herdPastureRatio(pens domain.Fact[[]PenGrazing]) (float64, bool) {
	rows, known := pens.Value()
	if !known {
		return 0, false
	}
	supply, demand := 0.0, 0.0
	for _, p := range rows {
		d, dk := p.DemandPerDay.Value()
		g, gk := p.PasturePerDay.Value()
		s, sk := p.StoredNutrition.Value()
		if !dk || !gk || !sk || !foodNumber(d) || !foodNumber(g) || !foodNumber(s) {
			return 0, false
		}
		supply += g + s/herdStoredFeedDays
		demand += d
	}
	if demand <= 0 {
		return 0, false
	}
	return supply / demand, true
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

// herdRemovalMethod is slaughter whenever native allows it and the player
// ideo neither venerates the race nor carries an AnimalSlaughter precept,
// else release when native allows that, else none. ok is false when a
// needed native fact is unknown.
func herdRemovalMethod(a UpkeepAnimal) (domain.HusbandryMethod, bool) {
	slaughter, sk := a.SafeToSlaughter.Value()
	if !sk {
		return "", false
	}
	if barred, _ := a.Herd.SlaughterBarred.Value(); slaughter && !barred {
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

// herdFeedPerMeat ranks feed cost per unit of meat; unknown costs rank last
// among their tier.
func herdFeedPerMeat(a UpkeepAnimal) float64 {
	feed, fk := a.Herd.FeedPerDay.Value()
	meat, mk := a.Herd.MeatNutrition.Value()
	if !fk || !mk || meat <= 0 {
		return -1
	}
	return feed / meat
}

// herdSurplusCandidates lists, per race in limits, the animals to remove so
// the kept count (not release/slaughter-designated) falls to the limit. Cull
// order: old (past 80% of life expectancy) or sick first; then males beyond
// one per five females; then untrained adults by highest feed per meat;
// then trained adults; juveniles only when juveniles is set (feed short).
// Ties go to the lowest ID. An animal whose removal would leave its race
// under a breeding pair of its own sex (or whose sex is unknown) is kept.
// Any tracked animal with an unknown designation or eligibility fact makes
// the result unknown.
func herdSurplusCandidates(rows []UpkeepAnimal, limits map[Resource]int64, juveniles bool) ([]herdRemoval, bool) {
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
		if method != "" && (juveniles || !herdJuvenile(a) || herdOld(a)) {
			eligible[a.Definition] = append(eligible[a.Definition], herdRemoval{a, method})
		}
	}
	var out []herdRemoval
	for race, limit := range limits {
		surplus := kept[race] - limit
		rows := eligible[race]
		excessMales := sexes[race]["Male"] - max(herdPairMales, (sexes[race]["Female"]+herdMalesPerFemales-1)/herdMalesPerFemales)
		tier := func(a UpkeepAnimal) int {
			sick, _ := a.Herd.Sick.Value()
			switch {
			case herdOld(a) || sick:
				return 0
			case herdJuvenile(a):
				return 4
			case herdTrained(a):
				return 3
			}
			return 2
		}
		sort.Slice(rows, func(i, j int) bool {
			ti, tj := tier(rows[i].animal), tier(rows[j].animal)
			if ti != tj {
				return ti < tj
			}
			if fi, fj := herdFeedPerMeat(rows[i].animal), herdFeedPerMeat(rows[j].animal); fi != fj {
				return fi > fj
			}
			return rows[i].animal.ID < rows[j].animal.ID
		})
		// Excess males jump ahead of every tier but old/sick, in the
		// order already ranked.
		excess := map[PawnID]bool{}
		for _, r := range rows {
			if a := r.animal; a.Gender == "Male" && tier(a) != 0 && int64(len(excess)) < excessMales {
				excess[a.ID] = true
			}
		}
		rank := func(a UpkeepAnimal) int {
			switch t := tier(a); {
			case t == 0:
				return 0
			case excess[a.ID]:
				return 1
			default:
				return t
			}
		}
		sort.SliceStable(rows, func(i, j int) bool { return rank(rows[i].animal) < rank(rows[j].animal) })
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
