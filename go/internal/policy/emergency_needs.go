package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// EmergencyNeeds uses the same census and medical/threat rules as clock safety.
// Incomplete or conflicting evidence cannot become a recovered routine need.
func EmergencyNeeds(snapshot EmergencySnapshot, current domain.GenerationSnapshot, tick domain.Tick) (hostiles, patients domain.Fact[int64]) {
	decision := EvaluateEmergency(snapshot, current, tick)
	var threats int64
	for _, hold := range decision.Holds {
		switch hold.Reason {
		case EmergencyUnknownFacts, EmergencyStaleFacts:
			return domain.Unknown[int64](), domain.Unknown[int64]()
		case EmergencyUnsafeThreat:
			threats++
		}
	}
	// A standing hostile building is a deficit without a hold: it never
	// stops the clock, but ActiveCombat stays open until it is destroyed.
	for _, threat := range snapshot.facts.Threats {
		if dead, _ := threat.Dead.Value(); threat.Building() && !dead {
			threats++
		}
	}
	// Patients are counted from the census, not the holds: a colonist who
	// only needs tending is the tend planner's patient although the
	// emergency no longer holds dispatch for them (#66).
	return domain.Known(threats), domain.Known(countPatients(snapshot, true))
}

// UrgentPatients counts the critical patients who are bleeding or downed with
// a tend outstanding: the ones whose care cannot wait for ordinary work. A
// living colonist who merely needs tending (a chronic condition, a minor
// wound the native doctors reach in their own time) or who is downed with
// nothing to tend (malnutrition, exhaustion; a bed and ticks are the care,
// #304) is a patient but not an urgent one. Uncertainty is unknown, exactly
// as EmergencyNeeds reports it.
func UrgentPatients(snapshot EmergencySnapshot, current domain.GenerationSnapshot, tick domain.Tick) domain.Fact[int64] {
	if _, patients := EmergencyNeeds(snapshot, current, tick); !known(patients) {
		return domain.Unknown[int64]()
	}
	return domain.Known(countPatients(snapshot, false))
}

// countPatients counts the living colonists who are urgent patients
// (urgentPatient) and, with tending, every downed, bleeding or tend-needing
// colonist too. Callers have already established that every health fact is
// known.
func countPatients(snapshot EmergencySnapshot, tending bool) int64 {
	var patients int64
	for _, pawn := range snapshot.facts.Colonists {
		if dead, known := pawn.Dead.Value(); known && dead {
			continue
		}
		downed, _ := pawn.Downed.Value()
		bleeding, _ := pawn.Bleeding.Value()
		needsTend, _ := pawn.NeedsTend.Value()
		if urgentPatient(pawn) || tending && (downed || bleeding || needsTend) {
			patients++
		}
	}
	return patients
}

func known[T any](f domain.Fact[T]) bool {
	_, k := f.Value()
	return k
}
