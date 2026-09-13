package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func equipStoreFixture(t *testing.T) (*Store, string, EquipAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "equip.db")
	s := open(t, path)
	cell := domain.Cell{X: 1, Z: 1}
	equip, _ := domain.NewEquip("unarmed", "thing", "Gun_Revolver", cell)
	a, _ := domain.NewEquipAction("equip", equip)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	v := EquipAdmission{Snapshot: snapshot, Tick: 12, Pawn: "unarmed", Thing: "thing", Definition: "Gun_Revolver", Cell: cell, PawnSnapshotToken: "pawn-cas", ThingSnapshotToken: "thing-cas"}
	return s, path, v
}

func TestEquipAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := equipStoreFixture(t)
	if _, err := s.PrepareEquip(ctx, "plan", "equip", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.EquipAdmissions) != 1 || state.EquipAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "equip", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestEquipDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := equipStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "equip", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "equip", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted an equip action")
	}
}

func TestEquipAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := equipStoreFixture(t)
			if _, err := s.PrepareEquip(ctx, "plan", "equip", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "equip", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "equip", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE equip_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestEquipAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := equipStoreFixture(t)
	if _, err := s.PrepareEquip(ctx, "plan", "equip", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "equip", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "equip", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareEquip(ctx, "plan", "equip", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.PawnSnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareEquip(ctx, "plan", "equip", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "equip"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.EquipAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
