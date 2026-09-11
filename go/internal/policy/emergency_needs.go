package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// EmergencyNeeds uses the same census and medical/threat rules as clock safety.
// Incomplete or conflicting evidence cannot become a recovered routine need.
func EmergencyNeeds(snapshot EmergencySnapshot, current domain.GenerationSnapshot, tick domain.Tick) (hostiles, patients domain.Fact[int64]) {
	decision := EvaluateEmergency(snapshot, current, tick)
	var threats, medical int64
	for _, hold := range decision.Holds {
		switch hold.Reason {
		case EmergencyUnknownFacts, EmergencyStaleFacts:
			return domain.Unknown[int64](), domain.Unknown[int64]()
		case EmergencyUnsafeThreat:
			threats++
		case EmergencyCriticalMedical:
			medical++
		}
	}
	return domain.Known(threats), domain.Known(medical)
}
