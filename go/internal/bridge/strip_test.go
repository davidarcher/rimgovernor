package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// TestStripIntentWire is the bridge contract for #1117: a strip action is a
// DesignateIntent with STRIP on the exact target.
func TestStripIntentWire(t *testing.T) {
	strip, err := domain.NewStrip("Corpse_Human12")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewStripAction("strip-0", strip)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("strip is not an intent-mode kind")
	}
	wire, err := IntentAction("key-1", action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetDesignate()
	if wire.GetKey() != "key-1" || got.GetThingId() != "Corpse_Human12" || got.GetDesignation() != op.ThingDesignation_THING_DESIGNATION_STRIP {
		t.Fatalf("wire = %v", wire)
	}
}
