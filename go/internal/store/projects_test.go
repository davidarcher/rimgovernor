package store

import (
	"context"
	"encoding/json"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const testProjectID domain.ProjectID = "project-00000000-EnsureCooking-0"

func projectFixture(t *testing.T) (*Store, ProjectState) {
	t.Helper()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "projects.db"))
	r := routineRequest()
	reviewRoutine(t, s, &r)
	p, err := domain.NewProject(testProjectID, policy.EnsureCooking, domain.AutopilotGoal, 2, scope(), 10)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreateProject(ctx, p); err != nil {
		t.Fatal(err)
	}
	state, err := s.ReviewProject(ctx, p.ID, 0, scope(), 10, domain.NeedDeficit)
	if err != nil {
		t.Fatal(err)
	}
	return s, state
}

func TestProjectMethodRoundTripBlobAndRebuild(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, state := projectFixture(t)
	id := domain.MintPlanID()
	state, err := s.CommitProjectMethod(ctx, state.Project.ID, state.Revision, "stove", "no cooking bill", plan(t, id, domain.ActionID(id+"-0")))
	if err != nil {
		t.Fatal(err)
	}
	if len(state.Methods) != 1 || state.Methods[0].Plan != id || state.Admitted != 1 || len(state.History) != 1 || state.Revision != 2 {
		t.Fatalf("committed %+v", state)
	}
	if m, ok, err := s.PlanGoalMethod(ctx, id); err != nil || !ok || m.Project != state.Project.ID || m.Goal != policy.EnsureCooking || m.Reason != "no cooking bill" {
		t.Fatal("plan method", m, err)
	}
	// The same method twice, or a method over open work, is refused.
	if _, err = s.CommitProjectMethod(ctx, state.Project.ID, state.Revision, "stove", "", plan(t, "again", "again-0")); err == nil {
		t.Fatal("second method admitted over open work")
	}
	// The blob is {schemaVersion, project, revision} under project/<id>.
	blobs, err := s.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	key := GovernorProjectKeyPrefix + string(state.Project.ID)
	var shape map[string]json.RawMessage
	if json.Unmarshal([]byte(blobs[key]), &shape) != nil || len(shape) != 3 || shape["schemaVersion"] == nil || shape["project"] == nil || shape["revision"] == nil {
		t.Fatal("project blob shape", blobs[key])
	}
	// One orphan pass over the goal and the project: both plans are handed
	// over, both method rows go, both owners come back from the save.
	g, err := domain.NewGoal("routine-0000000000000000-MaintainHousing-0", domain.AutopilotGoal, 2, scope(), 10)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreateGoal(ctx, g); err != nil {
		t.Fatal(err)
	}
	gs, err := s.ReviewGoal(ctx, g.ID, 0, scope(), 10, domain.NeedDeficit)
	if err != nil {
		t.Fatal(err)
	}
	gid := domain.MintPlanID()
	if _, err = s.CommitGoalMethod(ctx, gs.Goal.ID, gs.Revision, "shell", plan(t, gid, domain.ActionID(gid+"-0"))); err != nil {
		t.Fatal(err)
	}
	if blobs, err = s.GovernorStateBlobs(ctx); err != nil {
		t.Fatal(err)
	}
	passes := 0
	var seen []domain.PlanID
	if err = s.RebuildGoals(ctx, blobs, func(_ context.Context, plans []PlanState) error {
		passes++
		for _, p := range plans {
			seen = append(seen, p.Spec.ID())
		}
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if passes != 1 || len(seen) != 2 {
		t.Fatal("orphan pass", passes, seen)
	}
	var rows, live int
	if err = s.db.QueryRowContext(ctx, "SELECT COUNT(*) FROM goal_methods").Scan(&rows); err != nil || rows != 0 {
		t.Fatal("method rows survived the rebuild", rows, err)
	}
	if err = s.db.QueryRowContext(ctx, "SELECT COUNT(*) FROM plans WHERE retired=0").Scan(&live); err != nil || live != 0 {
		t.Fatal("plans survived the rebuild", live, err)
	}
	back, err := s.LoadProject(ctx, state.Project.ID)
	if err != nil || len(back.Methods) != 0 || back.Project != state.Project || back.Revision != state.Revision {
		t.Fatal("project not rebuilt from its blob", back, err)
	}
	// A project absent from the save is deleted; a stale schema is refused.
	delete(blobs, key)
	if err = s.RebuildGoals(ctx, blobs, nil); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LoadProject(ctx, state.Project.ID); !errors.Is(err, ErrNotFound) {
		t.Fatal("absent project kept", err)
	}
	stale, _ := json.Marshal(GovernorProjectBlob{SchemaVersion: GovernorStateSchemaVersion - 1, Project: state.Project, Revision: 1})
	if err = s.RebuildGoals(ctx, map[string]string{key: string(stale)}, nil); err == nil {
		t.Fatal("stale project blob accepted")
	}
}

func TestProjectFinishCancelRetireAndOwnerKey(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, state := projectFixture(t)
	// A method row binds exactly one owner, and a project row's epoch is 0.
	if _, err := s.db.ExecContext(ctx, "INSERT INTO goal_methods(goal_id,project_id,epoch,method_id,plan_id,priority) VALUES('a','b','0','m','p',1)"); err == nil {
		t.Fatal("two-owner row accepted")
	}
	p := plan(t, "project-plan", "project-action")
	state, err := s.CommitProjectMethod(ctx, state.Project.ID, state.Revision, "stove", "", p)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.ExecContext(ctx, "INSERT INTO goal_methods(project_id,epoch,method_id,plan_id,priority) VALUES(?,'1','m','project-plan',1)", state.Project.ID); err == nil {
		t.Fatal("project row with epoch 1 accepted")
	}
	// Cancelling cancels the work and the row stays at the new revision.
	if _, err = s.CancelProject(ctx, state.Project.ID, state.Revision-1); !errors.Is(err, ErrConflict) {
		t.Fatal("stale revision cancelled", err)
	}
	cancelled, err := s.CancelProject(ctx, state.Project.ID, state.Revision)
	if err != nil || cancelled.Project.Status != domain.ProjectCancelled {
		t.Fatal(cancelled.Project, err)
	}
	loaded, err := s.LoadPlan(ctx, p.ID())
	if err != nil || domain.GoalWorkOpen(loaded.Progress) {
		t.Fatal("cancel left the method open", err)
	}
	if _, err = s.CommitProjectMethod(ctx, state.Project.ID, cancelled.Revision, "late", "", plan(t, "late", "late-0")); err == nil {
		t.Fatal("cancelled project admitted a method")
	}
	// Retirement takes only invalidated autopilot projects without open work.
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback()
	if err = retireProjects(ctx, tx, nil); err != nil {
		t.Fatal(err)
	}
	if kept, err := loadProject(ctx, tx, state.Project.ID); err != nil || kept.Retired {
		t.Fatal("cancelled project retired", err)
	}
	tx.Rollback()
	past := scope()
	past.Load = "another-load"
	rewound, err := s.ReviewProject(ctx, state.Project.ID, cancelled.Revision, past, 20, domain.NeedDeficit)
	if err != nil || rewound.Project.Status != domain.ProjectCancelled {
		t.Fatal("cancelled project reviewed", rewound.Project, err)
	}
}

func TestProjectInvalidatedByWorldChangeRetires(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, state := projectFixture(t)
	other := scope()
	other.Colony = "another-colony"
	invalid, err := s.ReviewProject(ctx, state.Project.ID, state.Revision, other, 20, domain.NeedDeficit)
	if err != nil || invalid.Project.Status != domain.ProjectInvalidated {
		t.Fatal(invalid.Project, err)
	}
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback()
	if err = retireProjects(ctx, tx, map[domain.ProjectID]bool{invalid.Project.ID: true}); err != nil {
		t.Fatal(err)
	}
	if kept, err := loadProject(ctx, tx, invalid.Project.ID); err != nil || kept.Retired {
		t.Fatal("retained project retired", err)
	}
	if err = retireProjects(ctx, tx, nil); err != nil {
		t.Fatal(err)
	}
	if gone, err := loadProject(ctx, tx, invalid.Project.ID); err != nil || !gone.Retired {
		t.Fatal("invalidated project kept in capacity", gone.Retired, err)
	}
	var active int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM projects WHERE retired=0").Scan(&active); err != nil || active != 0 {
		t.Fatal(active, err)
	}
}

func TestProjectFinishesOnceAndBlobSkipsRetired(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, state := projectFixture(t)
	done, err := s.ReviewProject(ctx, state.Project.ID, state.Revision, scope(), 20, domain.NeedRecovered)
	if err != nil || done.Project.Status != domain.ProjectFinished {
		t.Fatal(done.Project, err)
	}
	if !domain.ProjectRegressed(done.Project, domain.NeedDeficit, false) {
		t.Fatal("regress rule moved")
	}
	if _, err = s.ReviewProject(ctx, done.Project.ID, done.Revision, scope(), 30, domain.NeedDeficit); err == nil {
		t.Fatal("regressed project reviewed in place")
	}
	if blobs, err := s.GovernorStateBlobs(ctx); err != nil || blobs[GovernorProjectKeyPrefix+string(done.Project.ID)] == "" {
		t.Fatal("finished project is not saved", err)
	}
}
