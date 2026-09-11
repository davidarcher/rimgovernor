package store

import (
	"context"
	"errors"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDependencyPersistenceAndGuardedExecution(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "dependencies.db")
	s := open(t, path)
	base := plan(t, "p", "a", "b")
	p, e := domain.NewPlan(base.ID(), base.Revision(), base.Actions(), domain.ActionDependency{Action: "b", Requires: "a"})
	if e != nil {
		t.Fatal(e)
	}
	if e = s.CreatePlan(ctx, p); e != nil {
		t.Fatal(e)
	}
	s.Close()
	s = open(t, path)
	loaded, e := s.LoadPlan(ctx, "p")
	if e != nil || !reflect.DeepEqual(p.Dependencies(), loaded.Spec.Dependencies()) {
		t.Fatal(loaded, e)
	}
	if _, e = s.Prepare(ctx, "p", "b", scope(), 10); !errors.Is(e, domain.ErrDependency) {
		t.Fatal(e)
	}
	admission := Admission{Snapshot: scope(), Tick: 10, Costs: []MaterialCost{}, Footprint: []domain.Cell{{X: 3, Z: 7}}}
	if _, e = s.ReserveAndPrepare(ctx, "p", "b", admission); !errors.Is(e, domain.ErrDependency) {
		t.Fatal(e)
	}
	prepare(t, s, "a")
	if _, e = s.Dispatch(ctx, "p", "a", scope(), 10); e != nil {
		t.Fatal(e)
	}
	if _, e = s.RecordReceipt(ctx, "p", "a", 1, domain.ReceiptAccepted); e != nil {
		t.Fatal(e)
	}
	if _, e = s.Prepare(ctx, "p", "b", scope(), 11); !errors.Is(e, domain.ErrDependency) {
		t.Fatal("receipt advanced work", e)
	}
	if _, e = s.Observe(ctx, "p", domain.Observation{Action: "a", Attempt: 1, Snapshot: scope(), Tick: 11, Effect: domain.EffectCompleted}, scope()); e != nil {
		t.Fatal(e)
	}
	admission.Tick = 11
	if _, e = s.ReserveAndPrepare(ctx, "p", "b", admission); e != nil {
		t.Fatal(e)
	}
	if _, e = s.Dispatch(ctx, "p", "b", scope(), 11); e != nil {
		t.Fatal(e)
	}
}

func TestDependencyDispatchRechecksAfterPreparation(t *testing.T) {
	ctx := context.Background()
	s, _ := fixture(t)
	prepare(t, s, "b")
	// Inject a legal prerequisite edge after preparation to exercise dispatch's
	// independent guard; production plan intent is immutable.
	if _, e := s.db.ExecContext(ctx, "INSERT INTO action_dependencies VALUES('p','b','a')"); e != nil {
		t.Fatal(e)
	}
	if _, e := s.Dispatch(ctx, "p", "b", scope(), 10); !errors.Is(e, domain.ErrDependency) {
		t.Fatal("dispatch bypassed prerequisite", e)
	}
	if _, e := s.Cancel(ctx, "p", "b"); e != nil {
		t.Fatal("dependency blocked cancellation", e)
	}
}

func TestDependencyCorruptCrossPlanRefusedOnLoad(t *testing.T) {
	ctx := context.Background()
	s, _ := fixture(t)
	if e := s.CreatePlan(ctx, plan(t, "other", "outside")); e != nil {
		t.Fatal(e)
	}
	if _, e := s.db.ExecContext(ctx, "INSERT INTO action_dependencies VALUES('p','b','outside')"); e != nil {
		t.Fatal(e)
	}
	if _, e := s.LoadPlan(ctx, "p"); e == nil {
		t.Fatal("cross-plan dependency accepted")
	}
}
