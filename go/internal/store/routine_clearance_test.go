package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestClearanceIssuedWorkRecoveryAndManual(t *testing.T) {
	t.Parallel()
	db := open(t, memoryPath(t))
	ctx := context.Background()
	r := routineRequest()
	r.Facts.Upkeep.Clearance = domain.Known([]policy.ClearanceTarget{{EntityID: "ruin", DefName: "Wall", InHome: true, Deconstructible: true}})
	g := routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions)
	// Shared journal semantics do not depend on the future upkeep action family.
	if _, err := db.CommitMethod(ctx, g.Standard.ID, g.Revision, "owned-work", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Upkeep.Clearance = domain.Known([]policy.ClearanceTarget{})
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions); g.Standard.Finding != domain.FindingMet || g.Standard.Status != domain.StandardSettled {
		t.Fatal("unissued plan invented a deficit", g)
	}
	// The unissued method settles with the recovery (#290); the renewed
	// deficit opens a new Episode and binds a fresh method.
	if p, err := db.LoadPlan(ctx, "p"); err != nil || p.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal("recovery left the unissued method open", p, err)
	}
	r.Facts.Upkeep.Clearance = domain.Known([]policy.ClearanceTarget{{EntityID: "ruin", DefName: "Wall", InHome: true, Deconstructible: true}})
	g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions)
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
	r.Facts.Upkeep.Clearance = domain.Known([]policy.ClearanceTarget{})
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions); g.Standard.Finding != domain.FindingUnmet {
		t.Fatal("issued target loss recovered need", g)
	}
	r.Enabled = false
	reviewRoutine(t, db, &r)
	r.Enabled = true
	for i := 0; i < 2; i++ {
		if g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions); g.Standard.Finding != domain.FindingUnmet {
			t.Fatal("replacement binding lost unresolved work", g)
		}
	}
	if _, err := db.RecordReceipt(ctx, "p2", "a2", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	// An applied building closes once the census shows it built (#856).
	r.Facts.CurrentConstruction = builtCensus(t, "wall")
	if g = routineGoal(t, reviewRoutine(t, db, &r), policy.ClearHomeObstructions); g.Standard.Finding != domain.FindingMet {
		t.Fatal(g)
	}
}
