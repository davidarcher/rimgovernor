package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestMedicalReserveRetainsHistoryAcrossManualUnknownAndRestart(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Facts.Colonists = domain.Known(int64(3))
	set := func(n int64) {
		r.Facts.MedicalReserve = policy.MedicalReserveObservation{Items: domain.Known([]policy.MedicineStack{{ID: "medicine", Definition: "MedicineHerbal", Count: n, Perishable: domain.Known(false)}}), Resources: domain.Known([]policy.Amount{{Resource: "MedicineHerbal", Count: n}})}
	}
	set(2)
	out := reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainMedicalReserves); g.Goal.Need != domain.NeedDeficit || !out.Review.Latches.MedicalReserve {
		t.Fatal(g)
	}
	set(5)
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainMedicalReserves); g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
	r.Facts.MedicalReserve.Items = domain.Unknown[[]policy.MedicineStack]()
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainMedicalReserves); g.Goal.Need != domain.NeedUnknown || g.Goal.Priority != 3 {
		t.Fatal(g)
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	defer s.Close()
	retained, err := s.LoadRoutineReview(ctx)
	if err != nil || !retained.Latches.MedicalReserve || retained.Enabled {
		t.Fatal(retained, err)
	}
	r.Enabled = true
	set(5)
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainMedicalReserves); g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
	set(9)
	r.Facts.UpkeepIssued = map[policy.GoalID]bool{policy.MaintainMedicalReserves: true}
	out = reviewRoutine(t, s, &r)
	recovered := routineGoal(t, out, policy.MaintainMedicalReserves)
	if recovered.Goal.Need != domain.NeedRecovered || out.Review.Latches.MedicalReserve {
		t.Fatal(recovered)
	}
	set(2)
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainMedicalReserves); g.Goal.Need != domain.NeedDeficit || g.Goal.Epoch <= recovered.Goal.Epoch {
		t.Fatal(g)
	}
	set(5)
	r.Current.Load = "new-load"
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainMedicalReserves); g.Goal.Need != domain.NeedRecovered {
		t.Fatal(g)
	}
}
