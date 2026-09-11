package executor

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"testing"
)

func TestRefusalRequiresFreshAdmissionAfterRestart(t *testing.T) {
	f := newFixture(t)
	f.env.onPlace = func(_ context.Context, p Placement) (Receipt, error) {
		return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptRefused}, nil
	}
	result, err := f.run()
	if err != nil || result.Progress.View().Unresolved || result.Progress.View().Stage != domain.Pending {
		t.Fatal(err)
	}
	if err = f.store.Close(); err != nil {
		t.Fatal(err)
	}
	f.store, err = store.Open(context.Background(), f.path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.store.Close()
	f.executor.journal = f.store
	f.env.stock = 0
	if _, err = f.run(); err == nil {
		t.Fatal("refusal retry bypassed fresh resource admission")
	}
	_, placed, observed := f.env.counts()
	if placed != 1 || observed != 0 {
		t.Fatal("refusal retried without admission or required ledger lookup")
	}
	f.env.stock = 20
	result, err = f.run()
	if err != nil || result.Progress.View().Attempt != 2 {
		t.Fatal("fresh retry failed", err)
	}
}
