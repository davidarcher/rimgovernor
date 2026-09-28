package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// equipCandidatePawnFacts reads one pawn row into the facts equip, gear
// and the combat loadout rank defenders by.
func equipCandidatePawnFacts(row *n.PawnState) policy.EquipCandidatePawn {
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
	return facts
}
