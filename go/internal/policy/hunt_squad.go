package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A squad hunt (#1617) takes a group of nearby wild animals, mixed species
// allowed, with 3 to 4 drafted ranged colonists. A group is worth a squad at
// SquadHuntMinPrey animals, or at any single animal a lone hunter must not
// designate (Retaliates); the revenge cap (MaxHuntRevengeChance) binds only
// lone designation hunters.
const (
	SquadHuntMinPrey = 3
	// SquadHuntMinGunners and SquadHuntMaxGunners are the squad's size.
	SquadHuntMinGunners = 3
	SquadHuntMaxGunners = 4
	// squadHuntRadius links two animals into one group, in cells.
	squadHuntRadius = 12.0
	// squadSleepingWork scales the work of a sleeping animal: it stands still,
	// so night is a soft preference and never a gate.
	squadSleepingWork = 0.5
)

// SquadGunners counts the colonists a squad hunt may draft: ranged and
// capable of hunting.
func SquadGunners(profiles []PawnProfile) int {
	n := 0
	for _, p := range profiles {
		if p.Ranged && p.Capable(WorkHunting, 0) {
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

// SquadHunts are the food channels of the groups worth a squad when gunners
// can form one, and the sources left to designation hunting. A grouped
// animal is credited once, in its group's channel.
func SquadHunts(sources []AcquisitionSource, gunners int) ([]FoodChannel, []AcquisitionSource) {
	if gunners < SquadHuntMinGunners {
		return nil, sources
	}
	grouped := map[string]bool{}
	var channels []FoodChannel
	for _, g := range SquadPreyGroups(sources) {
		var nutrition, work, risk float64
		c := FoodChannel{Kind: FoodHunt, LeadDays: domain.Known(0.0), Open: domain.Known(false)}
		for _, s := range g {
			grouped[s.ID] = true
			nutrition += s.NutritionYield
			w := FoodHuntWorkTicks / (1 + s.WeaponRange/25)
			if s.Sleeping {
				w *= squadSleepingWork
			}
			work += w
			risk = math.Max(risk, s.HuntRevengeCost())
			c.Prey = append(c.Prey, s.ID)
		}
		c.ID = "squad:" + g[0].ID
		c.NutritionPerDay = domain.Known(nutrition / FoodHuntCycleDays)
		c.WorkPerDay = domain.Known(work / FoodHuntCycleDays)
		c.Risk = []FoodRisk{{FoodRevenge, math.Min(1, risk)}}
		c.Terms = []FoodPlanTerm{{"estimated_cycle_days", FoodHuntCycleDays}, {"estimated_work_ticks", work}, {"squad_prey", float64(len(g))}, {"revenge_cost", risk}}
		channels = append(channels, c)
	}
	rest := make([]AcquisitionSource, 0, len(sources))
	for _, s := range sources {
		if !grouped[s.ID] {
			rest = append(rest, s)
		}
	}
	return channels, rest
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
		if e.Channel.Kind == FoodHunt && e.Decision == FoodPlanOpen {
			for _, id := range e.Channel.Prey {
				out = append(out, domain.PawnID(id))
			}
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}
