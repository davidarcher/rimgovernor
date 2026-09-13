package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func prisonerInteractionStoreFixture(t *testing.T) (*Store, string, PrisonerInteractionAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "prisoner_interaction.db")
	s := open(t, path)
	recruit, _ := domain.NewPrisonerInteraction("prisoner", domain.PrisonerInteractionRecruit)
	a, _ := domain.NewPrisonerInteractionAction("prisoner-interaction-1", recruit)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	v := PrisonerInteractionAdmission{Snapshot: snapshot, Tick: 12, Pawn: "prisoner", Interaction: domain.PrisonerInteractionRecruit, PawnSnapshotToken: "prisoner-cas"}
	return s, path, v
}

func TestPrisonerInteractionAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := prisonerInteractionStoreFixture(t)
	if _, err := s.PreparePrisonerInteraction(ctx, "plan", "prisoner-interaction-1", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.PrisonerInteractionAdmissions) != 1 || state.PrisonerInteractionAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "prisoner-interaction-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestPrisonerInteractionDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := prisonerInteractionStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "prisoner-interaction-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "prisoner-interaction-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a prisoner interaction action")
	}
}

func TestPrisonerInteractionAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := prisonerInteractionStoreFixture(t)
			if _, err := s.PreparePrisonerInteraction(ctx, "plan", "prisoner-interaction-1", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "prisoner-interaction-1", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "prisoner-interaction-1", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE prisoner_interaction_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestPrisonerInteractionAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := prisonerInteractionStoreFixture(t)
	if _, err := s.PreparePrisonerInteraction(ctx, "plan", "prisoner-interaction-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "prisoner-interaction-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "prisoner-interaction-1", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PreparePrisonerInteraction(ctx, "plan", "prisoner-interaction-1", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.PawnSnapshotToken = "refreshed-after-absence"
	if _, err := s.PreparePrisonerInteraction(ctx, "plan", "prisoner-interaction-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "prisoner-interaction-1"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.PrisonerInteractionAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
