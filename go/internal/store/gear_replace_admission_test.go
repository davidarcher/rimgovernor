package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func gearReplaceStoreFixture(t *testing.T) (*Store, string, GearReplaceAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "gear_replace.db")
	s := open(t, path)
	replace, _ := domain.NewGearReplace("unarmed", "thing", "Apparel_Parka")
	a, _ := domain.NewGearReplaceAction("gear-replace", replace)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	v := GearReplaceAdmission{Snapshot: snapshot, Tick: 12, Pawn: "unarmed", Thing: "thing", Definition: "Apparel_Parka", PawnSnapshotToken: "pawn-cas", ThingSnapshotToken: "thing-cas", LoadoutToken: "loadout-cas"}
	return s, path, v
}

func TestGearReplaceAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := gearReplaceStoreFixture(t)
	if _, err := s.PrepareGearReplace(ctx, "plan", "gear-replace", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.GearReplaceAdmissions) != 1 || state.GearReplaceAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "gear-replace", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestGearReplaceDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := gearReplaceStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "gear-replace", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "gear-replace", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a gear replace action")
	}
}

func TestGearReplaceAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := gearReplaceStoreFixture(t)
			if _, err := s.PrepareGearReplace(ctx, "plan", "gear-replace", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "gear-replace", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "gear-replace", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE gear_replace_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestGearReplaceAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := gearReplaceStoreFixture(t)
	if _, err := s.PrepareGearReplace(ctx, "plan", "gear-replace", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "gear-replace", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "gear-replace", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareGearReplace(ctx, "plan", "gear-replace", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.PawnSnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareGearReplace(ctx, "plan", "gear-replace", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "gear-replace"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.GearReplaceAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
