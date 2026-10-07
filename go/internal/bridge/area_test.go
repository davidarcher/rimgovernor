package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// An area action builds one AreaIntent (#1321): a bot area carries its key,
// the home area carries home and no key.
func TestAreaBuildsAreaIntent(t *testing.T) {
	for _, tc := range []struct {
		op   domain.AreaOperation
		key  string
		want o.AreaOperation
	}{
		{domain.AreaCreate, "safe", o.AreaOperation_AREA_OPERATION_CREATE},
		{domain.AreaSetCells, "", o.AreaOperation_AREA_OPERATION_SET_CELLS},
		{domain.AreaClearCells, "safe", o.AreaOperation_AREA_OPERATION_CLEAR_CELLS},
	} {
		value, err := domain.NewArea(tc.op, tc.key, []domain.Cell{{X: 4, Z: 2}, {X: 1, Z: 9}})
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewAreaAction("a1", value)
		if err != nil {
			t.Fatal(err)
		}
		if !action.Kind().IntentMode() {
			t.Fatal("area is not an intent kind")
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		a := wire.GetArea()
		if a.GetOperation() != tc.want || a.GetKey() != tc.key || a.GetHome() != (tc.key == "") || (a.Key != nil) == (a.Home != nil) ||
			len(a.GetRects()) != 2 || a.GetRects()[0].GetMinX() != 4 || a.GetRects()[0].GetMinZ() != 2 || a.GetRects()[1].GetMaxZ() != 9 {
			t.Fatalf("%v", wire)
		}
	}
	del, _ := domain.NewArea(domain.AreaDelete, "safe", nil)
	action, _ := domain.NewAreaAction("d", del)
	wire, err := IntentAction("plan/1", action)
	if err != nil || wire.GetArea().GetOperation() != o.AreaOperation_AREA_OPERATION_DELETE || len(wire.GetArea().GetRects()) != 0 {
		t.Fatal(wire, err)
	}
}
