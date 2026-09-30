package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
)

// policyPrunes is the prune step that runs after the per-pawn policy
// planners (#1298): the bot owns every outfit, drug, food and reading policy
// and every allowed area (#1292, #719), so each one whose load id the
// planners did not assign (absent from keep) is deleted, player-made
// included. One prune per database with anything to delete, in database
// order.
func policyPrunes(p observation.Policies, keep map[string]bool) []domain.PolicyPrune {
	var r []domain.PolicyPrune
	add := func(db domain.PolicyDatabase, ids []string) {
		var drop []string
		for _, id := range ids {
			if id != "" && !keep[id] {
				drop = append(drop, id)
			}
		}
		if len(drop) == 0 {
			return
		}
		if v, err := domain.NewPolicyPrune(db, drop); err == nil {
			r = append(r, v)
		}
	}
	ids := func(rows []observation.PolicyEntry) []string {
		out := make([]string, 0, len(rows))
		for _, row := range rows {
			out = append(out, row.ID)
		}
		return out
	}
	add(domain.OutfitPolicies, ids(p.Outfit))
	add(domain.DrugPolicies, ids(p.Drug))
	add(domain.FoodPolicies, ids(p.Food))
	add(domain.ReadingPolicies, ids(p.Reading))
	areas := make([]string, 0, len(p.AllowedAreas))
	for _, a := range p.AllowedAreas {
		areas = append(areas, a.ID)
	}
	add(domain.AllowedAreas, areas)
	return r
}
