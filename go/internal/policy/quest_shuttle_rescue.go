package policy

// ShuttleRescueAdmission admits the short crash offer immediately when its
// observed window is still open and the colony can defend the generated raid.
func ShuttleRescueAdmission(offer JoinerOffer, facts RoundsFacts) QuestSkipReason {
	profile, known := offer.Profile.Value()
	if !known || profile.Family != QuestFamilyShuttleRescue {
		return ""
	}
	remaining, known := offer.ExpiresInTicks.Value()
	if !known {
		return "expiry_unknown"
	}
	if remaining <= 0 {
		return "expired"
	}
	return JoinerThreatReason(offer, facts)
}
