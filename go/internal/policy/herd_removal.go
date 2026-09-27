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
	sexes := map[Resource]int64{}
	for _, a := range rows {
		if _, ok := a.Release.Value(); !ok {
			return HusbandryChoice{Reason: HusbandryUnknown}
		}
		if _, ok := a.Slaughter.Value(); !ok {
			return HusbandryChoice{Reason: HusbandryUnknown}
		}
		counts[a.Definition]++
		sexes[a.Definition+"/"+Resource(a.Gender)]++
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
		floor := max(herd.PopulationMin[a.Definition], herdPairSize)
		ceiling, capped := herd.PopulationMax[a.Definition]
		sex := a.Definition + "/" + Resource(a.Gender)
		pair := a.Gender == "Male" && sexes[sex] <= herdPairMales || a.Gender == "Female" && sexes[sex] <= herdPairFemales
		room := counts[a.Definition] > floor && !pair
		surplus := capped && counts[a.Definition] > max(floor, ceiling)
		keepRelease := release && !slaughter && surplus && !pair
		keepSlaughter := slaughter && !release && room && (surplus || foodWanted[a.ID])
		// An unknown cap (wealth unread) cannot revoke a surplus removal.
		if !capped && room && !keepSlaughter {
			unknown = true
			counts[a.Definition]--
			sexes[sex]--
			continue
		}
		if release && !keepRelease {
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryCancelRelease}
		}
		if slaughter && !keepSlaughter {
			// Unknown food demand cannot revoke an otherwise allowed removal.
			if room && !foodKnown {
				unknown = true
			} else {
				return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryCancelSlaughter}
			}
		}
		counts[a.Definition]--
		sexes[sex]--
	}
	if unknown {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}
