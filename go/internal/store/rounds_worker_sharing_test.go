package store

import (
	"context"
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Multiple Concerns and a player project may queue work with one observed
// worker. Restart, revision and world checks remain authoritative.
func TestRoundsMethodsShareWorkersWithoutSlots(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Policy.ResearchLadder = []string{"Stonecutting"}
	r.Facts.Research = domain.Known(policy.ResearchFacts{Projects: []policy.ResearchProjectID{"Stonecutting"}})
	r.Facts.Workers = domain.Known(1)
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1, policy.WorkResearch: 1, policy.WorkPlantCutting: 1})
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(3)), domain.Known(int64(3))
	first := reviewRounds(t, s, &r)
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRounds(ctx)
	if err != nil || !reflect.DeepEqual(loaded, first.Review) {
		t.Fatal(loaded, err)
	}
	// A player project already queued work for the sole builder.
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

func TestHousingAssignmentNeedsNoWorkerSlot(t *testing.T) {
	t.Parallel()
	for _, workers := range []domain.Fact[int]{domain.Known(0), domain.Unknown[int]()} {
		s := open(t, memoryPath(t))
		r := roundsRequest()
		r.Policy.Stage.Floor = policy.StageDevelopment
		r.Facts.Workers = workers
		r.Facts.Labor = domain.Known(map[policy.WorkType]int{})
		r.Facts.Colonists = domain.Known(int64(3))
		r.Facts.IndoorCapacity = domain.Known(int64(3))
		r.Facts.BedCapacity = domain.Known(int64(2))
		out := reviewRounds(t, s, &r)
		g := roundsGoal(t, out, policy.MaintainHousing)
		assign, err := domain.NewAssign("pawn", "bed", domain.ClearPrevious())
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewAssignAction("assign", assign)
		if err != nil {
			t.Fatal(err)
		}
		p, err := domain.NewPlan("bed-assignment", 1, []domain.Action{action})
		if err != nil {
			t.Fatal(err)
		}
		if err = s.CommitOwnerMethod(context.Background(), g, "assign-bed", "", p); err != nil {
			t.Fatal("assignment refused without a worker slot", err)
		}
		// The open assignment blocks another method until its native outcome settles.
		g, err = s.LoadStandard(context.Background(), g.Standard.ID)
		if err != nil {
			t.Fatal(err)
		}
		if err = s.CommitOwnerMethod(context.Background(), g, "duplicate", "", plan(t, "other", "other-action")); err == nil {
			t.Fatal("open assignment did not block duplicate work")
		}
		data, err := json.Marshal(out.Review)
		if err != nil {
			t.Fatal(err)
		}
		var fields map[string]json.RawMessage
		if err = json.Unmarshal(data, &fields); err != nil {
			t.Fatal(err)
		}
		if _, present := fields["Development"]; present {
			t.Fatal("slot state persisted")
		}
		s.Close()
	}
}
