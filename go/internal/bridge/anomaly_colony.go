package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// validateAnomalyColony checks the Anomaly colony section (#1738): names and
// ids are unique per table, a category's discovered count never exceeds its
// entries, knowledge and the threat fraction are finite and nonnegative and
// levels are nonnegative. Absent scalars stay unknown.
func validateAnomalyColony(v *o.ColonyFactsSnapshot) error {
	if v.Anomaly == nil {
		return nil
	}
	switch s := v.Anomaly.Outcome.(type) {
	case *o.AnomalySection_Unavailable:
		return validateUnavailable(s.Unavailable)
	case *o.AnomalySection_Observed:
		f := s.Observed
		if f == nil {
			return contract("incomplete anomaly colony facts")
		}
		seen := map[string]bool{}
		for _, r := range f.Knowledge {
			if r == nil || validID(r.GetCategory()) != nil || seen["k"+r.GetCategory()] || r.CurrentProject != nil && validID(r.GetCurrentProject()) != nil || badNonNegative(r.Knowledge) ||
				r.CurrentProject == nil && r.Knowledge != nil {
				return contract("invalid anomaly knowledge category")
			}
			seen["k"+r.GetCategory()] = true
		}
		for _, r := range f.Codex {
			if r == nil || validID(r.GetCategory()) != nil || seen["c"+r.GetCategory()] || r.Entries == nil || r.Discovered == nil || r.GetDiscovered() > r.GetEntries() {
				return contract("invalid anomaly codex category")
			}
			seen["c"+r.GetCategory()] = true
		}
		for _, name := range f.DiscoveredEntries {
			if validID(name) != nil || seen["e"+name] {
				return contract("invalid anomaly discovered codex entry")
			}
			seen["e"+name] = true
		}
		for _, r := range f.HeldEntities {
			if r == nil || validID(r.GetPawnId()) != nil || seen["p"+r.GetPawnId()] || validID(r.GetPlatformId()) != nil || seen["h"+r.GetPlatformId()] {
				return contract("invalid anomaly held entity")
			}
			seen["p"+r.GetPawnId()] = true
			seen["h"+r.GetPlatformId()] = true
		}
		if i := f.Incidents; i != nil {
			if i.Level != nil && i.GetLevel() < 0 || i.HighestLevelReached != nil && i.GetHighestLevelReached() < 0 || i.TicksSinceLevelChange != nil && i.GetTicksSinceLevelChange() < 0 ||
				i.LevelDef != nil && validID(i.GetLevelDef()) != nil || badNonNegative(i.AnomalyThreatFractionNow) {
				return contract("invalid anomaly incident state")
			}
		}
		return nil
	default:
		return contract("missing anomaly colony outcome")
	}
}
