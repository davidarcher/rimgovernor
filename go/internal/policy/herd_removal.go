package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ReconcileHerdRemoval compares durable native flags with today's objectives.
// It has no controller-history dependency, so Manual edits and save/load use
// the same rule. Existing valid removals consume the budget before new work.
func ReconcileHerdRemoval(animals domain.Fact[[]UpkeepAnimal], herd HerdPolicy, food domain.Fact[FoodPlan]) HusbandryChoice {
	rows, known := animals.Value()
	if !known {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	rows = append([]UpkeepAnimal(nil), rows...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].ID < rows[j].ID })
	counts := map[Resource]int64{}
	for _, a := range rows {
		if _, ok := a.Release.Value(); !ok {
			return HusbandryChoice{Reason: HusbandryUnknown}
		}
		if _, ok := a.Slaughter.Value(); !ok {
			return HusbandryChoice{Reason: HusbandryUnknown}
		}
		counts[a.Definition]++
	}
	plan, foodKnown := food.Value()
	foodWanted := map[PawnID]bool{}
	for _, e := range plan.Portfolio {
		if e.Channel.Kind == FoodHunt && e.Decision == FoodPlanOpen {
			for _, a := range rows {
				if e.Channel.ID == "slaughter:"+string(a.ID) {
					foodWanted[a.ID] = true
				}
			}
		}
	}
	unknown := false
	for _, a := range rows {
		release, _ := a.Release.Value()
		slaughter, _ := a.Slaughter.Value()
		if !release && !slaughter {
			continue
		}
		floor := herd.PopulationMin[a.Definition]
		ceiling, capped := herd.PopulationMax[a.Definition]
		room := counts[a.Definition] > floor
		surplus := capped && counts[a.Definition] > max(floor, ceiling)
		keepRelease := release && !slaughter && herd.AllowRelease && surplus
		keepSlaughter := slaughter && !release && herd.AllowSlaughter && room && (surplus || foodWanted[a.ID])
		if release && !keepRelease {
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryCancelRelease}
		}
		if slaughter && !keepSlaughter {
			// Unknown food demand cannot revoke an otherwise allowed removal.
			if herd.AllowSlaughter && room && !foodKnown {
				unknown = true
			} else {
				return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryCancelSlaughter}
			}
		}
		counts[a.Definition]--
	}
	if unknown {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}
