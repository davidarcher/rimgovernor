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
		if herdFertile(a) {
			sexes[a.Definition+"/"+Resource(a.Gender)]++
		}
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
		// A sterilized animal is no part of a breeding pair.
		fertile := herdFertile(a)
		pair := fertile && (a.Gender == "Male" && sexes[sex] <= herdPairMales || a.Gender == "Female" && sexes[sex] <= herdPairFemales)
		if herd.Retired[a.Definition] {
			floor, pair = 0, false
		}
		room := counts[a.Definition] > floor && !pair
		surplus := capped && counts[a.Definition] > max(floor, ceiling)
		keepRelease := release && !slaughter && surplus && !pair
		keepSlaughter := slaughter && !release && room && (surplus || foodWanted[a.ID])
		// An unknown cap (wealth unread) cannot revoke a surplus removal.
		if !capped && room && !keepSlaughter {
			unknown = true
			counts[a.Definition]--
			if fertile {
				sexes[sex]--
			}
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
		if fertile {
			sexes[sex]--
		}
	}
	if unknown {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}

// PrioritizeSlaughterChoice orders the best Animals-skilled capable handler
// (TamerFor) to slaughter the first animal whose slaughter designation
// stands, through the game's Prioritize order. An animal not designated,
// also marked for release, or bonded (or of unread bond), gets no order; no capable handler or an
// unread census or roster (nothing to act on yet) leaves the designation to native handlers.
func PrioritizeSlaughterChoice(animals domain.Fact[[]UpkeepAnimal], profiles domain.Fact[[]PawnProfile]) HusbandryChoice {
	rows, known := animals.Value()
	roster, rosterKnown := profiles.Value()
	if !known || !rosterKnown {
		return HusbandryChoice{Reason: HusbandryNoDeficit}
	}
	rows = append([]UpkeepAnimal(nil), rows...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].ID < rows[j].ID })
	handler, ok := TamerFor(roster, 0)
	for _, a := range rows {
		slaughter, sk := a.Slaughter.Value()
		release, rk := a.Release.Value()
		if !sk || !rk || !slaughter || release {
			continue
		}
		if bonded, bk := a.Bonded.Value(); !bk {
			return HusbandryChoice{Reason: HusbandryUnknown}
		} else if bonded {
			continue
		}
		if barred, bk := a.Herd.SlaughterBarred.Value(); !bk {
			return HusbandryChoice{Reason: HusbandryUnknown}
		} else if barred {
			continue
		}
		if !ok {
			break
		}
		return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryPrioritizeSlaughter, Handler: handler}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}
