package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"path/filepath"
	"testing"
)

func TestRoutineUpkeepRetainsEmergencyAcrossUnknownManualAndRestart(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "upkeep.db")
	db := open(t, path)
	r := routineRequest()
	out := reviewRoutine(t, db, &r)
	if g := routineGoal(t, out, policy.MaintainFireSafety); g.Goal.Priority != 4 || g.Goal.Need != domain.NeedUnknown {
		t.Fatal(g)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	g := routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety)
	epoch := g.Goal.Epoch
	if g.Goal.Priority != 1 || g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
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
	db := open(t, filepath.Join(t.TempDir(), "issued.db"))
	ctx := context.Background()
	r := routineRequest()
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	g := routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety)
	// Shared journal semantics do not depend on the future upkeep action family.
	if _, err := db.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "owned-work", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety); g.Goal.Need != domain.NeedRecovered {
		t.Fatal("unissued plan invented a deficit", g)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	reviewRoutine(t, db, &r)
	if _, err := db.Prepare(ctx, "p", "a", scope(), r.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Dispatch(ctx, "p", "a", scope(), r.Tick); err != nil {
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
	if _, err := db.Observe(ctx, "p", domain.Observation{Action: "a", Attempt: 1, Snapshot: scope(), Tick: r.Tick, Effect: domain.EffectCompleted}, scope()); err != nil {
		t.Fatal(err)
	}
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.MaintainFireSafety); g.Goal.Need != domain.NeedRecovered {
		t.Fatal(g)
	}
}
