package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A manhunter animal adjacent to a downed patient: the tender is not sent in
// alone, and the patient is tended again once the threat is gone.
func TestSelectTendWaitsForAdjacentManhunter(t *testing.T) {
	cell := func(x, z int32) domain.Fact[domain.Cell] { return domain.Known(domain.Cell{X: x, Z: z}) }
	facts := EmergencyFacts{
		Colonists: []EmergencyPawn{{ID: "patient", Position: cell(10, 10)}, {ID: "medic", Position: cell(30, 10)}},
		Threats: []EmergencyThreat{{ID: "muffalo", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false),
			Animal: domain.Known(true), Distance: domain.Known(1.0), Position: cell(11, 10)}},
	}
	patient := tendPatient("patient", 2)
	patient.Downed = domain.Known(true)
	patient.Threatened = ThreatenedPatients(facts)["patient"]
	if !patient.Threatened {
		t.Fatal("adjacent manhunter did not threaten the patient")
	}
	doctors := []TendDoctorFacts{tendDoctor("medic", 10)}
	if d, p, ok := selectTend(doctors, []TendPatientFacts{patient}); ok {
		t.Fatalf("tender %s sent to %s beside a live manhunter", d, p)
	}
	facts.Threats[0].Downed = domain.Known(true)
	patient.Threatened = ThreatenedPatients(facts)["patient"]
	if _, p, ok := selectTend(doctors, []TendPatientFacts{patient}); !ok || p != "patient" {
		t.Fatal("patient not tended once the manhunter is down")
	}
	facts.Threats[0].Downed = domain.Known(false)
	facts.Threats[0].Position = cell(40, 40)
	if ThreatenedPatients(facts)["patient"] {
		t.Fatal("distant hostile blocked the tend")
	}
}
