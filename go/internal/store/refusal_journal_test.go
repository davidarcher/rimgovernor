package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A refused receipt journals native's code, reason and class beside the
// refusal, and the record survives a reopen.
func TestRefusedReceiptJournalsNativeRefusal(t *testing.T) {
	ctx := context.Background()
	s, path, v := billStoreFixture(t)
	if _, err := s.Prepare(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	refusal := domain.NativeRefusal{Code: "FAILURE_CODE_INVALID_REQUEST", Reason: "no bench", Class: domain.RefusalPermanent}
	results, err := s.RecordReceipts(ctx, []BatchReceipt{{Plan: "plan", Action: "bill", Attempt: 1, Receipt: domain.ReceiptRefused, Refusal: &refusal}})
	if err != nil || results[0].Err != nil {
		t.Fatal(err, results[0].Err)
	}
	if got, known := results[0].Progress.View().Refusal.Value(); !known || got != refusal {
		t.Fatalf("recorded refusal = %+v, %v; want %+v", got, known, refusal)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	states, err := open(t, path).LoadPlans(ctx)
	if err != nil || len(states) != 1 {
		t.Fatal(err, states)
	}
	if got, known := states[0].Progress[0].View().Refusal.Value(); !known || got != refusal {
		t.Fatalf("reloaded refusal = %+v, %v; want %+v", got, known, refusal)
	}
}
