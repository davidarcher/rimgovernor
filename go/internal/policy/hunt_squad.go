package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A squad hunt takes a group of nearby wild animals, mixed species
// allowed, with every drafted ranged colonist, at least SquadHuntMinGunners. A group is worth a squad at
// SquadHuntMinPrey animals, or at any single animal a lone hunter must not
// designate (Retaliates); the revenge cap (MaxHuntRevengeChance) binds only
// lone designation hunters.
const (
	SquadHuntMinPrey = 3
	// SquadHuntMinGunners is the smallest squad worth drafting; there is no upper bound.
	SquadHuntMinGunners = 3
	// squadHuntRadius is the linkage distance, in cells, that joins two animals into one group: a threshold, not a cap.
	squadHuntRadius = 12.0
	// squadSleepingWork scales the work of a sleeping animal: it stands still,
	// so night is a soft preference and never a gate.
	squadSleepingWork = 0.5
)

// SquadWeatherWork scales a squad hunt's work for the current weather's
// accuracy multiplier (WeatherDef.accuracyMultiplier, the factor on ranged
// hit chance): half of the extra shots the lower hit chance costs, so a hunt
// is mildly dearer and never gated. An unknown weather, and a multiplier of
// 1 or more, leave it unchanged.
func SquadWeatherWork(accuracy domain.Fact[float64]) float64 {
	if hit, known := accuracy.Value(); known && hit > 0 && hit < 1 {
		return 1 + (1/hit-1)/2
	}
	return 1
}

// SquadGunners counts the colonists a squad hunt may draft: a hunting weapon
// (WeaponDef.Hunts) and capable of hunting.
func SquadGunners(profiles []PawnProfile) int {
	n := 0
	for _, p := range profiles {
		if p.Hunts && p.Capable(WorkHunting, 0) {
			n++
		}
	}
	return n
}

// SquadPreyGroups clusters the standing, food-yielding squad prey (single
// linkage within squadHuntRadius) and keeps the groups worth a squad, each
// sorted by id, groups by first id.
func SquadPreyGroups(sources []AcquisitionSource) [][]AcquisitionSource {
	var prey []AcquisitionSource
	for _, s := range sources {
		if s.SquadPrey() && s.Food && !s.Downed {
			prey = append(prey, s)
		}
	}
	sort.Slice(prey, func(i, j int) bool { return prey[i].ID < prey[j].ID })
	group := make([]int, len(prey))
	for i := range group {
		group[i] = i
	}
	find := func(i int) int {
		for group[i] != i {
			group[i] = group[group[i]]
			i = group[i]
		}
		return i
	}
	for i := range prey {
		for j := i + 1; j < len(prey); j++ {
			if math.Hypot(float64(prey[i].Cell.X-prey[j].Cell.X), float64(prey[i].Cell.Z-prey[j].Cell.Z)) <= squadHuntRadius {
				group[find(j)] = find(i)
			}
		}
	}
	byRoot := map[int][]AcquisitionSource{}
	var roots []int
	for i, s := range prey {
		r := find(i)
		if _, seen := byRoot[r]; !seen {
			roots = append(roots, r)
		}
		byRoot[r] = append(byRoot[r], s)
	}
	var out [][]AcquisitionSource
	for _, r := range roots {
		g := byRoot[r]
		worth := len(g) >= SquadHuntMinPrey
		for _, s := range g {
			worth = worth || s.Retaliates()
		}
		if worth {
			out = append(out, g)
		}
	}
	return out
}

// HuntMode is how a hunt candidate executes: a lone hunter's designation, or a
// drafted formation (HuntRequest).
type HuntMode string

const (
	HuntLone      HuntMode = "lone"
	HuntFormation HuntMode = "formation"
)

// Mode is the hunt's mode: a formation is the candidate that names its prey.
func (c SupplyCandidate) Mode() HuntMode {
	if len(c.Prey) > 0 {
		return HuntFormation
	}
	return HuntLone
}

