package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// QuestAccepter chooses only from the native eligibility list. The least
// skilled eligible pawn is preferred; royal favor goes to an existing
// title holder when one is eligible.
func QuestAccepter(offer JoinerOffer, facts RoundsFacts, workers domain.Fact[[]WorkPawn]) (domain.PawnID, bool) {
	if !offer.RequiresAccepter {
		return "", true
	}
	if len(offer.EligiblePawnIDs) == 0 {
		return "", false
	}
	royal, royalKnown := facts.Royalty.Value()
	choice := SelectQuestReward(offer, facts).Choice
	favor := int64(0)
	for _, reward := range offer.Favor {
		if reward.Choice == choice || reward.Choice == -1 {
			favor += int64(reward.Favor)
		}
	}
	priorities := map[PawnID]int{}
	pawns, known := workers.Value()
	if !known {
		return "", false
	}
	for _, pawn := range pawns {
		skills, known := pawn.Skills.Value()
		if !known {
			return "", false
		}
		priorities[pawn.ID] = 0
		for _, skill := range skills {
			if !skill.Disabled {
				priorities[pawn.ID] += skill.Level
			}
		}
	}
	best := domain.PawnID("")
	bestRoyal := false
	bestValue := 0
	for _, id := range offer.EligiblePawnIDs {
		if _, found := priorities[PawnID(id)]; !found {
			continue
		}
		isRoyal := false
		if royalKnown && favor > 0 {
			for _, holding := range royal.Holders[PawnID(id)] {
				isRoyal = isRoyal || holding.Title != ""
			}
		}
		value := priorities[PawnID(id)]
		if best == "" || (isRoyal && !bestRoyal) || (isRoyal == bestRoyal && (value < bestValue || (value == bestValue && id < best))) {
			best, bestRoyal, bestValue = id, isRoyal, value
		}
	}
	return best, best != ""
}
