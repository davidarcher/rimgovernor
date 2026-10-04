package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestUseItemActionRowRoundTrips proves the actions CHECK clause accepts a
// use_item row and that it decodes back (#1038).
func TestUseItemActionRowRoundTrips(t *testing.T) {
	db := open(t, memoryPath(t))
	defer db.Close()
	r := routineRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Facts.Gear = domain.Known(policy.GearObservation{Pawns: []policy.GearPawn{gearDeficitPawn("a")}})
	g := routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainEquipment)
	use, err := domain.NewUseItem("user", "PsychicShockLance", "raider")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewUseItemAction("lance-0", use)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("lance", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.CommitMethod(context.Background(), g.Standard.ID, g.Revision, "lance-method", p); err != nil {
		t.Fatal(err)
	}
	loaded, err := db.LoadPlan(context.Background(), "lance")
	if err != nil {
		t.Fatal(err)
	}
	got, ok := loaded.Spec.Actions()[0].UseItem()
	if !ok || got != use {
		t.Fatalf("use_item row did not round-trip: %+v", loaded.Spec.Actions()[0])
	}
}