// HuntCandidates are the hunt channels of the offered food prey, one per
// animal or per group: a group worth a squad (SquadPreyGroups) is one
// formation channel `squad:<first id>`, every other animal a lone channel
// named by its id. A grouped animal is credited once, in its group's channel.
// With fewer than SquadHuntMinGunners gunners a formation is a Hold: it
// yields nothing and carries a needs_gunners term, the gunners it lacks, and
// the nutrition it would deliver. Bad weather (rain, snow, fog) mildly raises
// the work of a formation. A hunt yields the animal's meat and its Products.
func HuntCandidates(sources []AcquisitionSource, gunners int, weatherAccuracy domain.Fact[float64]) []SupplyCandidate {
	grouped := map[string]bool{}
	var out []SupplyCandidate
	for _, g := range SquadPreyGroups(sources) {
		for _, s := range g {
			grouped[s.ID] = true
		}
		out = append(out, huntChannel("squad:"+g[0].ID, g, true, SquadWeatherWork(weatherAccuracy), gunners))
	}
	for _, s := range sources {
		if s.Hunt && s.Food && !s.Tree && !grouped[s.ID] {
			out = append(out, huntChannel(s.ID, []AcquisitionSource{s}, false, 1, gunners))
		}
	}
	return out
}

// huntChannel prices the prey as one cycle's hunt: each animal's pursuit work
// (shortened by weapon reach, halved asleep, only collection when downed)
// scaled by weather, the worst revenge exposure among them as risk.
func huntChannel(id string, prey []AcquisitionSource, formation bool, weather float64, gunners int) SupplyCandidate {
	var nutrition, work, risk float64
	products := map[Resource]float64{}
	var defs []Resource
	c := FoodCandidate(CandidateHunt, id, domain.Unknown[float64]())
	c.LeadDays, c.Source = domain.Known(0.0), HuntSource
	designated := true
	for _, s := range prey {
		designated = designated && s.Designated
		w := FoodHuntWorkTicks / (1 + s.WeaponRange/25)
		if s.Downed {
			w = FoodForageWorkTicks
		}
		if s.Sleeping {
			w *= squadSleepingWork
		}
		nutrition += s.NutritionYield
		work += w * weather
		risk = math.Max(risk, s.HuntRevengeCost())
		for _, p := range s.Products {
			if _, seen := products[p.Def]; !seen {
				defs = append(defs, p.Def)
			}
			products[p.Def] += p.Amount
		}
		if formation {
			c.Prey = append(c.Prey, s.ID)
		}
	}
	c.State = FoodState(domain.Known(false), domain.Known(designated))
	c.Yields[0].PerDay, c.LaborPerDay = domain.Known(nutrition/FoodHuntCycleDays), domain.Known(work/FoodHuntCycleDays)
	// The animals in reach are all the candidate can deliver: a finite source.
	c.Yields[0].StockCap = domain.Known(int64(math.Ceil(nutrition)))
	for _, def := range defs {
		c.Yields = append(c.Yields, CandidateYield{Good: ResourceKey{Def: def}, PerDay: domain.Known(products[def] / FoodHuntCycleDays)})
	}
	c.Risk = []CandidateRisk{{CandidateRevenge, math.Min(1, risk)}}
	c.Terms = []CandidateTerm{{"estimated_cycle_days", FoodHuntCycleDays}, {"estimated_work_ticks", work}, {"prey", float64(len(prey))}, {"revenge_cost", risk}}
	if len(prey) == 1 {
		s := prey[0]
		c.Terms = append(c.Terms, CandidateTerm{"revenge_chance", s.RevengeChance}, CandidateTerm{"herd_size", float64(s.HerdSize)}, CandidateTerm{"weapon_range", s.WeaponRange})
	}
	if formation && gunners < SquadHuntMinGunners {
		c.Terms = append(c.Terms, CandidateTerm{"needs_gunners", float64(SquadHuntMinGunners - gunners)}, CandidateTerm{"held_nutrition_per_day", nutrition / FoodHuntCycleDays})
		c.Yields[0].PerDay, c.LaborPerDay, c.Yields = domain.Known(0.0), domain.Known(0.0), c.Yields[:1]
	}
	return c
}

// HuntRequest is the squad prey the food plan opens: the union of the open
// squad channels' prey, sorted. It is what raises the hunt origin of the
// ActiveCombat incident while nothing hostile stands.
func HuntRequest(plan domain.Fact[FoodPlan]) []domain.PawnID {
	p, known := plan.Value()
	if !known {
		return nil
	}
	var out []domain.PawnID
	for _, e := range p.Portfolio {
		if e.Channel.Kind == CandidateHunt && e.Decision == FoodPlanOpen {
			for _, id := range e.Channel.Prey {
				out = append(out, domain.PawnID(id))
			}
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}
