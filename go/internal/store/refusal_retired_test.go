package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The refusal budget is derived from journaled refusals, so a refusal must
// stay readable by plan ID once its plan is retired (LoadPlans skips retired
// plans; LoadPlan does not).
func TestRefusalStaysReadableAfterItsPlanRetires(t *testing.T) {
	ctx := context.Background()
	s, _, v := billStoreFixture(t)
	if _, err := s.Prepare(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	refusal := domain.NativeRefusal{Code: "FAILURE_CODE_INVALID_REQUEST", Reason: "no bench", Class: domain.RefusalPermanent}
	if _, err := s.RecordReceipts(ctx, []BatchReceipt{{Plan: "plan", Action: "bill", Attempt: 1, Receipt: domain.ReceiptRefused, Refusal: &refusal}}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, "UPDATE plans SET retired=1 WHERE id='plan'"); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || !state.Retired {
		t.Fatal(state.Retired, err)
	}
	if got, known := state.Progress[0].View().Refusal.Value(); !known || got != refusal {
		t.Fatalf("retired plan refusal = %+v, %v; want %+v", got, known, refusal)
	}
}
