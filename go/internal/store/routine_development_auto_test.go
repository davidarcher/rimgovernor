package store

import (
	"context"
	"errors"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Automatic mode (#649) through the review and admission transaction:
// more than two projects fit distinct workers, admission refits against
// commitments read inside it (an intervening player project, an earlier
// admission, a retry, a stale review), the record survives a restart, and
// a switch back to an explicit limit keeps the open work it admitted.
func TestRoutineDevelopmentAutoAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects, r.Policy.AutoDevelopment = policy.MaxAutoDevelopmentProjects, true
	r.Policy.ResearchTarget = "Stonecutting"
	r.Facts.Research = domain.Known(policy.ResearchFacts{Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Workers = domain.Known(4)
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1, policy.WorkResearch: 1, policy.WorkPlantCutting: 1})
	r.Facts.WorkerCensus = domain.Known([]policy.DevelopmentWorker{
		{ID: "builder", Work: []policy.WorkType{policy.WorkConstruction}},
		{ID: "cutter", Work: []policy.WorkType{policy.WorkPlantCutting}},
		{ID: "hauler", Work: []policy.WorkType{policy.WorkHauling}},
		{ID: "scholar", Work: []policy.WorkType{policy.WorkResearch}},
	})
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(2)), domain.Known(int64(3))
	first := reviewRoutine(t, s, &r)
	d := first.Review.Development
	for _, need := range []domain.GoalID{policy.EnsureExpansion, policy.EnsureResearch, policy.MaintainWood} {
		if !developmentRow(t, first.Review, need).Selected {
			t.Fatal(need, d.Rows)
		}
	}
	if !d.Auto || d.Census == nil || len(*d.Census) != 4 || d.Unused == nil || *d.Unused != 1 || d.Capacity != 4 {
		t.Fatalf("%+v", d)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded, first.Review) {
		t.Fatal(loaded, err)
	}
	// A player project accepted since the ranking took the only builder.
	sub, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "builder"))
	if err != nil {
		t.Fatal(err)
	}
	expansion := routineGoal(t, first, policy.EnsureExpansion)
	if _, err = s.CommitGoalMethod(ctx, expansion.Goal.ID, expansion.Revision, "wall", plan(t, "wall", "wall-action")); !errors.Is(err, ErrNotAdmitted) || !strings.Contains(err.Error(), string(policy.DevelopmentLabor)) {
		t.Fatal("builder double-spent", err)
	}
	research := routineGoal(t, first, policy.EnsureResearch)
	if _, err = s.CommitGoalMethod(ctx, research.Goal.ID, research.Revision, "study", plan(t, "study", "study-action")); err != nil {
		t.Fatal(err)
	}
	// A retry of the same admission is refused, not duplicated.
	if _, err = s.CommitGoalMethod(ctx, research.Goal.ID, research.Revision, "study", plan(t, "study", "study-action")); err == nil {
		t.Fatal("retry admitted twice")
	}
	wood := routineGoal(t, first, policy.MaintainWood)
	if _, err = s.CommitGoalMethod(ctx, wood.Goal.ID, wood.Revision, "cut", plan(t, "cut", "cut-action")); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Cancel(ctx, sub.Plan, sub.Action); err != nil {
		t.Fatal(err)
	}
	// Switching to an explicit limit of one keeps the two admitted projects
	// open; nothing new is selected.
	r.Policy.MaxDevelopmentProjects, r.Policy.AutoDevelopment = 1, false
	r.Tick += 10
	explicit := reviewRoutine(t, s, &r)
	if len(explicit.Review.Development.Committed) != 2 || explicit.Review.Development.Auto {
		t.Fatalf("%+v", explicit.Review.Development)
	}
	if row := developmentRow(t, explicit.Review, policy.EnsureExpansion); row.Selected || row.Reason != policy.DevelopmentCapacity {
		t.Fatal(row)
	}
	// A reviewed goal from another load is refused on the snapshot.
	r.Policy.MaxDevelopmentProjects, r.Policy.AutoDevelopment = policy.MaxAutoDevelopmentProjects, true
	r.Current.Load = "reloaded"
	reviewRoutine(t, s, &r)
	stale := routineGoal(t, explicit, policy.EnsureExpansion)
	if _, err = s.CommitGoalMethod(ctx, stale.Goal.ID, stale.Revision, "wall", plan(t, "wall2", "wall2-action")); err == nil {
		t.Fatal("stale review admitted")
	}
}
