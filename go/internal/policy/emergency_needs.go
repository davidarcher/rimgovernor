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

// UrgentPatients counts the critical patients who are downed or bleeding: the
// ones whose care cannot wait for ordinary work. A living colonist who merely
// needs tending (a chronic condition, a minor wound the native doctors reach
// in their own time) is a patient but not an urgent one. Uncertainty is
// unknown, exactly as EmergencyNeeds reports it.
func UrgentPatients(snapshot EmergencySnapshot, current domain.GenerationSnapshot, tick domain.Tick) domain.Fact[int64] {
	if _, patients := EmergencyNeeds(snapshot, current, tick); !known(patients) {
		return domain.Unknown[int64]()
	}
	var urgent int64
	for _, pawn := range snapshot.facts.Colonists {
		if dead, known := pawn.Dead.Value(); known && dead {
			continue
		}
		downed, _ := pawn.Downed.Value()
		bleeding, _ := pawn.Bleeding.Value()
		if downed || bleeding {
			urgent++
		}
	}
	return domain.Known(urgent)
}

func known[T any](f domain.Fact[T]) bool {
	_, k := f.Value()
	return k
}
