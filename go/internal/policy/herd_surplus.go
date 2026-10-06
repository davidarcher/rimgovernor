package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Breeding pair MaintainHerd never culls below: one male and two females of
// each race, so a race can still recover by breeding after any removal. Only
// fertile animals count toward it (herdFertile).
const (
	herdPairMales   = 1
	herdPairFemales = 2
	herdPairSize    = herdPairMales + herdPairFemales
)

// herdFertile is whether the animal can still breed: anything not read as
// sterilized, so a census without the fact counts every animal.
func herdFertile(a UpkeepAnimal) bool {
	sterilized, _ := a.Sterilized.Value()
	return !sterilized
}

// HerdFacts are the per-animal facts MaintainHerd sizes and culls by
// (#875). The census fills age, life expectancy, sickness, adulthood,
// veneration and tame danger natively; MeatNutrition, FeedPerDay and Product
// merge in from the colony food channels by animal ID; SlaughterBarred and
// EatingBarred merge in from the ideoligion (ApplyHerdPrecepts). Unknown age
// or sickness ranks an animal as ordinary (not old, not sick, adult); an
// unknown SlaughterBarred leaves removal, sale and slaughter unplanned, an
// unknown EatingBarred food slaughter.
type HerdFacts struct {
	AgeYears, LifeExpectancy, ManhunterOnTameFail, MeatNutrition, FeedPerDay domain.Fact[float64]
	Sick, Adult, SlaughterBarred, EatingBarred, Venerated                    domain.Fact[bool]
	// Predator is RaceProps.predator; Product is true for a milk, wool,
	// chemfuel or egg producer (no longer read by the herd plan; kept so
	// recorded snapshots still decode).
	Predator, Product bool
}

// herdOldFraction of RaceProps.lifeExpectancy marks an animal as old.
const herdOldFraction = 0.8

// herdMalesPerFemales: one breeding male per this many females; males
// beyond max(1, ceil(females/5)) are excess.
const herdMalesPerFemales = 5

// HerdLayer is the breeding rule of one egg-laying race whose eggs can be
// fertilized (#1898), derived from the catalog: HensPerRooster is the hens
// one rooster keeps laying fertilized eggs, from how often he mates
// (24/mateMtbHours a day) against the fertilized eggs one hen lays a day
// (count/layInterval, each mating fertilizing eggFertilizationCountMax of
// them). Unknown when any of those facts is.
type HerdLayer struct {
	HensPerRooster domain.Fact[float64]
}

// herdLayerOf is the layer rule of a race, false for a race that does not lay
// fertilizable eggs.
func herdLayerOf(race AnimalRace) (HerdLayer, bool) {
	for _, p := range race.Products {
		if p.Kind != "eggs" || p.FertilizedDef == "" {
			continue
		}
		mtb, mk := race.MateMtbHours.Value()
		count, ck := p.Amount.Value()
		interval, ik := p.IntervalDays.Value()
		if !mk || !ck || !ik || mtb <= 0 || count <= 0 || interval <= 0 || p.FertilizationCountMax <= 0 {
			return HerdLayer{HensPerRooster: domain.Unknown[float64]()}, true
		}
		matingsPerDay := 24 / mtb
		matingsPerHenDay := count / interval / float64(p.FertilizationCountMax)
		return HerdLayer{HensPerRooster: domain.Known(matingsPerDay / matingsPerHenDay)}, true
	}
	return HerdLayer{}, false
}

// malesKept is the fertile males a race keeps for its fertile females: keep
// is the count above which males are excess, floor the count no removal goes
// below. A layer race below its hen target (PopulationMin) keeps enough
// roosters to fertilize every egg and one at target, floor and keep alike;
// any other race keeps one per herdMalesPerFemales females above a pair
// floor of herdPairMales. known is false for a layer whose ratio is unknown.
func (h HerdPolicy) malesKept(race Resource, females int64) (keep, floor int64, known bool) {
	layer, isLayer := h.Layers[race]
	if !isLayer {
		return max(herdPairMales, (females+herdMalesPerFemales-1)/herdMalesPerFemales), herdPairMales, true
	}
	ratio, rk := layer.HensPerRooster.Value()
	if !rk {
		return 0, 0, false
	}
	keep = herdPairMales
	if females < h.PopulationMin[race] {
		keep = max(herdPairMales, int64(math.Ceil(float64(females)/ratio)))
	}
	return keep, keep, true
}

// herdStoredFeedDays spreads stored pen feed over one quadrum, the span the
// worst-quadrum pasture rate describes.
const herdStoredFeedDays = 15.0

// herdTameDanger is the RaceProps.manhunterOnTameFailChance at or above
// which a race is dangerous to tame (lynx 0.2; big cats and bears 0.3,
// warg 0.4, tiger 0.5, emu 1.0).
const herdTameDanger = 0.2

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
// at least herdTameDanger of the time. Such a race is tamed only as the war
// target (herdDangerousWar); every other job and the leveling race pass it by.
func herdDangerous(a UpkeepAnimal) bool {
	chance, _ := a.Herd.ManhunterOnTameFail.Value()
	return a.Herd.Predator || chance >= herdTameDanger
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

// herdRemovalMethod is slaughter whenever SafeToSlaughter allows it (it
// refuses a bonded animal) and the player ideo's precepts neither
// penalise nor forbid it (SlaughterBarred), else release when native allows
// that, else none. ok is false when a needed native fact is unknown.
func herdRemovalMethod(a UpkeepAnimal) (domain.HusbandryMethod, bool) {
	slaughter, sk := a.SafeToSlaughter().Value()
	if !sk {
		return "", false
	}
	barred, bk := a.Herd.SlaughterBarred.Value()
	if !bk {
		return "", false
	}
	if slaughter && !barred {
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
// under a breeding pair of its own sex (or whose sex is unknown) is kept, except in
// a retired race (the plan's, #1628), which keeps no pair.
// A sterilized animal is no part of the pair and is removed freely.
// Any tracked animal with an unknown designation or eligibility fact makes
// the result unknown.
func herdSurplusCandidates(rows []UpkeepAnimal, limits map[Resource]int64, juveniles bool, herd HerdPolicy) ([]herdRemoval, bool) {
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
		if herdFertile(a) {
			sexes[a.Definition][a.Gender]++
		}
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
		keepMales, floorMales, mk := herd.malesKept(race, sexes[race]["Female"])
		if !mk {
			return nil, true
		}
		excessMales := sexes[race]["Male"] - keepMales
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
			case herd.Retired[race], !herdFertile(r.animal): // no pair to keep
			case g == "Male" && sexes[race][g] > floorMales, g == "Female" && sexes[race][g] > herdPairFemales:
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

// herdFoodLimits keeps max(floor, breeding pair) of every observed race, none
// of a retired one: food slaughter never cuts below either.
func herdFoodLimits(rows []UpkeepAnimal, herd HerdPolicy) map[Resource]int64 {
	limits := map[Resource]int64{}
	for _, a := range rows {
		if herd.Retired[a.Definition] {
			limits[a.Definition] = 0
		} else {
			limits[a.Definition] = max(herd.PopulationMin[a.Definition], herdPairSize)
		}
	}
	return limits
}

// foodBarredOrUnknown reports whether slaughter or eating the animal is
// barred by the player ideo or the precept read is missing.
func (h HerdFacts) foodBarredOrUnknown() bool {
	slaughter, sk := h.SlaughterBarred.Value()
	eating, ek := h.EatingBarred.Value()
	return slaughter || eating || !sk || !ek
}
