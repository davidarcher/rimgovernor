package store

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// gearDeficitPawn is a bare woman whose loadout model wants a shirt a bill
// can supply; gearRecoveredPawn already wears one.
func gearDeficitPawn(id policy.PawnID) policy.GearPawn {
	shirt := policy.GearOption{ID: "bill:shirt", Definition: "Apparel_BasicShirt", Quality: 2, Slot: policy.GearSkinTorso, Layers: []string{"OnSkin"}, Groups: []string{"Torso"}, Source: policy.GearBillSource, Condition: 1}
	return policy.GearPawn{Pawn: id, Loadout: "loadout", LoadoutModel: domain.Known(policy.GearLoadoutInput{Female: true, Options: []policy.GearOption{shirt}})}
}

func gearRecoveredPawn(id policy.PawnID) policy.GearPawn {
	shirt := policy.GearOption{ID: "shirt", Definition: "Apparel_BasicShirt", Quality: 2, Slot: policy.GearSkinTorso, Layers: []string{"OnSkin"}, Groups: []string{"Torso"}, Source: policy.GearWorn, Condition: 1}
	return policy.GearPawn{Pawn: id, Loadout: "loadout", LoadoutModel: domain.Known(policy.GearLoadoutInput{Female: true, Worn: []policy.GearOption{shirt}})}
}

func TestGearParallelAdmissionBoundsAndClaims(t *testing.T) {
	db := open(t, memoryPath(t))
	defer db.Close()
	r := routineRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Facts.Gear = domain.Known(policy.GearObservation{Pawns: []policy.GearPawn{gearDeficitPawn("a")}})
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
	if err := admit(3, "c", "three"); err != nil {
		t.Fatal("no slot limit applies", err)
	}
}

func TestRoutineGearNeedsPersistUnknownRecoveryRenewalAndManual(t *testing.T) {
	t.Parallel()
	path := memoryPath(t)
	db := open(t, path)
	r := routineRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	gear := policy.GearObservation{Pawns: []policy.GearPawn{gearDeficitPawn("pawn")}}
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
	gear.Pawns[0] = gearRecoveredPawn("pawn")
	r.Facts.Gear = domain.Known(gear)
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainEquipment)
	if g.Goal.Status != domain.GoalSatisfied {
		t.Fatal(g)
	}
	gear.Pawns[0] = gearDeficitPawn("pawn")
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainEquipment)
	if g.Goal.Need != domain.NeedDeficit || g.Goal.Epoch <= epoch {
		t.Fatal("a new model gap did not reopen equipment need", g)
	}
	// The equipment goal ranks for a development slot like any other
	// optional need (#233): with the slot it admits the gear family's
	// replacement bill on a standing bench.
	bill, err := domain.NewProductionBill("bench", "Make_Apparel_BasicShirt", domain.StockTarget, 1)
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
	paused := reviewRoutine(t, db, &r)
	suspended, err := db.LoadGoal(context.Background(), g.Goal.ID)
	if err != nil || suspended.Goal.Status != domain.GoalActive || paused.Review.Veto(suspended.Goal) == "" {
		t.Fatal("Manual left the equipment need active", suspended, err)
	}
	db.Close()
	db = open(t, path)
	disabled, err := db.LoadRoutineReview(context.Background())
	if err != nil || disabled.Enabled {
		t.Fatal(disabled, err)
	}
}
