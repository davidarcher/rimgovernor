package store

import (
	"context"
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func medicalPawn(bad bool) policy.CarePawn {
	return policy.CarePawn{ID: "patient", Dead: domain.Known(false), NeedsRest: domain.Known(false), NeedsTend: domain.Known(false), BadConditions: domain.Known(bad)}
}

func TestRoutineMedicalRestartRecoveryRenewalAndCancellation(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Policy.Stage.Floor = policy.StageStable // the care phase is raised from StageStable
	// A stocked reserve, so the care phase alone decides the merged goal.
	r.Facts.Colonists = domain.Known(int64(1))
	r.Facts.MedicalReserve = policy.MedicalReserveObservation{Catalog: policy.CoreItemFacts(), Items: domain.Known([]policy.MedicineStack{{ID: "medicine", Definition: "MedicineHerbal", Count: 100, Perishable: domain.Known(false)}}), Resources: domain.Known([]policy.Amount{{Resource: "MedicineHerbal", Count: 100}})}
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{medicalPawn(true)})
	out := reviewRoutine(t, s, &r)
	initial := routineGoal(t, out, policy.MaintainMedicalReserves)
	if initial.Standard.Finding != domain.FindingUnmet || initial.Standard.Priority != 2 || initial.Standard.Status != domain.StandardOpen {
		t.Fatal(initial)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRounds(ctx)
	if err != nil || !reflect.DeepEqual(loaded.MedicalCare, out.Review.MedicalCare) {
		t.Fatal(loaded, err)
	}
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{})
	// A caller-supplied aggregate cannot erase unresolved tracked patients.
	r.Facts.MedicalCareRecovered = domain.Known(true)
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainMedicalReserves).Standard.Finding != domain.FindingUnclear || !reflect.DeepEqual(out.Review.MedicalCare.Unknown, []policy.PawnID{"patient"}) {
		t.Fatal(out)
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if out.Review.Veto(routineGoal(t, out, policy.MaintainMedicalReserves).Standard) == "" || len(out.Review.MedicalCare.Unknown) != 1 {
		t.Fatal(out)
	}
	r.Enabled = true
	r.Current.Native++
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainMedicalReserves).Standard.Finding != domain.FindingUnclear {
		t.Fatal("new direction claimed recovery", out)
	}
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{medicalPawn(false)})
	out = reviewRoutine(t, s, &r)
	healed := routineGoal(t, out, policy.MaintainMedicalReserves)
	if healed.Standard.Finding != domain.FindingMet || healed.Standard.Status != domain.StandardSettled || len(out.Review.MedicalCare.Unknown) != 0 {
		t.Fatal(healed.Standard, out.Review.Latches.Medical, out.Review.Latches.MedicalReserve)
	}
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{medicalPawn(true)})
	out = reviewRoutine(t, s, &r)
	renewed := routineGoal(t, out, policy.MaintainMedicalReserves)
	if renewed.Standard.Finding != domain.FindingUnmet || renewed.Standard.Episode <= healed.Standard.Episode {
		t.Fatal(renewed)
	}
}

func TestRoutineMedicalWorldAndRewindResetHistory(t *testing.T) {
	t.Parallel()
	for _, change := range []struct {
		name  string
		apply func(*RoundsRequest)
	}{
		{"world", func(r *RoundsRequest) { r.Current.Load = "another-load" }},
		{"rewind", func(r *RoundsRequest) { r.Tick = 1 }},
	} {
		t.Run(change.name, func(t *testing.T) {
			s := open(t, memoryPath(t))
			r := routineRequest()
			r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{medicalPawn(true)})
			reviewRoutine(t, s, &r)
			change.apply(&r)
			r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{})
			out := reviewRoutine(t, s, &r)
			if out.Review.MedicalCare.Recovered() != domain.Known(true) {
				t.Fatal(out)
			}
		})
	}
}

func TestRoutineMedicalCorruptHistoryRejectedWhileDisabled(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{medicalPawn(true)})
	reviewRoutine(t, s, &r)
	r.Enabled = false
	out := reviewRoutine(t, s, &r)
	out.Review.MedicalCare.Unknown = []policy.PawnID{"patient"}
	data, err := json.Marshal(out.Review)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("UPDATE routine_review SET payload=? WHERE singleton=1", data); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LoadRounds(context.Background()); err == nil {
		t.Fatal("duplicated patient persisted across restart")
	}
}
