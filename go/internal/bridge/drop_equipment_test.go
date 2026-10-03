package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestDropEquipmentIntentWire (#1740): a drop is a give-job of the DropWeapon
// token on the held weapon, with no options and no second target.
func TestDropEquipmentIntentWire(t *testing.T) {
	drop, err := domain.NewDropEquipment("Human1", "Gun_Revolver7")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewDropEquipmentAction("drop-0", drop)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("drop_equipment is not an intent-mode kind")
	}
	wire, err := IntentAction("key-1", action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetGiveJob()
	ids := refIDs(got)
	if wire.GetKey() != "key-1" || got.GetPawn().GetId() != "Human1" || got.GetJob() != JobDropWeapon || len(ids) != 1 || ids[0] != "Gun_Revolver7" || got.GetOptions() != nil {
		t.Fatalf("wire = %v", wire)
	}
	if _, err := dropEquipmentAction(domain.Action{}); err == nil {
		t.Fatal("a non-drop action encoded as a drop")
	}
}
