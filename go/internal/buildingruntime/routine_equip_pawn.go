package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// equipCandidatePawnFacts is the pawn read the equip, gear and combat
// loadout planners share. It lives apart from routine_equip.go so equip
// code that uses medical_retry does not widen into the defense family.
func equipCandidatePawnFacts(row *n.PawnState, downsides policy.CreepJoinerDownsides) policy.EquipCandidatePawn {
	facts := policy.EquipCandidatePawn{Pawn: domain.PawnID(row.Pawn.GetId()), Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	facts.Profile = policy.BuildProfile(observation.WorkPawnRow(row))
	if row.RaidArmor != nil && !boundary.IssueField(row.Issues, "raid_armor") {
		facts.RaidArmor = domain.Known(row.GetRaidArmor())
	}
	facts.Position = domain.Cell{X: row.Pawn.GetPosition().GetX(), Z: row.Pawn.GetPosition().GetZ()}
	if biography := row.Biography; biography != nil && !boundary.IssueField(biography.Issues, "disabled_work_tags") {
		capable := true
		for _, tag := range biography.DisabledWorkTags {
			if tag == "Shooting" {
				facts.ShootingDisabled = true
			}
			if tag == "Violent" {
				capable = false
			}
		}
		facts.IncapableOfViolence = domain.Known(!capable)
	}
	if equipment := row.Equipment; equipment != nil && equipment.Armed != nil && !boundary.IssueField(equipment.Issues, "armed") {
		facts.Armed = domain.Known(equipment.GetArmed())
	}
	facts.NoArms = downsides.ArmsHold(bridge.CreepJoinerPawn(row))
	return facts
}

// definitionCatalogSource is a native source that serves the definition
// catalog (a *bridge.Client does, once per load).
type definitionCatalogSource interface {
	DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error)
}

// joyBuildingDefinitions are the catalog's joy buildings in preference order
// (DefinitionCatalog.JoyBuildings). A source that serves no catalog (a test
// double) gives none.
func joyBuildingDefinitions(ctx context.Context, native any, identity *c.Identity) ([]string, error) {
	source, ok := native.(definitionCatalogSource)
	if !ok {
		return nil, nil
	}
	catalog, err := source.DefinitionCatalog(ctx, identity)
	if err != nil {
		return nil, err
	}
	methods, err := catalog.JoyBuildings()
	if err != nil {
		return nil, err
	}
	names := make([]string, len(methods))
	for i, m := range methods {
		names[i] = m.Definition
	}
	return names, nil
}

// creepJoinerDownsides is the catalog's creepjoiner downside defs. A source
// that serves no catalog (a test double) gives none, which holds every
// creepjoiner back: nothing can show its downside.
func creepJoinerDownsides(ctx context.Context, native any, identity *c.Identity) (policy.CreepJoinerDownsides, error) {
	if source, ok := native.(definitionCatalogSource); ok {
		catalog, err := source.DefinitionCatalog(ctx, identity)
		if err != nil {
			return policy.CreepJoinerDownsides{}, err
		}
		return catalog.CreepJoinerDownsides(), nil
	}
	return policy.CreepJoinerDownsides{}, nil
}
