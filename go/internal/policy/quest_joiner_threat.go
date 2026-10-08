package policy

import "math"

// JoinerThreatReason gates the delayed quest raid using its generated budget.
func JoinerThreatReason(offer JoinerOffer, facts RoundsFacts) QuestSkipReason {
	calm, known := facts.QuestColonyCalm.Value()
	if !known {
		return "capacity_unknown"
	}
	if !calm {
		return "colony_busy"
	}
	threat, known := offer.ThreatPoints.Value()
	if !known || math.IsNaN(threat) || math.IsInf(threat, 0) || threat < 0 {
		return "threat_unknown"
	}
	defense, known := facts.DefenseCapacity.Value()
	if !known || math.IsNaN(defense) || math.IsInf(defense, 0) || defense < 0 {
		return "defense_unknown"
	}
	if defense < threat {
		return "defense_capacity"
	}
	return ""
}
