package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func husbandryStoreFixture(t *testing.T) (*Store, string, HusbandryAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "husbandry.db")
	s := open(t, path)
	train, _ := domain.NewHusbandry("animal", domain.HusbandryTrain, "Trainability_Advanced")
	a, _ := domain.NewHusbandryAction("husbandry-1", train)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := HusbandryAdmission{Snapshot: snapshot, Tick: 12, Animal: "animal", Method: domain.HusbandryTrain, Argument: "Trainability_Advanced", AnimalSnapshotToken: "animal-cas", CensusToken: "census-cas"}
	return s, path, v
}

func TestHusbandryAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := husbandryStoreFixture(t)
	if _, err := s.PrepareHusbandry(ctx, "plan", "husbandry-1", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.HusbandryAdmissions) != 1 || state.HusbandryAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "husbandry-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestHusbandryDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := husbandryStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "husbandry-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "husbandry-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a husbandry action")
	}
}

func TestHusbandryAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := husbandryStoreFixture(t)
			if _, err := s.PrepareHusbandry(ctx, "plan", "husbandry-1", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "husbandry-1", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "husbandry-1", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE husbandry_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestHusbandryAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := husbandryStoreFixture(t)
	if _, err := s.PrepareHusbandry(ctx, "plan", "husbandry-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "husbandry-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "husbandry-1", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareHusbandry(ctx, "plan", "husbandry-1", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.AnimalSnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareHusbandry(ctx, "plan", "husbandry-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "husbandry-1"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.HusbandryAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}

func TestHusbandryTameAndReleaseActionsRoundTrip(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "husbandry-designations.db"))
	arguments := map[domain.HusbandryMethod]string{domain.HusbandryAllowedArea: "Area_3", domain.HusbandryFollowDrafted: "true", domain.HusbandryFollowFieldwork: "false"}
	for i, method := range []domain.HusbandryMethod{domain.HusbandryTame, domain.HusbandryRelease, domain.HusbandryCancelSlaughter, domain.HusbandryCancelRelease, domain.HusbandryAllowedArea, domain.HusbandryMaster, domain.HusbandryFollowDrafted, domain.HusbandryFollowFieldwork} {
		// master with an empty argument round-trips the NULL-as-clear column.
		h, _ := domain.NewHusbandry("animal", method, arguments[method])
		id := domain.ActionID("husbandry-" + string(method))
		a, _ := domain.NewHusbandryAction(id, h)
		planID := domain.PlanID("plan-" + string(method))
		plan, err := domain.NewPlan(planID, 1, []domain.Action{a})
		if err != nil {
			t.Fatal(err)
		}
		if err = s.CreatePlan(ctx, plan); err != nil {
			t.Fatal(i, err)
		}
		state, err := s.LoadPlan(ctx, planID)
		if err != nil {
			t.Fatal(err)
		}
		got, ok := state.Spec.Actions()[0].Husbandry()
		if !ok || got != h {
			t.Fatal(method, got, ok)
		}
	}
}
