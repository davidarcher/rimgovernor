package policy

import (
	"errors"
	"sort"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ConstructionClaim names the autonomous action that owns a building. The
// journal supplies Plan, Action, Goal and Building for every applied
// building intent; OwnedConstructions fills Identity (the building's
// current id) and Cells from the census row whose intent key names the
// action. A census row no intent built carries Identity, Building and
// Cells only.
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
	// IntentKey is the Actions/Apply key of the building intent that placed
	// this building (BuildingState.intent_key), empty when none did.
	IntentKey string
}

// CurrentConstruction is the colony's built, player-owned buildings.
// Colony marks a complete census; Requested scopes an exact-ID read.
type CurrentConstruction struct {
	Colony    bool
	Requested []string
	Buildings []CurrentBuilding
	// Intents are building intents still standing as a blueprint or frame.
	Intents []ConstructionIntent
}

// ConstructionIntent is one keyed blueprint or frame on the map.
type ConstructionIntent struct {
	Key   string
	Stage string
}

// BuildingWork is where an applied building intent stands.
type BuildingWork int

const (
	// BuildingOpen: its blueprint or frame stands, or the census is unknown.
	BuildingOpen BuildingWork = iota
	// BuildingDone: a built row carries its key.
	BuildingDone
	// BuildingGone: neither exists; the work closed without a building.
	BuildingGone
)

// WorkOpen classifies an applied building action against the census.
func WorkOpen(action domain.ActionID, observed domain.Fact[CurrentConstruction]) BuildingWork {
	census, known := observed.Value()
	if !known || !census.Colony {
		return BuildingOpen
	}
	for _, row := range census.Buildings {
		if a, ok := IntentAction(row.IntentKey); ok && a == action {
			return BuildingDone
		}
	}
	for _, in := range census.Intents {
		if a, ok := IntentAction(in.Key); ok && a == action {
			return BuildingOpen
		}
	}
	return BuildingGone
}

// IntentAction is the action an intent key (<action>/<attempt>) names.
func IntentAction(key string) (domain.ActionID, bool) {
	i := strings.LastIndexByte(key, '/')
	if i <= 0 {
		return "", false
	}
	if _, err := strconv.ParseUint(key[i+1:], 10, 64); err != nil {
		return "", false
	}
	return domain.ActionID(key[:i]), true
}

// BuiltActions maps each action whose building stands built in a complete
// census to that building's id. Native reports a key only on a finished
// building, so presence here is the "built" fact every gate reads.
func BuiltActions(observed domain.Fact[CurrentConstruction]) (map[domain.ActionID]string, bool) {
	census, known := observed.Value()
	if !known || !census.Colony {
		return nil, false
	}
	built := map[domain.ActionID]string{}
	for _, row := range census.Buildings {
		if action, ok := IntentAction(row.IntentKey); ok {
			built[action] = row.ID
		}
	}
	return built, true
}

// OwnedConstructions is the common current geometry/ownership view for
// facility planning and colony extent: every census building, annotated with
// the journal claim whose action its intent key names. History neither
// excludes player-built facilities nor transfers provenance by geometry.
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
	byAction := map[domain.ActionID]ConstructionClaim{}
	for _, claim := range history {
		if foodID(string(claim.Plan)) && foodID(string(claim.Action)) && foodID(string(claim.Goal)) {
			byAction[claim.Action] = claim
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
		if action, ok := IntentAction(row.IntentKey); ok {
			if claim, claimed := byAction[action]; claimed && claim.Building == b {
				owned = claim
			}
		}
		owned.Identity = domain.ConstructionIdentity{Current: row.ID}
		owned.Cells = append([]domain.Cell{}, row.Cells...)
		result = append(result, owned)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Identity.Current < result[j].Identity.Current })
	return domain.Known(result), nil
}
