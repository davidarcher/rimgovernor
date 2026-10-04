package store

import (
	"context"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Automatic mode (#649) through the review and admission transaction:
// more than two projects fit distinct workers, admission refits against
// commitments read inside it (an intervening player project, an earlier
// admission, a retry, a stale review), and the record survives a restart.
func TestRoundsDevelopmentAutoAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Policy.ResearchLadder = []string{"Stonecutting"}
	r.Facts.Research = domain.Known(policy.ResearchFacts{Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Workers = domain.Known(4)
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1, policy.WorkResearch: 1, policy.WorkPlantCutting: 1})
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(3)), domain.Known(int64(3))
	first := reviewRounds(t, s, &r)
	d := first.Review.Development
	for _, need := range []domain.ConcernID{policy.MaintainHousing, policy.EnsureResearch, policy.MaintainResource} {
		if !developmentRow(t, first.Review, need).Selected {
			t.Fatal(need, d.Rows)
		}
	}
	if d.Capacity != 4 {
		t.Fatalf("%+v", d)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRounds(ctx)
	if err != nil || !reflect.DeepEqual(loaded, first.Review) {
		t.Fatal(loaded, err)
	}
	// A player project accepted since the ranking took the only builder.
	sub, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "builder"))
	if err != nil {
		t.Fatal(err)
	}
	expansion := roundsGoal(t, first, policy.MaintainHousing)
	if _, err = s.CommitMethod(ctx, expansion.Standard.ID, expansion.Revision, "wall", plan(t, "wall", "wall-action")); err != nil {
		t.Fatal("builder limit refused a goal", err)
	}
	research := roundsProject(t, first, policy.EnsureResearch)
	if _, err = s.CommitProjectMethod(ctx, research.Project.ID, research.Revision, "study", "", plan(t, "study", "study-action")); err != nil {
		t.Fatal(err)
	}
	// A retry of the same admission is refused, not duplicated.
	if _, err = s.CommitProjectMethod(ctx, research.Project.ID, research.Revision, "study", "", plan(t, "study", "study-action")); err == nil {
		t.Fatal("retry admitted twice")
	}
	wood := roundsGoal(t, first, policy.MaintainResource)
	if _, err = s.CommitMethod(ctx, wood.Standard.ID, wood.Revision, "cut", plan(t, "cut", "cut-action")); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Cancel(ctx, sub.Plan, sub.Action); err != nil {
		t.Fatal(err)
	}
	// A reviewed goal from another load is refused on the snapshot.
	r.Current.Load = "reloaded"
	reviewRounds(t, s, &r)
	stale := roundsGoal(t, first, policy.MaintainHousing)
	if _, err = s.CommitMethod(ctx, stale.Standard.ID, stale.Revision, "wall", plan(t, "wall2", "wall2-action")); err == nil {
		t.Fatal("stale review admitted")
	}
}
