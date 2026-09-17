package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A bed assignment persists its pawn, bed and previous-bed expectation in
// the action row and its two CAS tokens in a typed admission record; the
// generic prepare refuses it and dispatch needs the typed admission.
func TestBedAssignActionRoundTripAndAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "bed_assign.db"))
	for _, previous := range []domain.PreviousBed{domain.ClearPreviousBed(), must(domain.KnownPreviousBed("spot-1"))} {
		ba, err := domain.NewBedAssign("pawn", "bed", previous)
		if err != nil {
			t.Fatal(err)
		}
		action := domain.ActionID("assign-" + previous.ID())
		a, _ := domain.NewBedAssignAction(action, ba)
		id := domain.PlanID("plan-" + previous.ID())
		plan, err := domain.NewPlan(id, 1, []domain.Action{a})
		if err != nil {
			t.Fatal(err)
		}
		if err = s.CreatePlan(ctx, plan); err != nil {
			t.Fatal(err)
		}
		state, err := s.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(err)
		}
		if got, ok := state.Spec.Actions()[0].BedAssign(); !ok || got != ba {
			t.Fatal("bed assign action did not round trip", state.Spec.Actions()[0])
		}
		snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: id, Revision: 1, Native: 2}
		v := BedAssignAdmission{Snapshot: snapshot, Tick: 12, Pawn: "pawn", Bed: "bed", PreviousBedClear: previous.Clear(), PreviousBedID: previous.ID(), PawnSnapshotToken: "pawn-cas", BedSnapshotToken: "bed-cas"}
		if _, err := s.Prepare(ctx, id, action, v.Snapshot, v.Tick); err == nil {
			t.Fatal("generic prepare accepted a bed assign action")
		}
		if _, err := s.Dispatch(ctx, id, action, v.Snapshot, v.Tick); err == nil {
			t.Fatal("dispatch without admission accepted")
		}
		if _, err := s.PrepareBedAssign(ctx, id, action, v); err != nil {
			t.Fatal(err)
		}
		state, err = s.LoadPlan(ctx, id)
		if err != nil || len(state.BedAssignAdmissions) != 1 || state.BedAssignAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
			t.Fatal(state, err)
		}
		if _, err := s.Dispatch(ctx, id, action, v.Snapshot, v.Tick); err != nil {
			t.Fatal(err)
		}
	}
}

func must[T any](v T, err error) T {
	if err != nil {
		panic(err)
	}
	return v
}
