package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ConstructionClaim names the autonomous action that owns a building. The
// journal supplies Plan, Action, Goal and Building for every applied
// building intent; OwnedConstructions fills Identity (the building's
// current id) and Cells from the census row standing where the intent
// built. A census row no intent built carries Identity, Building and
// Cells only.
type ConstructionClaim struct {
	Plan     domain.PlanID
	Action   domain.ActionID
	Concern  domain.ConcernID
	Identity domain.ConstructionIdentity
	Building domain.Building
	Cells    []domain.Cell
}
type CurrentBuilding struct {
	ID       string
	Building domain.Building
	Cells    []domain.Cell
}

// CurrentConstruction is the colony's built, player-owned buildings.
// Colony marks a complete census; Requested scopes an exact-ID read.
type CurrentConstruction struct {
	Colony    bool
	Requested []string
	Buildings []CurrentBuilding
	// Sites are the player's blueprints and frames still standing.
	Sites []ConstructionSite
}

// ConstructionSite is one blueprint or frame on the map: the building it
// will become and its stage.
type ConstructionSite struct {
	Building domain.Building
	Stage    string
}

// BuildingWork is where an applied building intent stands.
type BuildingWork int

const (
	// BuildingOpen: its blueprint or frame stands, or the census is unknown.
	BuildingOpen BuildingWork = iota
	// BuildingDone: a built row stands where it built.
	BuildingDone
	// BuildingGone: neither exists; the work closed without a building.
	BuildingGone
)

// WorkOpen classifies an applied building intent against the census by
// geometry (#1355): whatever stands with its definition, stuff, anchor and
// rotation is its work, whoever placed it. A match placed by someone else
// is the same end state, so no lineage is kept.
func WorkOpen(building domain.Building, observed domain.Fact[CurrentConstruction]) BuildingWork {
	census, known := observed.Value()
	if !known || !census.Colony {
		return BuildingOpen
	}
	if _, ok := census.Built(building); ok {
		return BuildingDone
	}
	for _, site := range census.Sites {
		if site.Building == building {
			return BuildingOpen
		}
	}
	return BuildingGone
}

// Built is the id of the built row standing as building.
func (c CurrentConstruction) Built(building domain.Building) (string, bool) {
	for _, row := range c.Buildings {
		if row.Building == building {
			return row.ID, true
		}
	}
	return "", false
}

// OwnedConstructions is the common current geometry/ownership view for
// facility planning and colony extent: every census building, annotated with
// the journal claim whose building stands there. History never excludes
// player-built facilities.
func OwnedConstructions(claims domain.Fact[[]ConstructionClaim], observed domain.Fact[CurrentConstruction]) (domain.Fact[[]ConstructionClaim], error) {
	unknown := domain.Unknown[[]ConstructionClaim]()
	census, known := observed.Value()
	if !known || !census.Colony {
		return unknown, nil
	}
	if len(census.Requested) != 0 {
		return unknown, errors.New("invalid colony construction census")
	}
	history, _ := claims.Value()
	byBuilding := map[domain.Building]ConstructionClaim{}
	for _, claim := range history {
		if foodID(string(claim.Plan)) && foodID(string(claim.Action)) && foodID(string(claim.Concern)) {
			byBuilding[claim.Building] = claim
		}
	}
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
		owned := ConstructionClaim{Building: b}
		if claim, claimed := byBuilding[b]; claimed {
			owned = claim
		}
		owned.Identity = domain.ConstructionIdentity{Current: row.ID}
		owned.Cells = append([]domain.Cell{}, row.Cells...)
		result = append(result, owned)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Identity.Current < result[j].Identity.Current })
	return domain.Known(result), nil
}
