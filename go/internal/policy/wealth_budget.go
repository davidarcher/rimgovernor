package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// wealthBudgetMinRaidPoints is the storyteller's raid-point floor (vanilla
// minimum, 35): a tiny reading never makes the budget divide by a vanishing
// threat.
const wealthBudgetMinRaidPoints = 35.0

// WealthBudget is the wealth headroom the colony's defense affords (#1189).
// Raid points scale with wealth, so the wealth the defense capacity can hold
// is wealth × capacity / raidPoints; the headroom is that minus wealth:
// positive means wealth may grow by that much, negative means shed it. Raid
// points are floored at 35. Any unknown, non-finite or negative input leaves
// the budget unknown.
func WealthBudget(raidPoints, capacity domain.Fact[float64], wealth domain.Fact[WealthFacts]) domain.Fact[float64] {
	points, pk := raidPoints.Value()
	strength, ck := capacity.Value()
	w, wk := wealth.Value()
	if !pk || !ck || !wk || !finite(points) || !finite(strength) || !finite(w.Total) || points < 0 || strength < 0 || w.Total < 0 {
		return domain.Unknown[float64]()
	}
	points = max(points, wealthBudgetMinRaidPoints)
	return domain.Known(w.Total * (strength - points) / points)
}

// WealthBudget is the headroom from f's raid points, defense capacity and
// wealth.
func (f RoundsFacts) WealthBudget() domain.Fact[float64] {
	return WealthBudget(f.RaidPoints, f.DefenseCapacity, f.Wealth)
}
