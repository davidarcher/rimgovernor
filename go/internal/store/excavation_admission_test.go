package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func excavationStoreFixture(t *testing.T) (*Store, string, ExcavationAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "excavation.db")
	s := open(t, path)
	excavation, _ := domain.NewExcavation(domain.Cell{X: 3, Z: 4}, "Granite")
	a, _ := domain.NewExcavationAction("dig-1", excavation)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := ExcavationAdmission{Snapshot: snapshot, Tick: 12, Cell: domain.Cell{X: 3, Z: 4}, Definition: "Granite", SnapshotToken: "excavate-cas"}
	return s, path, v
}

func TestExcavationAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := excavationStoreFixture(t)
	if _, err := s.PrepareExcavation(ctx, "plan", "dig-1", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.ExcavationAdmissions) != 1 || state.ExcavationAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "dig-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestExcavationDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := excavationStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "dig-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "dig-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted an excavation action")
	}
}

func TestExcavationAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := excavationStoreFixture(t)
			if _, err := s.PrepareExcavation(ctx, "plan", "dig-1", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "dig-1", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "dig-1", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE excavation_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestExcavationAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := excavationStoreFixture(t)
	if _, err := s.PrepareExcavation(ctx, "plan", "dig-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "dig-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "dig-1", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareExcavation(ctx, "plan", "dig-1", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.SnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareExcavation(ctx, "plan", "dig-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "dig-1"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.ExcavationAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}

func TestExcavationAdmissionRejectsForeignCellOrDefinition(t *testing.T) {
	ctx := context.Background()
	s, _, v := excavationStoreFixture(t)
	wrong := v
	wrong.Cell = domain.Cell{X: 4, Z: 4}
	if _, err := s.PrepareExcavation(ctx, "plan", "dig-1", wrong); err == nil {
		t.Fatal("foreign cell admitted")
	}
	wrong = v
	wrong.Definition = "Marble"
	if _, err := s.PrepareExcavation(ctx, "plan", "dig-1", wrong); err == nil {
		t.Fatal("foreign definition admitted")
	}
}
