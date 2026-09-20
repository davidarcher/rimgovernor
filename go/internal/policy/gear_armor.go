package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ArmorResearchRungs is the armor ladder a colony with a soldier walks
// right after Electricity (#470): the simple helmet's smithy, the tailoring
// bench flak cloth needs, flak armor itself, then shield belts.
var ArmorResearchRungs = []string{"Smithing", "ComplexClothing", "FlakArmor", "Shields"}

// ArmorResearchLadder is ladder with ArmorResearchRungs spliced in directly
// after Electricity (at the front when the ladder has no Electricity rung)
// while a soldier role exists; rungs the ladder already names move up
// rather than repeat. Without a soldier the ladder is returned as is.
func ArmorResearchLadder(ladder []string, soldier bool) []string {
	if !soldier {
		return ladder
	}
	armor := map[string]bool{}
	for _, rung := range ArmorResearchRungs {
		armor[rung] = true
	}
	out := []string{}
	inserted := false
	insert := func() {
		if !inserted {
			out = append(out, ArmorResearchRungs...)
			inserted = true
		}
	}
	for _, rung := range ladder {
		if armor[rung] {
			continue
		}
		out = append(out, rung)
		if rung == "Electricity" {
			insert()
		}
	}
	if !inserted {
		out = append(append([]string{}, ArmorResearchRungs...), out...)
	}
	return out
}

// ArmorResearchPolicy is p with its ResearchLadder extended by
// ArmorResearchLadder; an empty ladder (roadmap disabled) stays empty.
func ArmorResearchPolicy(p RoutinePolicy, soldier bool) RoutinePolicy {
	if len(p.ResearchLadder) == 0 {
		return p
	}
	p.ResearchLadder = ArmorResearchLadder(p.ResearchLadder, soldier)
	return p
}

// GearSoldierPresent reports whether the gear census derives a soldier role
// for any pawn (its apparel-policy role, or its loadout model's when one is
// supplied). Unknown census: no soldier.
func GearSoldierPresent(gear domain.Fact[GearObservation]) bool {
	v, known := gear.Value()
	if !known {
		return false
	}
	for _, p := range v.Pawns {
		if model, ok := p.LoadoutModel.Value(); ok && DeriveGearRole(model.Role) == GearSoldier {
			return true
		}
		if state, ok := p.Policy.Value(); ok && DeriveGearRole(state.Role) == GearSoldier {
			return true
		}
	}
	return false
}

// gearAvailable is the ingredient stock a gear bill may spend: the known
// supply census less each MaintainResource reserve and concurrent hold, and
// nothing of a resource whose spending rule is not Allow. Known reports which
// resources the census measured at all.
func gearAvailable(stock []Stock, rules []ResourceRule, holds []Amount) (available map[Resource]int64, known map[Resource]bool) {
	available = map[Resource]int64{}
	known = map[Resource]bool{}
	for _, s := range stock {
		if n, ok := s.Available.Value(); ok {
			available[s.Resource] = n
			known[s.Resource] = true
		}
	}
	for _, rule := range rules {
		if rule.Spending != Allow {
			available[rule.Resource] = 0
			known[rule.Resource] = true
		} else {
			available[rule.Resource] = max(0, available[rule.Resource]-rule.Reserve)
		}
	}
	for _, hold := range holds {
		available[hold.Resource] = max(0, available[hold.Resource]-hold.Count)
	}
	return available, known
}

// GearMaterialBudget is the loadout model's Budget: what each measured
// material can fund after MaintainResource reserves and holds, the same
// floor food bills honour (#470). Unmeasured resources are absent, which the
// model treats as unfunded.
func GearMaterialBudget(stock []Stock, rules []ResourceRule, holds []Amount) []Amount {
	available, known := gearAvailable(stock, rules, holds)
	out := []Amount{}
	for resource := range known {
		out = append(out, Amount{resource, available[resource]})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Resource < out[j].Resource })
	return out
}

// gearFunded reports whether budget covers every ingredient of o. A nil
// budget is unbudgeted (the caller supplied no census) and funds everything;
// an option without ingredients costs nothing.
func gearFunded(budget []Amount, o GearOption) bool {
	if budget == nil {
		return true
	}
	for _, need := range o.Ingredients {
		funded := false
		for _, have := range budget {
			funded = funded || have.Resource == need.Resource && have.Count >= need.Count
		}
		if !funded {
			return false
		}
	}
	return true
}
