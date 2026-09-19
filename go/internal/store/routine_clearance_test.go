package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestClearanceIssuedWorkRecoveryAndManual(t *testing.T) {
	t.Parallel()
	db := open(t, memoryPath(t))
	ctx := context.Background()
	r := routineRequest()
	r.Facts.Upkeep.Clearance = domain.Known([]policy.ClearanceTarget{{EntityID: "ruin", DefName: "Wall", InHome: true, Deconstructible: true}})
	g := routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions)
	// Shared journal semantics do not depend on the future upkeep action family.
	if _, err := db.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "owned-work", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Upkeep.Clearance = domain.Known([]policy.ClearanceTarget{})
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions); g.Goal.Need != domain.NeedRecovered || g.Goal.Status != domain.GoalSatisfied {
		t.Fatal("unissued plan invented a deficit", g)
	}
	// The unissued method settles with the recovery (#290); the renewed
	// deficit opens a new epoch and binds a fresh method.
	if p, err := db.LoadPlan(ctx, "p"); err != nil || p.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal("recovery left the unissued method open", p, err)
	}
	r.Facts.Upkeep.Clearance = domain.Known([]policy.ClearanceTarget{{EntityID: "ruin", DefName: "Wall", InHome: true, Deconstructible: true}})
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions)
	if _, err := db.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "owned-work", plan(t, "p2", "a2")); err != nil {
		t.Fatal(err)
	}
	scope2 := scope()
	scope2.Plan = "p2"
	if _, err := db.Prepare(ctx, "p2", "a2", scope2, r.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Dispatch(ctx, "p2", "a2", scope2, r.Tick); err != nil {
		t.Fatal(err)
	}
	r.Tick++
	r.Facts.Upkeep.Clearance = domain.Known([]policy.ClearanceTarget{})
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions); g.Goal.Need != domain.NeedDeficit {
		t.Fatal("issued target loss recovered need", g)
	}
	r.Enabled = false
	reviewRoutine(t, db, &r)
	r.Enabled = true
	for i := 0; i < 2; i++ {
		if g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions); g.Goal.Need != domain.NeedDeficit {
			t.Fatal("replacement binding lost unresolved work", g)
		}
	}
	if _, err := db.Observe(ctx, "p2", domain.Observation{Action: "a2", Attempt: 1, Snapshot: scope2, Tick: r.Tick, Effect: domain.EffectCompleted}, scope2); err != nil {
		t.Fatal(err)
	}
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions); g.Goal.Need != domain.NeedRecovered {
		t.Fatal(g)
	}
}
