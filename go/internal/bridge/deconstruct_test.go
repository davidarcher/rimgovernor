package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A deconstruction with cleared ground (#1366) sends its rectangles on the
// DECONSTRUCT Designate; one without sends none.
func TestDeconstructCarriesClearedGround(t *testing.T) {
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
	if len(wire.GetDesignate().GetClearedGround()) != 0 || wire.GetDesignate().GetTarget().GetId() != "Thing_Wall1" {
		t.Fatalf("%v", wire)
	}
	cleared, err := base.WithClearedGround([]domain.GroundRect{{Origin: domain.Cell{X: 8, Z: 9}, Width: 6, Height: 5}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewDeconstructionAction("d1", cleared)
	if err != nil {
		t.Fatal(err)
	}
	wire, err = IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	g := wire.GetDesignate().GetClearedGround()
	if len(g) != 1 || g[0].GetOrigin().GetX() != 8 || g[0].GetOrigin().GetZ() != 9 || g[0].GetWidth() != 6 || g[0].GetHeight() != 5 {
		t.Fatalf("%v", wire)
	}
	if wire.GetDesignate().ReplaceWithWall != nil {
		t.Fatalf("plain deconstruction sent a wall swap: %v", wire)
	}
	swap, err := domain.NewDeconstructionAction("d2", cleared.WithWallReplacement())
	if err != nil {
		t.Fatal(err)
	}
	if wire, err = IntentAction("plan/1", swap); err != nil || !wire.GetDesignate().GetReplaceWithWall() {
		t.Fatalf("door swap (#1245) not sent: %v %v", wire, err)
	}
	if _, err := base.WithClearedGround([]domain.GroundRect{{Origin: domain.Cell{X: 1, Z: 1}, Width: 0, Height: 2}}); err == nil {
		t.Fatal("accepted an empty rectangle")
	}
}
