package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestDropEquipmentActionRowRoundTrips proves the actions CHECK clause
// accepts a drop_equipment row and that it decodes back (#1740).
func TestDropEquipmentActionRowRoundTrips(t *testing.T) {
	db := open(t, memoryPath(t))
	defer db.Close()
	r := roundsRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Facts.Gear = domain.Known(policy.GearObservation{Pawns: []policy.GearPawn{gearDeficitPawn("a")}})
	g := roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainEquipment)
	drop, err := domain.NewDropEquipment("joiner", "Gun_Revolver7")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewDropEquipmentAction("drop-0", drop)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("drop", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.CommitMethod(context.Background(), g.Standard.ID, g.Revision, "drop-method", p); err != nil {
		t.Fatal(err)
	}
	loaded, err := db.LoadPlan(context.Background(), "drop")
	if err != nil {
		t.Fatal(err)
	}
	got, ok := loaded.Spec.Actions()[0].DropEquipment()
	if !ok || got != drop {
		t.Fatalf("drop_equipment row did not round-trip: %+v", loaded.Spec.Actions()[0])
	}
}
