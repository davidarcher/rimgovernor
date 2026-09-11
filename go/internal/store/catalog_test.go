package store

import (
	"context"
	"path/filepath"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCatalogEmptyBoundsCancellationAndOrderedRecords(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "catalog.db"))
	empty, err := s.LoadPlans(ctx, 1)
	if err != nil || empty == nil || len(empty) != 0 {
		t.Fatal(empty, err)
	}
	for _, limit := range []int{-1, 0, 257} {
		if got, err := s.LoadPlans(ctx, limit); err == nil || got != nil {
			t.Fatal("invalid catalog bound accepted")
		}
	}
	for _, id := range []domain.PlanID{"z", "a", "m"} {
		action := domain.ActionID("action-" + string(id))
		if err = s.CreatePlan(ctx, plan(t, id, action)); err != nil {
			t.Fatal(err)
		}
		admission := evidence(10, 20)
		admission.Snapshot.Plan = id
		if _, err = s.ReserveAndPrepare(ctx, id, action, admission); err != nil {
			t.Fatal(err)
		}
	}
	if got, err := s.LoadPlans(ctx, 2); err == nil || got != nil {
		t.Fatal("overflow returned partial accounting")
	}
	states, err := s.LoadPlans(ctx, 3)
	if err != nil {
		t.Fatal(err)
	}
	for i, id := range []domain.PlanID{"a", "m", "z"} {
		want, err := s.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(err)
		}
		if states[i].Spec.ID() != id || !reflect.DeepEqual(states[i], want) {
			t.Fatal("catalog changed order or dropped typed evidence")
		}
	}
	cancelled, cancel := context.WithCancel(ctx)
	cancel()
	if got, err := s.LoadPlans(cancelled, 3); err == nil || got != nil {
		t.Fatal("cancelled catalog returned plans")
	}
}

func TestCatalogDoesNotObserveConcurrentUncommittedAdmission(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	reader := open(t, path)
	if _, err := s.ReserveAndPrepare(ctx, "p", "a", evidence(10, 40)); err != nil {
		t.Fatal(err)
	}
	// Hold an actual writer transaction with an invalid intermediate record. A
	// catalog must wait for that transaction instead of returning mixed evidence.
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback()
	if _, err = tx.ExecContext(ctx, "UPDATE admissions SET payload='invalid transient JSON' WHERE action_id='a'"); err != nil {
		t.Fatal(err)
	}
	waitCtx, cancel := context.WithTimeout(ctx, 80*time.Millisecond)
	defer cancel()
	if states, err := reader.LoadPlans(waitCtx, 1); err == nil || states != nil {
		t.Fatal("catalog escaped writer transaction")
	}
	if err = tx.Rollback(); err != nil {
		t.Fatal(err)
	}
	states, err := reader.LoadPlans(ctx, 1)
	if err != nil || len(states) != 1 || states[0].Admissions[0].Admission.Costs[0].Count != 40 {
		t.Fatal("catalog observed intermediate accounting", err)
	}
}

func TestCatalogCorruptionNeverReturnsEarlierPlans(t *testing.T) {
	ctx := context.Background()
	s, _ := fixture(t)
	if err := s.CreatePlan(ctx, plan(t, "a-first", "new-action")); err != nil {
		t.Fatal(err)
	}
	if _, err := s.ReserveAndPrepare(ctx, "p", "a", evidence(10, 40)); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec("UPDATE admissions SET payload='{}' WHERE action_id='a'"); err != nil {
		t.Fatal(err)
	}
	if states, err := s.LoadPlans(ctx, 2); err == nil || states != nil {
		t.Fatal("corrupt later plan returned partial catalog")
	}
}
