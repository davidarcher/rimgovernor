package executor

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// bridge registers haul as an intent-mode kind at init; this package does
// not import bridge.
func init() { domain.RegisterIntentKind(domain.HaulAction) }

type haulEnvironment struct {
	*environment
	inspected, dispatched int
	uncertain             bool
	receipt               domain.Receipt
}

func (n *haulEnvironment) InspectHaul(_ context.Context, target Target) (HaulInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return HaulInspection{StartedAt: now, ObservedAt: now, Snapshot: target.Snapshot, Tick: n.tick}, nil
}
func (n *haulEnvironment) WriteHaul(_ context.Context, p Placement) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	kind := n.receipt
	if kind == "" {
		kind = domain.ReceiptAccepted
	}
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: kind}, nil
}

func haulFixture(t *testing.T) (*fixture, *haulEnvironment) {
	t.Helper()
	f := newFixture(t)
	haul, _ := domain.NewHaul("hauler", "thing", "MealSimple", domain.Cell{X: 1, Z: 1})
	action, _ := domain.NewHaulAction("haul-1", haul)
	plan, _ := domain.NewPlan("hauls", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &haulEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableHaul(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

// An applied haul is ordered, and its receipt completes the action; where
// the item went is the next planner review's to read.
func TestHaulAppliedReceiptCompletes(t *testing.T) {
	f, n := haulFixture(t)
	result, err := f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || result.Progress.View().Unresolved || n.dispatched != 1 || n.inspected != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	if result.Progress.View().Tick != n.tick {
		t.Fatalf("dispatched at %d, want the read tick %d", result.Progress.View().Tick, n.tick)
	}
}

// Native refused the intent against live state: the action fails and the
// owning routine replans.
func TestHaulRefusedReceiptFails(t *testing.T) {
	f, n := haulFixture(t)
	n.receipt = domain.ReceiptRefused
	result, err := f.run()
	if err != nil || result.Progress.View().Stage != domain.Unsuccessful || n.dispatched != 1 {
		t.Fatal(result, err)
	}
}

// A lost reply leaves the outcome unknown; the idempotent intent is sent
// again on the next run.
func TestHaulLostReplyResends(t *testing.T) {
	f, n := haulFixture(t)
	n.uncertain = true
	result, err := f.run()
	if err == nil || result.Progress.View().Stage != domain.Pending || n.dispatched != 1 {
		t.Fatal(result, err)
	}
	n.uncertain = false
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 2 {
		t.Fatal(result, err, n.dispatched)
	}
}
