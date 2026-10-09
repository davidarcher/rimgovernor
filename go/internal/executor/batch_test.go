package executor

import (
	"context"
	"errors"
	"fmt"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
)

type batchFixture struct {
	executor *Executor
	store    *store.Store
	env      *environment
	plan     domain.PlanID
	ids      []domain.ActionID
}

func newBatchFixture(t *testing.T, n int) *batchFixture {
	t.Helper()
	ctx := context.Background()
	clock := testkit.NewManualClock(time.Date(2026, 9, 10, 12, 0, 0, 0, time.UTC))
	journal, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = journal.Close() })
	actions := make([]domain.Action, n)
	ids := make([]domain.ActionID, n)
	for i := range n {
		building, err := domain.NewBuilding("Wall", domain.Cell{X: 10 + int32(i), Z: 10}, domain.North, "WoodLog")
		if err != nil {
			t.Fatal(err)
		}
		ids[i] = domain.ActionID(fmt.Sprintf("action-%d", i+1))
		if actions[i], err = domain.NewBuildingAction(ids[i], building, domain.TierExpand); err != nil {
			t.Fatal(err)
		}
	}
	plan, err := domain.NewPlan("plan-1", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	env := &environment{clock: clock, tick: 100}
	executor, err := New(journal, env, clock, Limits{MaxAge: time.Second, RunTimeout: 2 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	authority := Authority{Enabled: true, Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: plan.Revision(), Native: 1}}
	if err = executor.UpdateAuthority(authority); err != nil {
		t.Fatal(err)
	}
	return &batchFixture{executor, journal, env, plan.ID(), ids}
}

func (f *batchFixture) run(t *testing.T) []BatchItem {
	t.Helper()
	out, err := f.executor.RunBatch(context.Background(), f.plan, f.ids)
	if err != nil || len(out) != len(f.ids) {
		t.Fatal(err, len(out))
	}
	return out
}

func (f *batchFixture) views(t *testing.T) map[domain.ActionID]domain.ProgressView {
	t.Helper()
	state, err := f.store.LoadPlan(context.Background(), f.plan)
	if err != nil {
		t.Fatal(err)
	}
	out := map[domain.ActionID]domain.ProgressView{}
	for _, p := range state.Progress {
		out[p.View().Action] = p.View()
	}
	return out
}

// 32 building intents cost one bounds read and one Apply.
func TestRunBatchOneInspectOneApply(t *testing.T) {
	f := newBatchFixture(t, 32)
	for _, item := range f.run(t) {
		if item.Err != nil || !item.Result.NativeCalled {
			t.Fatal(item.Action, item.Err)
		}
	}
	if inspections, placements := f.env.counts(); inspections != 1 || placements != 32 || f.env.writes != 1 {
		t.Fatal("batch not one inspect and one apply", inspections, placements, f.env.writes)
	}
	for id, v := range f.views(t) {
		if v.Stage != domain.Completed || v.Unresolved {
			t.Fatalf("%s not completed: %+v", id, v)
		}
	}
}

// A mixed reply records each action's own receipt; the unknown one is sent
// again under a new attempt.
func TestRunBatchMixedReplyRecordsPerActionReceipts(t *testing.T) {
	f := newBatchFixture(t, 3)
	kinds := map[domain.ActionID]domain.Receipt{"action-1": domain.ReceiptAccepted, "action-2": domain.ReceiptRefused, "action-3": domain.ReceiptUnknown}
	f.env.onPlace = func(_ context.Context, p Placement) (Receipt, error) {
		return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: kinds[p.Action.ID()]}, nil
	}
	f.run(t)
	views := f.views(t)
	for id, want := range kinds {
		if got, _ := views[id].Receipt.Value(); got != want {
			t.Fatalf("%s receipt %s, want %s", id, got, want)
		}
	}
	if views["action-1"].Stage != domain.Completed {
		t.Fatal("applied not completed", views["action-1"])
	}
	f.env.onPlace = nil
	f.run(t)
	if f.env.writes != 2 || f.env.placements != 4 {
		t.Fatal("only the unknown attempt resends", f.env.writes, f.env.placements)
	}
	if v := f.views(t)["action-3"]; v.Stage != domain.Completed || v.Attempt != 2 {
		t.Fatalf("unknown not resent: %+v", v)
	}
}

// A batch failure leaves every dispatched attempt unknown, and the next run
// sends them all again.
func TestRunBatchFailureLeavesAllUnknownAndResends(t *testing.T) {
	f := newBatchFixture(t, 3)
	failure := errors.New("batch_failure")
	f.env.onWrite = func([]Placement) error { return failure }
	for _, item := range f.run(t) {
		if !errors.Is(item.Err, failure) {
			t.Fatal(item.Action, item.Err)
		}
	}
	for id, v := range f.views(t) {
		if got, _ := v.Receipt.Value(); got != domain.ReceiptUnknown {
			t.Fatalf("%s receipt %s after batch failure", id, got)
		}
	}
	f.env.onWrite = nil
	f.run(t)
	if f.env.writes != 2 || f.env.placements != 6 {
		t.Fatal("failed batch not resent", f.env.writes, f.env.placements)
	}
	for id, v := range f.views(t) {
		if v.Stage != domain.Completed || v.Attempt != 2 {
			t.Fatalf("%s not resent: %+v", id, v)
		}
	}
}

type vetoJournal struct {
	Journal
	veto domain.ActionID
}

func (j *vetoJournal) DispatchBatch(ctx context.Context, attempts []store.BatchAttempt) ([]store.BatchResult, error) {
	var pass []store.BatchAttempt
	for _, a := range attempts {
		if a.Action != j.veto {
			pass = append(pass, a)
		}
	}
	inner, err := j.Journal.DispatchBatch(ctx, pass)
	if err != nil {
		return nil, err
	}
	out := make([]store.BatchResult, 0, len(attempts))
	for _, a := range attempts {
		if a.Action == j.veto {
			out = append(out, store.BatchResult{Err: store.ErrActionVetoed})
			continue
		}
		out, inner = append(out, inner[0]), inner[1:]
	}
	return out, nil
}

// A gate-refused action is held and left out of the Apply.
func TestRunBatchGateRefusedActionLeftOutOfApply(t *testing.T) {
	f := newBatchFixture(t, 3)
	f.executor.journal = &vetoJournal{Journal: f.store, veto: "action-2"}
	var sent []domain.ActionID
	f.env.onWrite = func(ps []Placement) error {
		for _, p := range ps {
			sent = append(sent, p.Action.ID())
		}
		return nil
	}
	out := f.run(t)
	if len(sent) != 2 || sent[0] != "action-1" || sent[1] != "action-3" {
		t.Fatal("vetoed action sent", sent)
	}
	if !errors.Is(out[1].Err, ErrHeld) || out[1].Result.NativeCalled || out[0].Err != nil || out[2].Err != nil {
		t.Fatal(out)
	}
	views := f.views(t)
	if views["action-2"].Stage == domain.Dispatched || views["action-1"].Stage != domain.Completed {
		t.Fatal(views)
	}
}
