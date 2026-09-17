package store

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"path/filepath"
	"testing"
)

func TestRoutineGearNeedsPersistUnknownRecoveryRenewalAndManual(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "gear.db")
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
	method := plan(t, "gear-pending", "gear-action")
	if _, err := db.CommitGoalMethod(context.Background(), g.Goal.ID, g.Revision, "method", method); !errors.Is(err, ErrConflict) {
		t.Fatal("unavailable gear execution family admitted a method", err)
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
