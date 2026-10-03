package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// MaintainSurgery's part bills (#1168) are ProductionBillIntents committed
// as a method of the goal, so the bill admission must bind that goal (#1755).
func TestCommitSurgeryPartBillMethod(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	op := policy.SurgeryOperation{Recipe: domain.Known("InstallPegLeg"), Item: "PegLeg", PartDefName: domain.Known("Leg"), PartIndex: domain.Known(30), Kind: policy.SurgeryRestore,
		SuccessChance: domain.Known(0.9), EligibleDoctors: domain.Known(1), IngredientsOnMap: domain.Known(true), Violation: domain.Known(false), Lethal: domain.Known(false)}
	r.Facts.MedicalPawns = domain.Known([]policy.CarePawn{{ID: "patient", Dead: domain.Known(false), QueuedSurgeries: domain.Known(0), Operations: domain.Known([]policy.SurgeryOperation{op})}})
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainSurgery)
	bill, err := domain.NewProductionBill("bench", "Make_Prosthetic", domain.GearBatch, 1)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewProductionBillAction("surgery-part-0", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("surgery-part", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "part-bill", plan); err != nil {
		t.Fatal(err)
	}
}
