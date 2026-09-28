package executor

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
)

func init() {
	for _, kind := range []domain.ActionKind{domain.AcquisitionAction, domain.MineAcquisitionAction, domain.AcquisitionWithdrawAction} {
		domain.RegisterIntentKind(kind)
	}
}

// Acquisition, mine acquisition and a stall withdraw are plain intents
// (#1046): the applied receipt is terminal, with no admission and no
// observation.
func TestAcquireIntentAppliedIsTerminal(t *testing.T) {
	value, err := domain.NewAcquisition("plant-1", "WoodLog", domain.Cell{X: 4, Z: 5})
	if err != nil {
		t.Fatal(err)
	}
	for _, build := range []func(domain.ActionID, domain.Acquisition) (domain.Action, error){domain.NewAcquisitionAction, domain.NewMineAcquisitionAction, domain.NewAcquisitionWithdrawAction} {
		action, err := build("action-1", value)
		if err != nil {
			t.Fatal(err)
		}
		if !PlainIntent(action.Kind()) {
			t.Fatal("not a plain intent", action.Kind())
		}
		f := newFixtureAt(t, storetest.Path(t), action)
		f.env.onPlace = func(_ context.Context, p Placement) (Receipt, error) {
			if p.Action.Kind() != action.Kind() {
				t.Fatal("placed", p.Action.Kind())
			}
			return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
		}
		result, err := f.run()
		if err != nil || !result.NativeCalled {
			t.Fatal(action.Kind(), err)
		}
		if v := f.progress(t); v.Stage != domain.Completed || v.Unresolved {
			t.Fatalf("%s: applied intent not terminal: %+v", action.Kind(), v)
		}
	}
}
