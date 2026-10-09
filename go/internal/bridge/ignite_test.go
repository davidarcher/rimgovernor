package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An ignite builds one IgniteIntent with the pawn and cell.
func TestIgniteBuildsIntent(t *testing.T) {
	value, err := domain.NewIgnite("Human12", domain.Cell{X: 3, Z: 9})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewIgniteAction("i1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("ignite is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	if r := wire.GetIgnite(); r.GetPawnId() != "Human12" || r.GetCell().GetX() != 3 || r.GetCell().GetZ() != 9 {
		t.Fatalf("%v", wire)
	}
}

func TestIgniteRejectsInvalid(t *testing.T) {
	if _, err := domain.NewIgnite("", domain.Cell{X: 1, Z: 1}); err == nil {
		t.Fatal("empty pawn accepted")
	}
	if _, err := domain.NewIgnite("Human12", domain.Cell{X: -1, Z: 1}); err == nil {
		t.Fatal("negative cell accepted")
	}
}
