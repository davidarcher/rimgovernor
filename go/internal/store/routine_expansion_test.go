package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestExpansionDurableRenewalUnknownAndPlayerCapacity(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Policy.SetProjectLimit(1)
	r.Facts.Colonists = domain.Known(int64(3))
	r.Facts.BedCapacity = domain.Known(int64(3))
	r.Facts.IndoorCapacity = domain.Known(int64(3))
	r.Facts.Wood = domain.Known(int64(500))
	out := reviewRoutine(t, s, &r)
	first := routineGoal(t, out, policy.EnsureExpansion)
	if first.Goal.Need != domain.NeedDeficit || !developmentRow(t, out.Review, policy.EnsureExpansion).Selected {
		t.Fatal(out)
	}
	if _, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "expansion-competition")); err != nil {
		t.Fatal(err)
	}
	out = reviewRoutine(t, s, &r)
	if row := developmentRow(t, out.Review, policy.EnsureExpansion); row.Selected || row.Reason != policy.DevelopmentCapacity {
		t.Fatal(row)
	}
	r.Facts.IndoorCapacity = domain.Unknown[int64]()
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.EnsureExpansion); g.Goal.Need != domain.NeedUnknown || g.Goal.Epoch != first.Goal.Epoch {
		t.Fatal(g)
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || loaded.Enabled || loaded.Revision != out.Review.Revision {
		t.Fatal(loaded, err)
	}
	r.Enabled = true
	r.Facts.IndoorCapacity = domain.Known(int64(4))
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.EnsureExpansion); g.Goal.Need != domain.NeedRecovered {
		t.Fatal(g)
	}
	recovered := routineGoal(t, out, policy.EnsureExpansion)
	r.Facts.Colonists = domain.Known(int64(4))
	r.Facts.BedCapacity = domain.Known(int64(4))
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.EnsureExpansion); g.Goal.Need != domain.NeedDeficit || g.Goal.Epoch <= recovered.Goal.Epoch {
		t.Fatal(g, recovered)
	}
}

func TestRoutineCapabilitiesPreserveCommittedExpansion(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	r.Policy.SetProjectLimit(1)
	r.Facts.Colonists = domain.Known(int64(3))
	r.Facts.BedCapacity = domain.Known(int64(3))
	r.Facts.IndoorCapacity = domain.Known(int64(3))
	r.Facts.AvailableMethods = domain.Known([]policy.GoalID{policy.EnsureExpansion})
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureExpansion)
	if !developmentRow(t, out.Review, policy.EnsureExpansion).Selected {
		t.Fatal(out)
	}
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "expansion", plan(t, "expansion", "additional-place")); err != nil {
		t.Fatal(err)
	}
	r.Facts.AvailableMethods = domain.Known([]policy.GoalID{})
	out = reviewRoutine(t, s, &r)
	row := developmentRow(t, out.Review, policy.EnsureExpansion)
	if row.Selected || !row.Committed || row.Reason != policy.DevelopmentCommitted {
		t.Fatal(row)
	}
	if got := routineGoal(t, out, policy.EnsureExpansion); got.Goal.Need != domain.NeedDeficit || len(got.Methods) != 1 {
		t.Fatal(got)
	}
}
