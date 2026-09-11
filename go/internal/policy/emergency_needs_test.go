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
