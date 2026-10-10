package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A deconstruction is the DECONSTRUCT Designate of its exact target; a door
// swap also asks for the wall replacement.
func TestDeconstructDesignatesTarget(t *testing.T) {
	base, err := domain.NewDeconstruction("Thing_Wall1", "Wall", domain.Cell{X: 10, Z: 12})
	if err != nil {
		t.Fatal(err)
	}
	plain, err := domain.NewDeconstructionAction("d0", base)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := IntentAction("plan/1", plain)
	if err != nil {
		t.Fatal(err)
	}
	if wire.GetDesignate().GetTarget().GetId() != "Thing_Wall1" || wire.GetDesignate().ReplaceWithWall != nil {
		t.Fatalf("%v", wire)
	}
	swap, err := domain.NewDeconstructionAction("d2", base.WithWallReplacement())
	if err != nil {
		t.Fatal(err)
	}
	if wire, err = IntentAction("plan/1", swap); err != nil || !wire.GetDesignate().GetReplaceWithWall() {
		t.Fatalf("door swap (#1245) not sent: %v %v", wire, err)
	}
}
