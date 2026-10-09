package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A remove_production_bill builds one RemoveProductionBillIntent with
// the bench and the native bill id.
func TestRemoveProductionBillBuildsIntent(t *testing.T) {
	value, err := domain.NewRemoveProductionBill("Bench_1", "Bill_Production_77")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRemoveProductionBillAction("r1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("remove_production_bill is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	if r := wire.GetRemoveProductionBill(); r.GetBenchId() != "Bench_1" || r.GetBill().GetId() != "Bill_Production_77" {
		t.Fatalf("%v", wire)
	}
}

// Only a removal asking for it sets cancel_unfinished on the wire.
func TestRemoveProductionBillCancelUnfinishedOnWire(t *testing.T) {
	value, err := domain.NewRemoveProductionBill("Bench_1", "Bill_Production_77")
	if err != nil {
		t.Fatal(err)
	}
	for _, cancel := range []bool{false, true} {
		v := value
		if cancel {
			v = v.CancelUnfinished()
		}
		action, err := domain.NewRemoveProductionBillAction("r1", v)
		if err != nil {
			t.Fatal(err)
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		if got := wire.GetRemoveProductionBill().GetCancelUnfinished(); got != cancel {
			t.Fatalf("cancel %v sent as %v", cancel, got)
		}
	}
}
