package store

import (
	"context"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// evidence is a prepare scope at tick; building intents carry no cost (#856).
func evidence(tick domain.Tick, _ int64) Admission {
	return Admission{Snapshot: scope(), Tick: tick}
}

func TestCatalogEmptyCancellationAndOrderedRecords(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	empty, err := s.LoadPlans(ctx)
	if err != nil || empty == nil || len(empty) != 0 {
		t.Fatal(empty, err)
	}
	for _, id := range []domain.PlanID{"z", "a", "m"} {
		action := domain.ActionID("action-" + string(id))
		if err = s.CreatePlan(ctx, plan(t, id, action)); err != nil {
			t.Fatal(err)
		}
		admission := evidence(10, 20)
		admission.Snapshot.Plan = id
		if _, err = s.Prepare(ctx, id, action, admission.Snapshot, admission.Tick); err != nil {
			t.Fatal(err)
		}
	}
	states, err := s.LoadPlans(ctx)
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
	if got, err := s.LoadPlans(cancelled); err == nil || got != nil {
		t.Fatal("cancelled catalog returned plans")
	}
}

func TestCatalogDoesNotObserveConcurrentUncommittedAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path := fixture(t)
	reader := open(t, path)
	if _, err := s.Prepare(ctx, "p", "a", evidence(10, 40).Snapshot, evidence(10, 40).Tick); err != nil {
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
	if states, err := reader.LoadPlans(waitCtx); err == nil || states != nil {
		t.Fatal("catalog escaped writer transaction")
	}
	if err = tx.Rollback(); err != nil {
		t.Fatal(err)
	}
	states, err := reader.LoadPlans(ctx)
	if err != nil || len(states) != 1 {
		t.Fatal("catalog observed intermediate accounting", err)
	}
}

func TestCatalogCorruptionNeverReturnsEarlierPlans(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	if err := s.CreatePlan(ctx, plan(t, "a-first", "new-action")); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Prepare(ctx, "p", "a", evidence(10, 40).Snapshot, evidence(10, 40).Tick); err != nil {
		t.Fatal(err)
	}
	// A building intent records no admission row (#856): corrupt its transition.
	if _, err := s.db.Exec("UPDATE transitions SET payload='{}' WHERE action_id='a'"); err != nil {
		t.Fatal(err)
	}
	if states, err := s.LoadPlans(ctx); err == nil || states != nil {
		t.Fatal("corrupt later plan returned partial catalog")
	}
}

func TestPlanHistoryWithMethodsIsANewestFirstWindowIncludingRetiredPlans(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	if _, err := s.PlanHistoryWithMethods(ctx, 8); err == nil {
		t.Fatal("empty pattern list accepted")
	}
	for _, limit := range []int{0, 257} {
		if _, err := s.PlanHistoryWithMethods(ctx, limit, "*-shell"); err == nil {
			t.Fatal("invalid history bound accepted")
		}
	}
	empty, err := s.PlanHistoryWithMethods(ctx, 8, "*-shell")
	if err != nil || len(empty) != 0 {
		t.Fatal(empty, err)
	}
	// Committed in this order; IDs sort the other way so the window is
	// proven to follow commit order, not ID order.
	for _, id := range []domain.PlanID{"shell-c", "other-b", "shell-b", "bare", "shell-a"} {
		if err = s.CreatePlan(ctx, plan(t, id, domain.ActionID("action-"+string(id)))); err != nil {
			t.Fatal(err)
		}
		if _, err = s.db.ExecContext(ctx, "UPDATE plans SET method_id=? WHERE id=?", map[bool]string{true: "shelter-shell", false: "shelter-beds"}[id[0] == 's'], id); err != nil {
			t.Fatal(err)
		}
	}
	if _, err = s.db.ExecContext(ctx, "UPDATE plans SET retired=1 WHERE id='shell-b'"); err != nil {
		t.Fatal(err)
	}
	states, err := s.PlanHistoryWithMethods(ctx, 8, "*-shell")
	if err != nil {
		t.Fatal(err)
	}
	var got []domain.PlanID
	for _, state := range states {
		got = append(got, state.Spec.ID())
	}
	if !reflect.DeepEqual(got, []domain.PlanID{"shell-a", "shell-b", "shell-c"}) || !states[1].Retired {
		t.Fatal("history is not newest first with retired plans included:", got)
	}
	window, err := s.PlanHistoryWithMethods(ctx, 2, "*-shell")
	if err != nil || len(window) != 2 || window[0].Spec.ID() != "shell-a" || window[1].Spec.ID() != "shell-b" {
		t.Fatal("window did not keep the newest plans:", window, err)
	}
}
