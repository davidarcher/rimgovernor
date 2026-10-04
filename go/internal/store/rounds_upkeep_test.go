package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoundsUpkeepRetainsEmergencyAcrossUnknownManualAndRestart(t *testing.T) {
	t.Parallel()
	path := memoryPath(t)
	db := open(t, path)
	r := roundsRequest()
	out := reviewRounds(t, db, &r)
	if g := roundsGoal(t, out, policy.MaintainFireSafety); g.Standard.Priority != 4 || g.Standard.Finding != domain.FindingUnclear || len(out.Emergency) != 0 {
		t.Fatal(g, out.Emergency)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	out = reviewRounds(t, db, &r)
	g := roundsGoal(t, out, policy.MaintainFireSafety)
	epoch := g.Standard.Episode
	if g.Standard.Priority != 1 || g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	// The review names the need behind the emergency: a home fire vetoes
	// every development goal, and the rows alone only say so (#221).
	if len(out.Emergency) != 1 || out.Emergency[0] != policy.MaintainFireSafety {
		t.Fatal("emergency source not reported", out.Emergency)
	}
	if g := roundsGoal(t, out, policy.MaintainResource); out.Review.Veto(g.Standard) == "" {
		t.Fatal("fire emergency left a priority-3 goal active", g)
	}
	r.Enabled = false
	reviewRounds(t, db, &r)
	db.Close()
	db = open(t, path)
	loaded, err := db.LoadRounds(context.Background())
	if err != nil || loaded.Enabled || !loaded.Latches.Upkeep.Fire {
		t.Fatal(loaded, err)
	}
	r.Enabled = true
	r.Facts.Upkeep.Fires = domain.Unknown[[]policy.UpkeepFire]()
	g = roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety)
	if g.Standard.Priority != 1 || g.Standard.Finding != domain.FindingUnclear {
		t.Fatal("unknown erased observed fire risk", g)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	g = roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety)
	if g.Standard.Status != domain.StandardSettled {
		t.Fatal(g)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "new-fire", Home: true, Size: domain.Known(.5)}})
	g = roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety)
	if g.Standard.Finding != domain.FindingUnmet || g.Standard.Episode <= epoch {
		t.Fatal("renewed fire not reopened", g)
	}
	// Caller-supplied pending work is not trusted as a native obligation.
	r.Facts.UpkeepIssued = map[policy.ConcernID]bool{policy.MaintainFireSafety: true}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	if g = roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety); g.Standard.Finding != domain.FindingMet {
		t.Fatal(g)
	}
}

func TestRoundsUpkeepIssuedWorkCannotRecoverFromTargetDisappearance(t *testing.T) {
	t.Parallel()
	db := open(t, memoryPath(t))
	ctx := context.Background()
	r := roundsRequest()
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	g := roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety)
	// Shared journal semantics do not depend on the future upkeep action family.
	if _, err := db.CommitMethod(ctx, g.Standard.ID, g.Revision, "owned-work", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	if g = roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety); g.Standard.Finding != domain.FindingMet || g.Standard.Status != domain.StandardSettled {
		t.Fatal("unissued plan invented a deficit", g)
	}
	// The unissued method settles with the recovery (#290); the renewed
	// deficit opens a new Episode and binds a fresh method.
	if p, err := db.LoadPlan(ctx, "p"); err != nil || p.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal("recovery left the unissued method open", p, err)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	g = roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety)
	if _, err := db.CommitMethod(ctx, g.Standard.ID, g.Revision, "owned-work", plan(t, "p2", "a2")); err != nil {
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
	if g = roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety); g.Standard.Finding != domain.FindingUnmet {
		t.Fatal("issued target loss recovered need", g)
	}
	r.Enabled = false
	reviewRounds(t, db, &r)
	r.Enabled = true
	for i := 0; i < 2; i++ {
		if g = roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety); g.Standard.Finding != domain.FindingUnmet {
			t.Fatal("replacement binding lost unresolved work", g)
		}
	}
	if _, err := db.RecordReceipt(ctx, "p2", "a2", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	// An applied building closes once the census shows it built (#856).
	r.Facts.CurrentConstruction = builtCensus(t, "wall")
	if g = roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainFireSafety); g.Standard.Finding != domain.FindingMet {
		t.Fatal(g)
	}
}
