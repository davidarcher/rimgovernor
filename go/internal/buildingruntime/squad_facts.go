package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// meleeCapable mirrors the equipment-known derivation already inline in
// InspectMelee: melee needs no specific weapon, only complete information
// about whether one is equipped (native picks fists or an equipped weapon).
func meleeCapable(equipment *n.PawnEquipment) domain.Fact[bool] {
	if equipment == nil || equipment.Armed == nil || meleeIssue(equipment.Issues, "equipped") || meleeIssue(equipment.Issues, "armed") {
		return domain.Unknown[bool]()
	}
	known := !equipment.GetArmed()
	if equipment.GetArmed() && equipment.PrimaryId != nil {
		for _, item := range equipment.Equipped {
			if item.GetThing().GetId() == equipment.GetPrimaryId() {
				known = true
			}
		}
	}
	return domain.Known(known)
}

// squadThreatFacts populates only what the shared pawn snapshot actually
// carries. Body size and manhunter status are not exposed through this read,
// so animal/manhunter opponents stay ineligible here (Animal/BodySize/
// Manhunter unknown) until that native read is extended; humanlike opponents
// are unaffected.
func squadThreatFacts(row *n.PawnState) policy.SquadThreatFacts {
	facts := policy.SquadThreatFacts{ID: policy.PawnID(row.Pawn.GetId()), Dead: draftBool(row.Dead), Downed: draftBool(row.Downed)}
	if row.Humanlike != nil {
		facts.Humanlike = domain.Known(row.GetHumanlike())
	}
	if row.Animal != nil {
		facts.Animal = domain.Known(row.GetAnimal())
	}
	facts.RangedEquipped = rangedWeaponEquipped(row.Equipment)
	return facts
}

func squadDefenderFacts(row *n.PawnState) policy.SquadDefenderFacts {
	facts := policy.SquadDefenderFacts{ID: domain.PawnID(row.Pawn.GetId()), Dead: draftBool(row.Dead), Downed: draftBool(row.Downed), Drafted: draftBool(row.Drafted), MentalState: draftPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !tendIssue(row.Job.Issues, "player_forced") && !tendIssue(row.Job.Issues, "queued_jobs") {
		facts.PlayerForced, facts.QueuedJobs = draftBool(row.Job.PlayerForced), draftUint(row.Job.QueuedJobs)
	}
	if health := row.Health; health != nil && !tendIssue(health.Issues, "health") {
		facts.NeedsTend = draftBool(health.NeedsTend)
		if health.SummaryFraction != nil {
			facts.HealthFraction = domain.Known(health.GetSummaryFraction())
		}
	}
	if biography := row.Biography; biography != nil && !meleeIssue(biography.Issues, "disabled_work_tags") {
		capable := true
		for _, tag := range biography.DisabledWorkTags {
			if tag == "Violent" {
				capable = false
			}
		}
		facts.ViolenceCapable = domain.Known(capable)
	}
	facts.RangedEquipped = rangedWeaponEquipped(row.Equipment)
	facts.MeleeEquipped = meleeCapable(row.Equipment)
	if equipment := row.Equipment; equipment != nil && equipment.Armed != nil && !meleeIssue(equipment.Issues, "armed") {
		facts.Armed = domain.Known(equipment.GetArmed())
	}
	return facts
}
