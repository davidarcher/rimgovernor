package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func researchSelectStoreFixture(t *testing.T) (*Store, string, ResearchSelectAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "research_select.db")
	s := open(t, path)
	value, _ := domain.NewResearchSelect("ProjectDef")
	a, _ := domain.NewResearchSelectAction("research-select", value)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	v := ResearchSelectAdmission{Snapshot: snapshot, Tick: 12, Project: "ProjectDef"}
	return s, path, v
}

func TestResearchSelectAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := researchSelectStoreFixture(t)
	if _, err := s.PrepareResearchSelect(ctx, "plan", "research-select", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.ResearchSelectAdmissions) != 1 || state.ResearchSelectAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "research-select", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestResearchSelectDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := researchSelectStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "research-select", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "research-select", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a research select action")
	}
}

func TestResearchSelectAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := researchSelectStoreFixture(t)
			if _, err := s.PrepareResearchSelect(ctx, "plan", "research-select", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "research-select", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "research-select", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE research_select_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestResearchSelectAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := researchSelectStoreFixture(t)
	if _, err := s.PrepareResearchSelect(ctx, "plan", "research-select", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "research-select", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "research-select", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareResearchSelect(ctx, "plan", "research-select", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	if _, err := s.Cancel(ctx, "plan", "research-select"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal(state, err)
	}
}
