package executor

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
)

// An applied production_bill receipt that names a native bill journals the id
// on the action's progress (#2410); one that names none journals none.
func TestAppliedBillReceiptJournalsTheBillID(t *testing.T) {
	for _, tc := range []struct{ name, bill string }{{"named", "Bill_Production_77"}, {"unnamed", ""}} {
		bill, err := domain.NewProductionBill("Bench_1", "Make_Pemmican", domain.StockTarget, 20)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewProductionBillAction("bill-1", bill)
		if err != nil {
			t.Fatal(err)
		}
		f := newFixtureAt(t, storetest.Path(t), action)
		f.env.onPlace = func(_ context.Context, p Placement) (Receipt, error) {
			return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted, Bill: tc.bill}, nil
		}
		if _, err := f.run(); err != nil {
			t.Fatal(tc.name, err)
		}
		view := f.progress(t)
		id, known := view.Bill.Value()
		if view.Stage != domain.Completed || known != (tc.bill != "") || id != tc.bill {
			t.Fatal(tc.name, view)
		}
	}
}
