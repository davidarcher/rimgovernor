package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func cleanStoreFixture(t *testing.T) (*Store, string, CleanAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "clean.db")
	s := open(t, path)
	clean, _ := domain.NewClean("pawn", "filth", domain.Cell{X: 3, Z: 4})
	a, _ := domain.NewCleanAction("clean", clean)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := CleanAdmission{Snapshot: snapshot, Tick: 12, Pawn: "pawn", Filth: "filth", Cell: domain.Cell{X: 3, Z: 4}, PawnSnapshotToken: "pawn-cas", FilthSnapshotToken: "filth-cas"}
	return s, path, v
}

func TestCleanAdmissionPrepareAndLoad(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := cleanStoreFixture(t)
	if _, err := s.PrepareClean(ctx, "plan", "clean", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.CleanAdmissions) != 1 || state.CleanAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "clean", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestCleanDispatchRequiresCurrentAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := cleanStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "clean", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "clean", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a clean action")
	}
}

func TestCleanAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	t.Parallel()
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := cleanStoreFixture(t)
			if _, err := s.PrepareClean(ctx, "plan", "clean", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "clean", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "clean", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
			if effect == domain.EffectUnsuccessful {
				observation.UnsuccessfulReason = domain.NativeFailure
			}
			if _, err := s.Observe(ctx, "plan", observation, v.Snapshot); err != nil {
				t.Fatal(err)
			}
			v.Tick = observation.Tick + 1
			data, err := json.Marshal(v)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE clean_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestCleanAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, v := cleanStoreFixture(t)
	if _, err := s.PrepareClean(ctx, "plan", "clean", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "clean", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "clean", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareClean(ctx, "plan", "clean", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.PawnSnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareClean(ctx, "plan", "clean", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "clean"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.CleanAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
