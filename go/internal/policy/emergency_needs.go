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
	// Raiders still in their pods are a threat no census row names yet.
	if snapshot.PodsPending() {
		threats++
	}
	// Patients are counted from the census, not the holds: a colonist who
	// only needs tending is the tend planner's patient although the
	// emergency no longer holds dispatch for them.
	return domain.Known(threats), domain.Known(countPatients(snapshot, true))
}

// UrgentPatients counts the critical patients who are bleeding or downed with
// a tend outstanding: the ones whose care cannot wait for ordinary work. A
// living colonist who merely needs tending (a chronic condition, a minor
// wound the native doctors reach in their own time) or who is downed with
// nothing to tend (malnutrition, exhaustion; a bed and ticks are the care ) is a patient but not an urgent one. Uncertainty is unknown, exactly
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

// AmputationNeeds adds the colonists a life-saving amputation would save
// (LifeSavingAmputations) to the critical and urgent patient counts
// EmergencyNeeds and UrgentPatients report, never counting a colonist either
// already includes. Losing an immunity race is an emergency: the amputation
// runs at CriticalMedical's priority. Unknown care facts add nobody; an
// unknown count stays unknown.
func AmputationNeeds(snapshot EmergencySnapshot, medical domain.Fact[[]CarePawn], patients, urgent domain.Fact[int64]) (domain.Fact[int64], domain.Fact[int64]) {
	pawns, ok := medical.Value()
	if !ok {
		return patients, urgent
	}
	byID := map[PawnID]EmergencyPawn{}
	for _, pawn := range snapshot.facts.Colonists {
		byID[pawn.ID] = pawn
	}
	var critical, pressing int64
	for _, pawn := range pawns {
		if len(LifeSavingAmputations(pawn)) == 0 {
			continue
		}
		row, found := byID[pawn.ID]
		downed, _ := row.Downed.Value()
		bleeding, _ := row.Bleeding.Value()
		needsTend, _ := row.NeedsTend.Value()
		if !found || !urgentPatient(row) {
			pressing++
		}
		if !found || !(downed || bleeding || needsTend) {
			critical++
		}
	}
	add := func(f domain.Fact[int64], n int64) domain.Fact[int64] {
		if v, k := f.Value(); k {
			return domain.Known(v + n)
		}
		return f
	}
	return add(patients, critical), add(urgent, pressing)
}

func known[T any](f domain.Fact[T]) bool {
	_, k := f.Value()
	return k
}
