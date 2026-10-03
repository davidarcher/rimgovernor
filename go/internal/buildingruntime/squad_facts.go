package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// rangedWeaponEquipped requires the pawn's primary equipped item to be found
// and its native ranged flag known. Unarmed is a known false, never unknown.
func rangedWeaponEquipped(equipment *n.PawnEquipment) domain.Fact[bool] {
	if equipment == nil || equipment.Armed == nil || boundary.IssueField(equipment.Issues, "equipped") || boundary.IssueField(equipment.Issues, "armed") {
		return domain.Unknown[bool]()
	}
	if !equipment.GetArmed() {
		return domain.Known(false)
	}
	if equipment.PrimaryId == nil {
		return domain.Unknown[bool]()
	}
	for _, item := range equipment.Equipped {
		if item.GetThing().GetId() == equipment.GetPrimaryId() {
			if item.Ranged == nil {
				return domain.Unknown[bool]()
			}
			return domain.Known(item.GetRanged())
		}
	}
	return domain.Unknown[bool]()
}

// meleeCapable reports whether equipment is completely known:
// melee needs no specific weapon, only complete information
// about whether one is equipped (native picks fists or an equipped weapon).
func meleeCapable(equipment *n.PawnEquipment) domain.Fact[bool] {
	if equipment == nil || equipment.Armed == nil || boundary.IssueField(equipment.Issues, "equipped") || boundary.IssueField(equipment.Issues, "armed") {
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

// squadThreatFacts populates what the shared pawn snapshot carries, including
// the animal detail (BodySize) requested alongside combat pawn reads and the
// manhunter mental state exact-matched against Manhunter/ManhunterPermanent.
func squadThreatFacts(row *n.PawnState) policy.SquadThreatFacts {
	facts := policy.SquadThreatFacts{ID: policy.PawnID(row.Pawn.GetId()), Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed)}
	if row.Humanlike != nil {
		facts.Humanlike = domain.Known(row.GetHumanlike())
	}
	if row.Animal != nil {
		facts.Animal = domain.Known(row.GetAnimal())
	}
	facts.Anomaly = bridge.PawnAnomaly(row.Anomaly)
	facts.RangedEquipped = rangedWeaponEquipped(row.Equipment)
	// A wild animal has no equipment tracker (the equipped field reads as a
	// missing native component); it carries no ranged weapon either way, and
	// neither does an entity whose attack native read as melee only (#1739).
	if _, known := facts.RangedEquipped.Value(); !known && (facts.Animal == domain.Known(true) || policy.MeleeEntity(facts)) {
		facts.RangedEquipped = domain.Known(false)
	}
	facts.Manhunter = manhunterFact(row.MentalState, row.Issues)
	if animal := row.AnimalState; animal != nil && animal.BodySize != nil && !boundary.IssueField(animal.Issues, "body_size") {
		facts.BodySize = domain.Known(animal.GetBodySize())
	}
	return facts
}

// manhunterFact is an exact mentalState comparison
// rather than the looser substring match NativePawnObservationTools.cs uses
// for HostileReason: only the two Manhunter mental-state defNames count.
func manhunterFact(state *string, issues []*n.ReadIssue) domain.Fact[bool] {
	if state != nil {
		return domain.Known(*state == "Manhunter" || *state == "ManhunterPermanent")
	}
	for _, issue := range issues {
		if issue.GetField() == "mental_state" && issue.GetUnavailable().GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE {
			return domain.Known(false)
		}
	}
	return domain.Unknown[bool]()
}

// squadDefenderFacts reads row's defender facts. needed is plannedDrafts:
// a drafted pawn a live plan needs is spoken for (DraftOwned); any other
// drafted pawn is free to take (#939).
func squadDefenderFacts(row *n.PawnState, needed map[domain.PawnID]bool) policy.SquadDefenderFacts {
	facts := policy.SquadDefenderFacts{ID: domain.PawnID(row.Pawn.GetId()), Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	facts.DraftOwned = domain.Known(needed[facts.ID])
	if bt, ok := bridge.PawnBiotech(row.Biotech).Value(); ok {
		facts.Deathresting = bt.IsDeathresting()
	}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") && !boundary.IssueField(row.Job.Issues, "queued_jobs") {
		facts.PlayerForced, facts.QueuedJobs = boundary.FactBool(row.Job.PlayerForced), boundary.FactUint(row.Job.QueuedJobs)
	}
	if health := row.Health; health != nil && !boundary.IssueField(health.Issues, "health") {
		facts.NeedsTend = boundary.FactBool(health.NeedsTend)
		if health.SummaryFraction != nil {
			facts.HealthFraction = domain.Known(health.GetSummaryFraction())
		}
	}
	if biography := row.Biography; biography != nil && !boundary.IssueField(biography.Issues, "disabled_work_tags") {
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
	if equipment := row.Equipment; equipment != nil && equipment.Armed != nil && !boundary.IssueField(equipment.Issues, "armed") {
		facts.Armed = domain.Known(equipment.GetArmed())
	}
	return facts
}
