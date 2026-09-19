package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func questAcceptStoreFixture(t *testing.T) (*Store, string, QuestAcceptAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "quest_accept.db")
	s := open(t, path)
	accept, _ := domain.NewQuestAccept("quest-1", "pawn-1", 0)
	a, _ := domain.NewQuestAcceptAction("quest-accept-1", accept)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := QuestAcceptAdmission{Snapshot: snapshot, Tick: 12, Quest: "quest-1", AccepterPawn: "pawn-1", RewardChoice: 0, QuestSnapshotToken: "quest-cas"}
	return s, path, v
}

func TestQuestAcceptAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := questAcceptStoreFixture(t)
	if _, err := s.PrepareQuestAccept(ctx, "plan", "quest-accept-1", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.QuestAcceptAdmissions) != 1 || state.QuestAcceptAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "quest-accept-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestQuestAcceptDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := questAcceptStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "quest-accept-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "quest-accept-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a quest accept action")
	}
}

func TestQuestAcceptAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := questAcceptStoreFixture(t)
			if _, err := s.PrepareQuestAccept(ctx, "plan", "quest-accept-1", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "quest-accept-1", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "quest-accept-1", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE quest_accept_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestQuestAcceptAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := questAcceptStoreFixture(t)
	if _, err := s.PrepareQuestAccept(ctx, "plan", "quest-accept-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "quest-accept-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "quest-accept-1", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareQuestAccept(ctx, "plan", "quest-accept-1", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.QuestSnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareQuestAccept(ctx, "plan", "quest-accept-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "quest-accept-1"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.QuestAcceptAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
