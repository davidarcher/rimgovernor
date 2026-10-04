package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestDiseaseRestPersistsThroughRestartAndManual(t *testing.T) {
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	pawn := medicalPawn(true)
	c := policy.CareCondition{DefName: domain.Known("Flu"), Severity: domain.Known(.5), SeverityPerDay: domain.Known(.25), Immunity: domain.Known(.5), ImmunityPerDay: domain.Known(.25)}
	pawn.Conditions = domain.Known([]policy.CareCondition{c})
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{pawn})
	out := reviewRounds(t, s, &r)
	if len(out.Review.MedicalCare.Resting) != 1 {
		t.Fatal(out.Review.MedicalCare)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRounds(context.Background())
	if err != nil || len(loaded.MedicalCare.Resting) != 1 {
		t.Fatal(loaded, err)
	}
	r.Enabled = false
	r.Facts.MedicalPawns = domain.Unknown[[]policy.CarePawn]()
	out = reviewRounds(t, s, &r)
	if len(out.Review.MedicalCare.Resting) != 1 {
		t.Fatal(out.Review.MedicalCare)
	}
	r.Enabled = true
	c.Immunity = domain.Known(1.0)
	pawn.Conditions = domain.Known([]policy.CareCondition{c})
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{pawn})
	out = reviewRounds(t, s, &r)
	if len(out.Review.MedicalCare.Resting) != 0 {
		t.Fatal(out.Review.MedicalCare)
	}
}
