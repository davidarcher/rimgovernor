package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestReplaceLedgerRowsKeepsOnlyTheLatestReview(t *testing.T) {
	ctx := context.Background()
	s, err := Open(ctx, filepath.Join(t.TempDir(), "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	first := policy.NewLedgerView(policy.LedgerViewReconciled, 100)
	first.Orphans = append(first.Orphans, policy.LedgerBillView{ID: "B1", Bench: "T1", Recipe: "Make_Hat", State: policy.OrphanPending, GraceLeft: 2})
	if err = s.ReplaceLedgerRows(ctx, 100, first.Rows()); err != nil {
		t.Fatal(err)
	}
	rows, err := s.LedgerRows(ctx)
	if err != nil || len(rows) != 2 || rows[0].Section != policy.LedgerSectionOrphan || rows[0].Cells["bill"] != "B1" || rows[1].Section != policy.LedgerSectionRound || rows[1].Tick != 100 {
		t.Fatalf("rows %+v err %v", rows, err)
	}
	second := policy.NewLedgerView(policy.LedgerViewInactive, 160)
	if err = s.ReplaceLedgerRows(ctx, 160, second.Rows()); err != nil {
		t.Fatal(err)
	}
	if rows, err = s.LedgerRows(ctx); err != nil || len(rows) != 1 || rows[0].Cells["status"] != policy.LedgerViewInactive || rows[0].Tick != 160 {
		t.Fatalf("rows %+v err %v", rows, err)
	}
}
