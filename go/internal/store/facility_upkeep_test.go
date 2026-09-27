package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func completedFacility(t *testing.T, source domain.GoalSource, proof, cancelFirst bool) (*Store, string, GoalState, domain.Building) {
	t.Helper()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	if source != domain.AutopilotGoal {
		goal, err := domain.NewGoal("player", source, 2, scope(), 10)
		if err != nil {
			t.Fatal(err)
		}
		if err = s.CreateGoal(ctx, goal); err != nil {
			t.Fatal(err)
		}
		g, err = s.ReviewGoal(ctx, goal.ID, 0, scope(), 10, domain.NeedDeficit)
		if err != nil {
			t.Fatal(err)
		}
	}
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 7}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("placed", b)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("method", scope().Revision, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	g, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "build", p)
	if err != nil {
		t.Fatal(err)
	}
	current := scope()
	current.Plan = "method"
	if _, err = s.Prepare(ctx, p.ID(), a.ID(), current, 10); err != nil {
		t.Fatal(err)
	}
	// The receipt settles at the dispatch tick (#856).
	if _, err = s.Dispatch(ctx, p.ID(), a.ID(), current, 11); err != nil {
		t.Fatal(err)
	}
	if cancelFirst {
		g, err = s.CancelGoal(ctx, g.Goal.ID, g.Revision)
		if err != nil {
			t.Fatal(err)
		}
	}
	// Without proof the intent was refused: nothing was applied to claim.
	receipt := domain.ReceiptRefused
	if proof {
		receipt = domain.ReceiptAccepted
	}
	if _, err = s.RecordReceipt(ctx, p.ID(), a.ID(), 1, receipt); err != nil {
		t.Fatal(err)
	}
	return s, path, g, b
}

func TestConstructionClaimsRejectPlayerUnprovenFutureAndCancelledWork(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"player", "unproven", "future", "cancel-before", "cancel-after"} {
		t.Run(kind, func(t *testing.T) {
			source := domain.AutopilotGoal
			if kind == "player" {
				source = domain.PlayerGoal
			}
			s, path, g, _ := completedFacility(t, source, kind != "unproven", kind == "cancel-before")
			if kind == "cancel-after" {
				if _, err := s.CancelGoal(context.Background(), g.Goal.ID, g.Revision); err != nil {
					t.Fatal(err)
				}
			}
			s.Close()
			s = open(t, path)
			defer s.Close()
			tick := domain.Tick(12)
			if kind == "future" {
				tick = 10
			}
			got, err := s.ConstructionClaims(context.Background(), scope(), tick)
			rows, known := got.Value()
			if err != nil || !known || len(rows) != 0 {
				t.Fatal(rows, known, err)
			}
		})
	}
}

func TestFacilityUpkeepDurableUnknownManualAndPlayerReplacement(t *testing.T) {
	t.Parallel()
	s, path, _, building := completedFacility(t, domain.AutopilotGoal, true, false)
	r := routineRequest()
	r.Tick = 12
	r.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "wall", Building: building, Cells: []domain.Cell{building.Cell()}, IntentKey: "placed/1"}}})
	r.Facts.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Revision: 1, Targets: []policy.HomeCoverageTarget{{ID: "wall", Shape: domain.Known("shape"), Missing: domain.Known(int64(1)), Excluded: domain.Known(int64(1)), Cells: []domain.Cell{{X: 3, Z: 7}}}}})
	r.Facts.StoneStructures = domain.Known([]policy.StoneStructure{{ID: "wall", Definition: "Wall", Flammability: domain.Known(1.0)}})
	// Home coverage follows the census alone (#719): the wall stays missing
	// Home whatever construction ownership reads; stone shell follows ownership.
	assertNeeds := func(out RoutineReviewResult, want domain.NeedState) {
		t.Helper()
		if got := routineGoal(t, out, policy.MaintainStoneShell).Goal.Need; got != want {
			t.Fatal(policy.MaintainStoneShell, got, want)
		}
		if got := routineGoal(t, out, policy.MaintainHomeCoverage).Goal.Need; got != domain.NeedDeficit {
			t.Fatal(policy.MaintainHomeCoverage, got)
		}
	}
	assertNeeds(reviewRoutine(t, s, &r), domain.NeedDeficit)
	r.Facts.CurrentConstruction = domain.Unknown[policy.CurrentConstruction]()
	out := reviewRoutine(t, s, &r)
	assertNeeds(out, domain.NeedUnknown)
	if !out.Review.Latches.StoneShell {
		t.Fatal("unknown erased active history")
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if !out.Review.Latches.StoneShell {
		t.Fatal("Manual erased completed ownership history")
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	r.Enabled = true
	assertNeeds(reviewRoutine(t, s, &r), domain.NeedUnknown)
	// A complete census now reports the owned wall gone.
	r.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	assertNeeds(reviewRoutine(t, s, &r), domain.NeedRecovered)
}

func TestRoutineReviewCannotInventConstructionOrZoneOwnership(t *testing.T) {
	t.Parallel()
	s, _, _ := goalFixture(t)
	defer s.Close()
	r := routineRequest()
	b, _ := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 7}, domain.North, "WoodLog")
	// A caller-supplied claim is replaced by the journal's; the census holds
	// no building, so nothing is owned (#719 counts census buildings).
	r.Facts.ConstructionClaims = domain.Known([]policy.ConstructionClaim{{Plan: "fake", Action: "fake", Goal: "fake", Identity: domain.ConstructionIdentity{Current: "wall"}, Building: b}})
	r.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	r.Facts.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{ID: "zone", Shape: domain.Known("shape"), Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), Cells: []domain.Cell{{X: 3, Z: 7}}}}})
	r.Facts.StoneStructures = domain.Known([]policy.StoneStructure{{ID: "wall", Definition: "Wall", Flammability: domain.Known(1.0)}})
	out := reviewRoutine(t, s, &r)
	for _, id := range []domain.GoalID{policy.MaintainHomeCoverage, policy.MaintainStoneShell} {
		if got := routineGoal(t, out, id).Goal.Need; got != domain.NeedRecovered {
			t.Fatal(id, got)
		}
	}
}
