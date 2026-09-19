package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestEmergencyNeedsCountPatientsAndThreatsWithoutDoubleCounting(t *testing.T) {
	current := domain.GenerationSnapshot{Colony: "colony", Load: "load", Plan: "plan"}
	facts := EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true),
		Colonists: []EmergencyPawn{{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(true), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)}},
		Threats:   []EmergencyThreat{{ID: "threat", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}, {ID: "threat", Kind: HuntingPredator, Dead: domain.Known(false), Downed: domain.Known(false)}}}
	snapshot, err := NewEmergencySnapshot(current, 7, facts)
	if err != nil {
		t.Fatal(err)
	}
	hostiles, patients := EmergencyNeeds(snapshot, current, 7)
	if n, known := hostiles.Value(); !known || n != 1 {
		t.Fatal(hostiles)
	}
	if n, known := patients.Value(); !known || n != 1 {
		t.Fatal(patients)
	}
	for _, kind := range []string{"census", "health", "conflict", "stale"} {
		t.Run(kind, func(t *testing.T) {
			f := facts
			f.Colonists = append([]EmergencyPawn(nil), facts.Colonists...)
			f.Threats = append([]EmergencyThreat(nil), facts.Threats...)
			tick := domain.Tick(7)
			switch kind {
			case "census":
				f.ColonistsComplete = domain.Unknown[bool]()
			case "health":
				f.Colonists[0].NeedsTend = domain.Unknown[bool]()
			case "conflict":
				f.Threats[1].Dead = domain.Known(true)
			case "stale":
				tick++
			}
			s, err := NewEmergencySnapshot(current, 7, f)
			if err != nil {
				t.Fatal(err)
			}
			a, b := EmergencyNeeds(s, current, tick)
			if _, known := a.Value(); known {
				t.Fatal("uncertainty recovered threats")
			}
			if _, known := b.Value(); known {
				t.Fatal("uncertainty recovered patients")
			}
		})
	}
}

// A downed colonist with nothing to tend (malnutrition, #304) is a patient
// (rescue serves them) but not an urgent one; downed with a tend outstanding
// or bleeding is.
func TestUrgentPatientsCountsBleedingOrDownedUntendedOnly(t *testing.T) {
	current := domain.GenerationSnapshot{Colony: "colony", Load: "load", Plan: "plan"}
	pawn := func(id PawnID, downed, bleeding, needsTend bool) EmergencyPawn {
		return EmergencyPawn{ID: id, Dead: domain.Known(false), Downed: domain.Known(downed), Bleeding: domain.Known(bleeding), NeedsTend: domain.Known(needsTend)}
	}
	facts := EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true),
		Colonists: []EmergencyPawn{pawn("chronic", false, false, true), pawn("starved", true, false, false), pawn("downed", true, false, true), pawn("bleeding", false, true, true), pawn("well", false, false, false)}}
	snapshot, err := NewEmergencySnapshot(current, 7, facts)
	if err != nil {
		t.Fatal(err)
	}
	if _, patients := EmergencyNeeds(snapshot, current, 7); patients != domain.Known(int64(4)) {
		t.Fatal(patients)
	}
	if urgent := UrgentPatients(snapshot, current, 7); urgent != domain.Known(int64(2)) {
		t.Fatal(urgent)
	}
	// A dead colonist is nobody's patient; uncertainty stays unknown.
	dead := facts
	dead.Colonists = append([]EmergencyPawn(nil), facts.Colonists...)
	dead.Colonists[2].Dead = domain.Known(true)
	if s, err := NewEmergencySnapshot(current, 7, dead); err != nil {
		t.Fatal(err)
	} else if urgent := UrgentPatients(s, current, 7); urgent != domain.Known(int64(1)) {
		t.Fatal(urgent)
	}
	partial := facts
	partial.ColonistsComplete = domain.Unknown[bool]()
	if s, err := NewEmergencySnapshot(current, 7, partial); err != nil {
		t.Fatal(err)
	} else if _, known := UrgentPatients(s, current, 7).Value(); known {
		t.Fatal("uncertainty recovered urgency")
	}
	if _, known := UrgentPatients(snapshot, current, 8).Value(); known {
		t.Fatal("stale facts recovered urgency")
	}
}
