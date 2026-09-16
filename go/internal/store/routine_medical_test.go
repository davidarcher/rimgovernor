package store

import (
	"context"
	"encoding/json"
	"path/filepath"
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
	path := filepath.Join(t.TempDir(), "medical.db")
	s := open(t, path)
	r := routineRequest()
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{medicalPawn(true)})
	out := reviewRoutine(t, s, &r)
	initial := routineGoal(t, out, policy.MaintainMedicalCare)
	if initial.Goal.Need != domain.NeedDeficit || initial.Goal.Priority != 2 || initial.Goal.Status != domain.GoalActive {
		t.Fatal(initial)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded.MedicalCare, out.Review.MedicalCare) {
		t.Fatal(loaded, err)
	}
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{})
	// A caller-supplied aggregate cannot erase unresolved tracked patients.
	r.Facts.MedicalCareRecovered = domain.Known(true)
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainMedicalCare).Goal.Need != domain.NeedUnknown || !reflect.DeepEqual(out.Review.MedicalCare.Unknown, []policy.PawnID{"patient"}) {
		t.Fatal(out)
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainMedicalCare).Goal.Status != domain.GoalInvalidated || len(out.Review.MedicalCare.Unknown) != 1 {
		t.Fatal(out)
	}
	r.Enabled = true
	r.Current.Native++
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainMedicalCare).Goal.Need != domain.NeedUnknown {
		t.Fatal("new direction claimed recovery", out)
	}
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{medicalPawn(false)})
	out = reviewRoutine(t, s, &r)
	healed := routineGoal(t, out, policy.MaintainMedicalCare)
	if healed.Goal.Need != domain.NeedRecovered || healed.Goal.Status != domain.GoalSatisfied || len(out.Review.MedicalCare.Unknown) != 0 {
		t.Fatal(out)
	}
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{medicalPawn(true)})
	out = reviewRoutine(t, s, &r)
	renewed := routineGoal(t, out, policy.MaintainMedicalCare)
	if renewed.Goal.Need != domain.NeedDeficit || renewed.Goal.Epoch <= healed.Goal.Epoch {
		t.Fatal(renewed)
	}
	if _, err = s.CancelGoal(ctx, renewed.Goal.ID, renewed.Revision); err != nil {
		t.Fatal(err)
	}
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainMedicalCare).Goal.Status != domain.GoalCancelled {
		t.Fatal("review overrode cancellation", out)
	}
}

func TestRoutineMedicalWorldAndRewindResetHistory(t *testing.T) {
	t.Parallel()
	for _, change := range []struct {
		name  string
		apply func(*RoutineReviewRequest)
	}{
		{"world", func(r *RoutineReviewRequest) { r.Current.Load = "another-load" }},
		{"rewind", func(r *RoutineReviewRequest) { r.Tick = 1 }},
	} {
		t.Run(change.name, func(t *testing.T) {
			s := open(t, filepath.Join(t.TempDir(), "reset.db"))
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
	s := open(t, filepath.Join(t.TempDir(), "corrupt.db"))
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
	if _, err = s.LoadRoutineReview(context.Background()); err == nil {
		t.Fatal("duplicated patient persisted across restart")
	}
}
