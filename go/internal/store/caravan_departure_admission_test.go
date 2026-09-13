package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func caravanDepartureStoreFixture(t *testing.T) (*Store, string, CaravanDepartureAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "caravan_departure.db")
	s := open(t, path)
	departure, _ := domain.NewCaravanDeparture([]domain.PawnID{"alpha", "beta"}, []domain.CargoItem{{Definition: "MealSimple", Count: 10}}, 42)
	a, _ := domain.NewCaravanDepartureAction("caravan-departure", departure)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	v := CaravanDepartureAdmission{Snapshot: snapshot, Tick: 12, Crew: []CaravanCrewAdmission{{Pawn: "alpha", SnapshotToken: "cas-alpha"}, {Pawn: "beta", SnapshotToken: "cas-beta"}}, CatalogToken: "catalog-cas"}
	return s, path, v
}

func TestCaravanDepartureAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := caravanDepartureStoreFixture(t)
	if _, err := s.PrepareCaravanDeparture(ctx, "plan", "caravan-departure", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.CaravanDepartureAdmissions) != 1 || !reflect.DeepEqual(state.CaravanDepartureAdmissions[0].Admission, v) || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "caravan-departure", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestCaravanDepartureDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := caravanDepartureStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "caravan-departure", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "caravan-departure", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a caravan departure action")
	}
}

func TestCaravanDepartureAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := caravanDepartureStoreFixture(t)
			if _, err := s.PrepareCaravanDeparture(ctx, "plan", "caravan-departure", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "caravan-departure", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "caravan-departure", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE caravan_departure_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestCaravanDepartureAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := caravanDepartureStoreFixture(t)
	if _, err := s.PrepareCaravanDeparture(ctx, "plan", "caravan-departure", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "caravan-departure", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "caravan-departure", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareCaravanDeparture(ctx, "plan", "caravan-departure", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.Crew[0].SnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareCaravanDeparture(ctx, "plan", "caravan-departure", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "caravan-departure"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || !reflect.DeepEqual(state.CaravanDepartureAdmissions[0].Admission, v) {
		t.Fatal(state, err)
	}
}
