package store

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func routineProject(t *testing.T, r RoundsResult, need domain.ConcernID) ProjectState {
	t.Helper()
	for i, b := range r.Review.Projects {
		if b.Need == need {
			return r.Projects[i]
		}
	}
	t.Fatal("missing routine project", need)
	return ProjectState{}
}

// A finished Project stays finished through an unknown measurement and,
// once broken, opens a new Project row instead of bumping its epoch (#1022).
func TestRoutineProjectFinishesAndRegressionOpensNewRow(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Facts.Cooking = domain.Known(true)
	first := routineProject(t, reviewRoutine(t, s, &r), policy.EnsureCooking)
	if first.Project.Status != domain.ProjectCompleted {
		t.Fatal(first)
	}
	r.Facts.Cooking = domain.Unknown[bool]()
	if p := routineProject(t, reviewRoutine(t, s, &r), policy.EnsureCooking); p.Project.ID != first.Project.ID || p.Project.Status != domain.ProjectCompleted {
		t.Fatal("unknown reopened a finished project", p)
	}
	r.Facts.Cooking = domain.Known(false)
	next := routineProject(t, reviewRoutine(t, s, &r), policy.EnsureCooking)
	if next.Project.ID == first.Project.ID || next.Project.Status != domain.ProjectOpen || next.Project.Need != domain.NeedDeficit {
		t.Fatal(next)
	}
	old, err := s.LoadProject(t.Context(), first.Project.ID)
	if err != nil || old.Project.Status != domain.ProjectCompleted {
		t.Fatal("finished record lost", old, err)
	}
}

// Only Standards are goal rows: a Project or Response need never creates one,
// however the review measures it.
func TestProjectAndResponseKindsNeverMintGoalRows(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Facts.Cooking = domain.Known(false)
	out := reviewRoutine(t, s, &r)
	r.Facts.Cooking = domain.Known(true)
	reviewRoutine(t, s, &r)
	// The review binds Standards as goals and Projects as Projects; no
	// Response is bound as either (Responses are incidents).
	for _, b := range out.Review.Goals {
		if c := policy.ConcernTypeOf(b.Need); c != policy.StandardConcern {
			t.Fatalf("goal binding %s is a %s kind", b.Need, c)
		}
	}
	for _, b := range out.Review.Projects {
		if !policy.IsProjectKind(b.Need) {
			t.Fatalf("project binding %s is a %s kind", b.Need, policy.ConcernTypeOf(b.Need))
		}
	}
	if len(out.Review.Projects) == 0 {
		t.Fatal("review bound no Projects")
	}
	tx, err := s.begin(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback()
	ids, err := goalIDs(t.Context(), tx)
	if err != nil {
		t.Fatal(err)
	}
	for _, id := range ids {
		for _, d := range policy.AllInspections() {
			if d.Type == policy.StandardConcern {
				continue
			}
			if k := string(d.Concern); strings.Contains(string(id), "-"+k+"-") || strings.HasSuffix(string(id), "-"+k) {
				t.Fatalf("goal row %s is a %s kind %s", id, d.Type, k)
			}
		}
	}
	if len(ids) == 0 {
		t.Fatal("review filed no goals")
	}
}
