package store

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func batchOf(actions ...domain.ActionID) []BatchAttempt {
	var out []BatchAttempt
	for _, a := range actions {
		out = append(out, BatchAttempt{Plan: "p", Action: a, Snapshot: scope(), Tick: 10})
	}
	return out
}

// A gated action drops out of the batch with its own error; the rest land.
func TestBatchDropsGatedActionAndAdvancesRest(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	base := plan(t, "p", "floor", "bed", "wall")
	p, err := domain.NewPlan(base.ID(), base.Revision(), base.Actions(), domain.ActionDependency{Action: "bed", Requires: "floor"})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, p); err != nil {
		t.Fatal(err)
	}
	for _, step := range []func(context.Context, []BatchAttempt) ([]BatchResult, error){s.PrepareBatch, s.DispatchBatch} {
		results, err := step(ctx, batchOf("floor", "bed", "wall"))
		if err != nil {
			t.Fatal(err)
		}
		if results[0].Err != nil || results[2].Err != nil {
			t.Fatal(results[0].Err, results[2].Err)
		}
		if !errors.Is(results[1].Err, domain.ErrDependency) {
			t.Fatal("gated action admitted", results[1].Err)
		}
	}
	receipts, err := s.RecordReceipts(ctx, []BatchReceipt{
		{Plan: "p", Action: "floor", Attempt: 1, Receipt: domain.ReceiptAccepted},
		{Plan: "p", Action: "bed", Attempt: 1, Receipt: domain.ReceiptAccepted},
		{Plan: "p", Action: "wall", Attempt: 1, Receipt: domain.ReceiptRefused},
	})
	if err != nil {
		t.Fatal(err)
	}
	if receipts[0].Err != nil || receipts[2].Err != nil || receipts[1].Err == nil {
		t.Fatal(receipts[0].Err, receipts[1].Err, receipts[2].Err)
	}
	state, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	for i, want := range []domain.Receipt{domain.ReceiptAccepted, "", domain.ReceiptRefused} {
		view := state.Progress[i].View()
		if got, _ := view.Receipt.Value(); got != want {
			t.Fatalf("%s receipt %q, want %q", view.Action, got, want)
		}
		if want != "" && view.Unresolved {
			t.Fatalf("%s still unresolved", view.Action)
		}
	}
}

func TestConcurrentDispatchBatchExactlyOneWins(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	s, path := fixture(t)
	other := open(t, path)
	if _, err := s.PrepareBatch(ctx, batchOf("a", "b")); err != nil {
		t.Fatal(err)
	}
	start := make(chan struct{})
	results := make(chan []BatchResult, 2)
	var wg sync.WaitGroup
	for _, db := range []*Store{s, other} {
		wg.Add(1)
		go func(db *Store) {
			defer wg.Done()
			<-start
			out, err := db.DispatchBatch(ctx, batchOf("a", "b"))
			if err != nil {
				out = []BatchResult{{Err: err}, {Err: err}}
			}
			results <- out
		}(db)
	}
	close(start)
	wg.Wait()
	close(results)
	wins := [2]int{}
	for out := range results {
		for i, r := range out {
			if r.Err == nil {
				wins[i]++
			}
		}
	}
	if wins != [2]int{1, 1} {
		t.Fatalf("dispatches authorized per action %v", wins)
	}
	state, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range state.Progress[:2] {
		if p.View().Attempt != 1 || !p.View().Unresolved {
			t.Fatal("lost update", p.View().Action)
		}
	}
}
