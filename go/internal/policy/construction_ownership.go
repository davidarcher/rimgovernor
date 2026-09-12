package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Claims are derived from completed autonomous journal actions. Callers of
// routine review cannot supply them or turn a player placement into ownership.
type ConstructionClaim struct {
	Plan     domain.PlanID
	Action   domain.ActionID
	Goal     domain.GoalID
	Identity domain.ConstructionIdentity
	Building domain.Building
}
type CurrentBuilding struct {
	ID       string
	Building domain.Building
}

// A completed causal identity must still be present with its original geometry.
// Missing/replaced/moved buildings are not rebound by location or definition.
type CurrentConstruction struct {
	Requested []string
	Buildings []CurrentBuilding
}

func OwnedConstructions(claims domain.Fact[[]ConstructionClaim], observed domain.Fact[CurrentConstruction]) (domain.Fact[[]ConstructionClaim], error) {
	unknown := domain.Unknown[[]ConstructionClaim]()
	invalid := errors.New("invalid construction ownership evidence")
	wanted, known := claims.Value()
	if !known {
		return unknown, nil
	}
	if len(wanted) > 256 {
		return unknown, invalid
	}
	actions := map[domain.ActionID]bool{}
	identities := map[string]bool{}
	for _, claim := range wanted {
		b := claim.Building
		if !foodID(string(claim.Plan)) || !foodID(string(claim.Action)) || !foodID(string(claim.Goal)) || claim.Identity.Validate() != nil || actions[claim.Action] || identities[claim.Identity.Current] {
			return unknown, invalid
		}
		if _, err := domain.NewBuilding(b.Definition(), b.Cell(), b.Rotation(), b.Stuff()); err != nil {
			return unknown, invalid
		}
		actions[claim.Action] = true
		identities[claim.Identity.Current] = true
	}
	if len(wanted) == 0 {
		return domain.Known([]ConstructionClaim{}), nil
	}
	census, known := observed.Value()
	if !known {
		return unknown, nil
	}
	if len(census.Requested) > 256 || len(census.Buildings) > 256 {
		return unknown, invalid
	}
	requested := map[string]bool{}
	for _, id := range census.Requested {
		if !foodID(id) || requested[id] {
			return unknown, invalid
		}
		requested[id] = true
	}
	for _, claim := range wanted {
		if !requested[claim.Identity.Current] {
			return unknown, nil
		}
	}
	current := map[string]domain.Building{}
	for _, row := range census.Buildings {
		if !foodID(row.ID) || !requested[row.ID] {
			return unknown, invalid
		}
		if _, exists := current[row.ID]; exists {
			return unknown, invalid
		}
		if _, err := domain.NewBuilding(row.Building.Definition(), row.Building.Cell(), row.Building.Rotation(), row.Building.Stuff()); err != nil {
			return unknown, invalid
		}
		current[row.ID] = row.Building
	}
	result := []ConstructionClaim{}
	for _, claim := range wanted {
		if building, ok := current[claim.Identity.Current]; ok && building == claim.Building {
			result = append(result, claim)
		}
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Identity.Current < result[j].Identity.Current })
	return domain.Known(result), nil
}
