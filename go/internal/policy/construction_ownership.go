package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Journal claims retain completed autonomous construction provenance. Planning
// ownership may also come from the current player-faction census; those rows
// carry Identity.Current, Building and Cells, without invented action provenance.
type ConstructionClaim struct {
	Plan     domain.PlanID
	Action   domain.ActionID
	Goal     domain.GoalID
	Identity domain.ConstructionIdentity
	Building domain.Building
	Cells    []domain.Cell
}
type CurrentBuilding struct {
	ID       string
	Building domain.Building
	Cells    []domain.Cell
}

// CurrentConstruction distinguishes a colony census from an exact refresh.
// Exact refreshes retain causal identity only with the original geometry.
type CurrentConstruction struct {
	// Colony marks a complete built/artificial/player-only census. Requested
	// otherwise scopes an exact-ID refresh of journal claims.
	Colony    bool
	Requested []string
	Buildings []CurrentBuilding
}

func OwnedConstructions(claims domain.Fact[[]ConstructionClaim], observed domain.Fact[CurrentConstruction]) (domain.Fact[[]ConstructionClaim], error) {
	unknown := domain.Unknown[[]ConstructionClaim]()
	invalid := errors.New("invalid construction ownership evidence")
	census, observedKnown := observed.Value()
	if !observedKnown {
		return unknown, nil
	}
	if census.Colony {
		return observedConstructions(claims, census)
	}
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

// observedConstructions is the common current geometry/ownership view for
// facility planning and colony extent. History annotates an exact match only;
// it neither excludes player-built facilities nor transfers causal provenance.
func observedConstructions(claims domain.Fact[[]ConstructionClaim], census CurrentConstruction) (domain.Fact[[]ConstructionClaim], error) {
	unknown := domain.Unknown[[]ConstructionClaim]()
	if len(census.Buildings) > 256 || len(census.Requested) != 0 {
		return unknown, errors.New("invalid colony construction census")
	}
	history, _ := claims.Value()
	result := make([]ConstructionClaim, 0, len(census.Buildings))
	seen := map[string]bool{}
	for _, row := range census.Buildings {
		if !foodID(row.ID) || seen[row.ID] {
			return unknown, errors.New("invalid colony building identity")
		}
		b := row.Building
		if _, err := domain.NewBuilding(b.Definition(), b.Cell(), b.Rotation(), b.Stuff()); err != nil {
			return unknown, err
		}
		if !facilityCells(row.Cells) {
			return unknown, errors.New("invalid colony building footprint")
		}
		anchor := false
		for _, cell := range row.Cells {
			anchor = anchor || cell == b.Cell()
		}
		if !anchor {
			return unknown, errors.New("building anchor outside footprint")
		}
		seen[row.ID] = true
		owned := ConstructionClaim{Identity: domain.ConstructionIdentity{Current: row.ID}, Building: b}
		for _, claim := range history {
			if claim.Identity.Current == row.ID && claim.Building == b && claim.Identity.Validate() == nil && foodID(string(claim.Plan)) && foodID(string(claim.Action)) && foodID(string(claim.Goal)) {
				owned = claim
				break
			}
		}
		owned.Cells = append([]domain.Cell{}, row.Cells...)
		result = append(result, owned)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Identity.Current < result[j].Identity.Current })
	return domain.Known(result), nil
}
