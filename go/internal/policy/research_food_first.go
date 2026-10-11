package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// FoodFirstRunwayDays is the stored-food runway under which a map with no
// soil researches the food chain first: Hydroponics must run within about a
// month of the starting stock, and the research and its basins take days.
const FoodFirstRunwayDays = 45.0

// FoodFirstResearchRungs is the food chain a soilless colony walks first:
// Electricity and the Hydroponics it unlocks. It stays a judgment table like
// ArmorResearchRungs; the defs give the prerequisite, not when to want it.
var FoodFirstResearchRungs = []string{"Electricity", "Hydroponics"}

// NoSoil reports whether the observed planning window holds no soil: every
// cell's fertility is known and none is above zero. Any soil cell is
// Known(false); a window with no cell, or an unknown fertility and no soil
// seen, is unknown.
func NoSoil(cells []SiteCell) domain.Fact[bool] {
	unknown := false
	seen := false
	for _, c := range cells {
		fertility, known := c.Fertility.Value()
		if !known {
			unknown = true
			continue
		}
		seen = true
		if fertility > 0 {
			return domain.Known(false)
		}
	}
	if !seen || unknown {
		return domain.Unknown[bool]()
	}
	return domain.Known(true)
}

// FoodFirstOwed is whether the food chain leads the research ladder: no soil
// on the map and a stored-food runway under FoodFirstRunwayDays. Unknown
// either way is not owed.
func FoodFirstOwed(f RoundsFacts) bool {
	noSoil, soilKnown := f.NoSoil.Value()
	days, daysKnown := f.FoodDays.Value()
	return soilKnown && noSoil && daysKnown && days < FoodFirstRunwayDays
}

// FoodFirstLadder is ladder with FoodFirstResearchRungs at its head while
// foodFirst holds; rungs the ladder already names move up rather than repeat.
// An empty ladder (roadmap disabled) stays empty.
func FoodFirstLadder(ladder []string, foodFirst bool) []string {
	if !foodFirst || len(ladder) == 0 {
		return ladder
	}
	out := append([]string{}, FoodFirstResearchRungs...)
	for _, rung := range ladder {
		if rung != "Electricity" && rung != "Hydroponics" {
			out = append(out, rung)
		}
	}
	return out
}

// ResearchPolicy is p with its ResearchLadder extended for the colony: the
// armor rungs while a soldier role exists (ArmorResearchPolicy), then the
// food chain ahead of everything while a soilless colony's food runs short.
func ResearchPolicy(p RoundsPolicy, soldier, foodFirst bool) RoundsPolicy {
	p = ArmorResearchPolicy(p, soldier)
	p.ResearchLadder = FoodFirstLadder(p.ResearchLadder, foodFirst)
	return p
}
