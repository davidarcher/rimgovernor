package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// equipCandidatePawnFacts is the pawn read the equip, gear and combat
// loadout planners share. It lives apart from rounds_equip.go so equip
// code that uses medical_retry does not widen into the defense family.
func equipCandidatePawnFacts(row *n.PawnState, catalog *bridge.DefinitionCatalog, things bridge.Things) (policy.EquipCandidatePawn, error) {
	facts := policy.EquipCandidatePawn{Pawn: domain.PawnID(row.Pawn.GetId()), Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	work, err := observation.WorkPawnRow(row, catalog, things)
	if err != nil {
		return policy.EquipCandidatePawn{}, err
	}
	facts.Profile = policy.BuildProfile(work)
	// A colonist working Hunting is the hunter: only a ranged weapon of hunting
	// reach arms them (ScoreWeapon).
	if rows, known := work.Work.Value(); known {
		for _, w := range rows {
			if w.Work == policy.WorkHunting && w.Priority > 0 && !w.Disabled {
				facts.Role = policy.WeaponRoleHunter
			}
		}
	}
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
	facts.NoArms = catalog.CreepJoinerDownsides().ArmsHold(bridge.CreepJoinerPawn(row))
	return facts, nil
}

// definitionCatalogSource is a native source that serves the definition
// catalog (a *bridge.Client does, once per load).
type definitionCatalogSource interface {
	DefinitionCatalog(context.Context, *c.Identity) (*bridge.DefinitionCatalog, error)
}

// recreationDefinitions are the dining furniture and recreation foothold the catalog rows name
// (DefinitionCatalog.DiningFurniture) and the catalog's joy buildings in
// preference order (DefinitionCatalog.JoyBuildings). A source that serves no
// catalog cannot name the foothold: an error.
func recreationDefinitions(ctx context.Context, native any, identity *c.Identity) (furniture policy.DiningFurniture, joy []string, err error) {
	source, ok := native.(definitionCatalogSource)
	if !ok {
		return policy.DiningFurniture{}, nil, errors.New("native serves no definition catalog for the recreation foothold")
	}
	catalog, err := source.DefinitionCatalog(ctx, identity)
	if err != nil {
		return policy.DiningFurniture{}, nil, err
	}
	if furniture, err = catalog.DiningFurniture(); err != nil {
		return policy.DiningFurniture{}, nil, err
	}
	methods, err := catalog.JoyBuildings()
	if err != nil {
		return policy.DiningFurniture{}, nil, err
	}
	for _, m := range methods {
		joy = append(joy, m.Definition)
	}
	return furniture, joy, nil
}

// pawnCatalog is the catalog the pawn rows resolve their traits and creepjoiner
// downsides against. A source that serves no catalog (a test double) gives
// nil: no downside def, which holds every creepjoiner back, and no trait
// resolves.
func pawnCatalog(ctx context.Context, native any, identity *c.Identity) (*bridge.DefinitionCatalog, error) {
	if source, ok := native.(definitionCatalogSource); ok {
		return source.DefinitionCatalog(ctx, identity)
	}
	return nil, nil
}
