package store

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestGearParallelAdmissionBoundsAndClaims(t *testing.T) {
	db := open(t, memoryPath(t))
	defer db.Close()
	r := routineRequest()
	r.Policy.SetProjectLimit(2)
	r.Facts.Gear = domain.Known(policy.GearObservation{Pawns: []policy.GearPawn{{Pawn: "a", Loadout: "loadout", Deficit: domain.Known(true), Candidates: domain.Known([]policy.GearCandidate{})}}})
	g := routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainEquipment)
	admit := func(index int, pawn domain.PawnID, item string) error {
		gear, err := domain.NewGearReplace(pawn, item, "Parka")
		if err != nil {
			return err
		}
		a, err := domain.NewGearReplaceAction(domain.ActionID(fmt.Sprintf("wear-%d", index)), gear)
		if err != nil {
			return err
		}
		p, err := domain.NewPlan(domain.PlanID(fmt.Sprintf("gear-%d", index)), 1, []domain.Action{a})
		if err != nil {
			return err
		}
		next, err := db.CommitGoalMethod(context.Background(), g.Goal.ID, g.Revision, domain.MethodID(fmt.Sprintf("method-%d", index)), p)
		if err == nil {
			g = next
		}
		return err
	}
	if err := admit(1, "a", "one"); err != nil {
		t.Fatal(err)
	}
	if err := admit(2, "a", "two"); err == nil {
		t.Fatal("pawn dressed twice")
	}
	if err := admit(2, "b", "one"); err == nil {
		t.Fatal("item allocated twice")
	}
	if err := admit(2, "b", "two"); err != nil {
		t.Fatal(err)
	}
	if err := admit(3, "c", "three"); err == nil {
		t.Fatal("exceeded free slots")
	}
}

func TestRoutineGearNeedsPersistUnknownRecoveryRenewalAndManual(t *testing.T) {
	t.Parallel()
	path := memoryPath(t)
	db := open(t, path)
	r := routineRequest()
	gear := policy.GearObservation{Pawns: []policy.GearPawn{{Pawn: "pawn", Loadout: "loadout", Deficit: domain.Known(true), Candidates: domain.Known([]policy.GearCandidate{})}}}
	r.Facts.Gear = domain.Known(gear)
	out := reviewRoutine(t, db, &r)
	g := routineGoal(t, out, policy.MaintainEquipment)
	if g.Goal.Need != domain.NeedDeficit || g.Goal.Priority != 3 {
		t.Fatal(g)
	}
	epoch := g.Goal.Epoch
	db.Close()
	db = open(t, path)
	r.Facts.Gear = domain.Unknown[policy.GearObservation]()
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainEquipment)
	if g.Goal.Need != domain.NeedUnknown || g.Goal.Epoch != epoch {
		t.Fatal("missing census recovered or renewed need", g)
	}
	gear.Pawns[0].Deficit = domain.Known(false)
	r.Facts.Gear = domain.Known(gear)
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainEquipment)
	if g.Goal.Status != domain.GoalSatisfied {
		t.Fatal(g)
	}
	gear.Pawns[0].Candidates = domain.Known([]policy.GearCandidate{{Target: "replacement", Gain: .1, Definition: "Parka"}})
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainEquipment)
	if g.Goal.Need != domain.NeedDeficit || g.Goal.Epoch <= epoch {
		t.Fatal("native replacement did not reopen equipment need", g)
	}
	// The equipment goal ranks for a development slot like any other
	// optional need (#233): with the slot it admits the gear family's
	// replacement bill on a standing bench.
	bill, err := domain.NewProductionBill("bench", "Make_Apparel_BasicShirt", "bench-cas", domain.StockTarget, 1)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("gear-action", bill)
	if err != nil {
		t.Fatal(err)
	}
	method, err := domain.NewPlan("gear-pending", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := db.CommitGoalMethod(context.Background(), g.Goal.ID, g.Revision, "method", method); err != nil {
		t.Fatal("selected equipment goal refused a bill", err)
	}
	g, err = db.LoadGoal(context.Background(), g.Goal.ID)
	if err != nil || len(g.Methods) != 1 {
		t.Fatal(g, err)
	}
	r.Enabled = false
	reviewRoutine(t, db, &r)
	suspended, err := db.LoadGoal(context.Background(), g.Goal.ID)
	if err != nil || suspended.Goal.Status != domain.GoalSuspended {
		t.Fatal("Manual left the equipment need active", suspended, err)
	}
	db.Close()
	db = open(t, path)
	disabled, err := db.LoadRoutineReview(context.Background())
	if err != nil || disabled.Enabled {
		t.Fatal(disabled, err)
	}
}
