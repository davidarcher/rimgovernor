package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// researchFacts reads every ResearchProjectDef row of the mirror as the
// project's static facts: the cost, the knowledge category, the prerequisites
// and the required bench are fields of the row, so the catalog carries no
// second copy of them.
func researchFacts(rows []*d.ResearchProjectDef) (map[string]policy.ResearchProjectFacts, error) {
	out := make(map[string]policy.ResearchProjectFacts, len(rows))
	for _, row := range rows {
		name := row.GetDefName()
		if row == nil || validID(name) != nil {
			return nil, contract("invalid catalog research project")
		}
		if _, exists := out[name]; exists {
			return nil, contract("duplicate catalog research project %s", name)
		}
		for _, list := range [][]string{row.GetPrerequisites(), row.GetHiddenPrerequisites()} {
			for _, prerequisite := range list {
				if validID(prerequisite) != nil {
					return nil, contract("invalid catalog research prerequisite")
				}
			}
		}
		cost := ResearchProjectCost(row)
		if row.GetKnowledgeCategory() != "" && (math.IsNaN(cost) || math.IsInf(cost, 0) || cost <= 0) {
			return nil, contract("catalog knowledge project %s lacks a finite cost", name)
		}
		// The mirror does not carry native ResearchProjectDef.hidden (an
		// anomaly codex state, not a static fact): every project starts as
		// not hidden and the research read marks a codex-hidden one from
		// its "hidden" lock reason. A project with no prerequisites is an
		// empty list, which is known-empty, not unread.
		out[name] = policy.ResearchProjectFacts{Name: policy.ResearchProjectID(name), Hidden: domain.Known(false), KnowledgeCategory: row.GetKnowledgeCategory(), Cost: cost,
			Prerequisites: domain.Known(toProjectIDs(row.GetPrerequisites())), HiddenPrerequisites: domain.Known(toProjectIDs(row.GetHiddenPrerequisites())), RequiredBuilding: row.GetRequiredResearchBuilding()}
	}
	return out, nil
}

// ResearchProjectCost is ResearchProjectDef.Cost: the base cost, or the
// knowledge cost of a project with none. The game scales it by a factor of the
// player's tech level for a project of a tech level; a knowledge project has
// none (tech level Undefined, factor 1), so this is its apparent cost.
func ResearchProjectCost(row *d.ResearchProjectDef) float64 {
	if row.GetBaseCost() > 0 {
		return float64(row.GetBaseCost())
	}
	return float64(row.GetKnowledgeCost())
}
