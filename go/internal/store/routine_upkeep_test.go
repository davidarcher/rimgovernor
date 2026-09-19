package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestRoutineUpkeepRetainsEmergencyAcrossUnknownManualAndRestart(t *testing.T) {
	t.Parallel()
	path := memoryPath(t)
	db := open(t, path)
	r := routineRequest()
	out := reviewRoutine(t, db, &r)
	if g := routineGoal(t, out, policy.MaintainFireSafety); g.Goal.Priority != 4 || g.Goal.Need != domain.NeedUnknown || len(out.Emergency) != 0 {
		t.Fatal(g, out.Emergency)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	out = reviewRoutine(t, db, &r)
	g := routineGoal(t, out, policy.MaintainFireSafety)
	epoch := g.Goal.Epoch
	if g.Goal.Priority != 1 || g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
	// The review names the need behind the emergency: a home fire suspends
	// every development goal, and the rows alone only say so (#221).
	if len(out.Emergency) != 1 || out.Emergency[0] != policy.MaintainFireSafety {
		t.Fatal("emergency source not reported", out.Emergency)
	}
	if g := routineGoal(t, out, policy.MaintainWood); g.Goal.Status != domain.GoalSuspended {
		t.Fatal("fire emergency left a priority-3 goal active", g)
	}
	r.Enabled = false
	reviewRoutine(t, db, &r)
	db.Close()
	db = open(t, path)
	loaded, err := db.LoadRoutineReview(context.Background())
	if err != nil || loaded.Enabled || !loaded.Latches.Upkeep.Fire {
		t.Fatal(loaded, err)
	}
	r.Enabled = true
	r.Facts.Upkeep.Fires = domain.Unknown[[]policy.UpkeepFire]()
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety)
	if g.Goal.Priority != 1 || g.Goal.Need != domain.NeedUnknown {
		t.Fatal("unknown erased observed fire risk", g)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety)
	if g.Goal.Status != domain.GoalSatisfied {
		t.Fatal(g)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "new-fire", Home: true, Size: domain.Known(.5)}})
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety)
	if g.Goal.Need != domain.NeedDeficit || g.Goal.Epoch <= epoch {
		t.Fatal("renewed fire not reopened", g)
	}
	// Caller-supplied pending work is not trusted as a native obligation.
	r.Facts.UpkeepIssued = map[policy.GoalID]bool{policy.MaintainFireSafety: true}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety); g.Goal.Need != domain.NeedRecovered {
		t.Fatal(g)
	}
}

func TestRoutineUpkeepIssuedWorkCannotRecoverFromTargetDisappearance(t *testing.T) {
	t.Parallel()
	db := open(t, memoryPath(t))
	ctx := context.Background()
	r := routineRequest()
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	g := routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety)
	// Shared journal semantics do not depend on the future upkeep action family.
	if _, err := db.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "owned-work", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety); g.Goal.Need != domain.NeedRecovered || g.Goal.Status != domain.GoalSatisfied {
		t.Fatal("unissued plan invented a deficit", g)
	}
	// The unissued method settles with the recovery (#290); the renewed
	// deficit opens a new epoch and binds a fresh method.
	if p, err := db.LoadPlan(ctx, "p"); err != nil || p.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal("recovery left the unissued method open", p, err)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety)
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
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety); g.Goal.Need != domain.NeedDeficit {
		t.Fatal("issued target loss recovered need", g)
	}
	r.Enabled = false
	reviewRoutine(t, db, &r)
	r.Enabled = true
	for i := 0; i < 2; i++ {
		if g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety); g.Goal.Need != domain.NeedDeficit {
			t.Fatal("replacement binding lost unresolved work", g)
		}
	}
	if _, err := db.Observe(ctx, "p2", domain.Observation{Action: "a2", Attempt: 1, Snapshot: scope2, Tick: r.Tick, Effect: domain.EffectCompleted}, scope2); err != nil {
		t.Fatal(err)
	}
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety); g.Goal.Need != domain.NeedRecovered {
		t.Fatal(g)
	}
}
