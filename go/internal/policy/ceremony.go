package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The bestowing ceremony (#1602, epic #1598). Accepting the Empire's
// bestowing-ceremony quest sends a bestower, who waits at the throne spot
// until the player starts the ritual; the ceremony then fails if the
// bestower or the colonist is lost, drafted out of it or in a mental state.
// While one is pending the colony keeps the throne room of the title being
// bestowed (CeremonyThroneNeed outranks the favor-driven NextThroneNeed)
// and holds the colonist and the attendees off the Sleep timetable
// (CeremonyHold, PlanSchedulesHeld). Starting the ritual is a seam: no
// action kind carries the player's command (the bestower's Wait toil
// gizmo), so nothing here starts it.

// BestowingCeremony is one pending bestowing-ceremony quest, decoded from
// the royalty read. Quest is the quest census id. Pawn is the colonist to
// be bestowed with Title, Bestower the Empire pawn. Accepted is the quest
// Ongoing (else offered); BestowerWaiting the bestower standing on the map
// waiting for the command; Started the ritual running. Attendees are the
// colonists the ritual lord holds.
type BestowingCeremony struct {
	Quest           domain.QuestID
	Pawn, Bestower  PawnID
	Faction, Title  string
	Accepted        domain.Fact[bool]
	BestowerWaiting domain.Fact[bool]
	Started         domain.Fact[bool]
	Spot            domain.Fact[domain.Cell]
	Attendees       []PawnID
}

// PendingCeremonies are the accepted ceremonies, by colonist.
func PendingCeremonies(f RoyaltyFacts) []BestowingCeremony {
	var out []BestowingCeremony
	for _, c := range f.Ceremonies {
		if accepted, ok := c.Accepted.Value(); ok && accepted {
			out = append(out, c)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Pawn < out[j].Pawn })
	return out
}

// CeremonyThroneNeed is the throne room the first pending ceremony needs:
// the requirement of the title it bestows, false when none is pending or
// the title asks for no throne or is unknown to the ladder.
func CeremonyThroneNeed(f RoyaltyFacts) (ThroneNeed, bool) {
	for _, c := range PendingCeremonies(f) {
		for _, rung := range f.Ladder {
			if rung.Title != c.Title {
				continue
			}
			if need, ok := rungRequirement(rung); ok {
				need.Holder = c.Pawn
				return need, true
			}
		}
	}
	return ThroneNeed{}, false
}

// CeremonyHoldOf is CeremonyHold of a royalty read that may be unknown (no
// hold).
func CeremonyHoldOf(royalty domain.Fact[RoyaltyFacts]) map[PawnID]bool {
	if f, ok := royalty.Value(); ok {
		return CeremonyHold(f)
	}
	return nil
}

// CeremonyHold is the colonists a pending ceremony needs off the Sleep
// timetable: the one bestowed and the attendees the ritual lord holds.
func CeremonyHold(f RoyaltyFacts) map[PawnID]bool {
	var hold map[PawnID]bool
	for _, c := range PendingCeremonies(f) {
		if hold == nil {
			hold = map[PawnID]bool{}
		}
		hold[c.Pawn] = true
		for _, a := range c.Attendees {
			hold[a] = true
		}
	}
	return hold
}

// ClaimQuests are the offered bestowing-ceremony quests the title claim
// gate (NextTitleClaim) allows to accept now: the quest of the claiming
// colonist for the claimed title, not yet accepted. Accepting it is how the
// colony claims the title (SelectEmpireQuestMethod).
func ClaimQuests(f RoyaltyFacts, claim TitleClaim) []domain.QuestID {
	if !claim.Claim {
		return nil
	}
	var out []domain.QuestID
	for _, c := range f.Ceremonies {
		if accepted, ok := c.Accepted.Value(); ok && !accepted && c.Quest != "" && c.Pawn == claim.Holder && (c.Title == "" || c.Title == claim.Title) {
			out = append(out, c.Quest)
		}
	}
	return out
}
