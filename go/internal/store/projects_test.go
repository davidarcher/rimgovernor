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
	r := roundsRequest()
	reviewRounds(t, s, &r)
	p, err := domain.NewProject(testProjectID, policy.EnsureCooking, 2, scope(), 10)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreateProject(ctx, p); err != nil {
		t.Fatal(err)
	}
	state, err := s.ReviewProject(ctx, p.ID, 0, scope(), 10, domain.FindingUnmet)
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
	if m, ok, err := s.PlanMethod(ctx, id); err != nil || !ok || m.Project != state.Project.ID || m.Concern != policy.EnsureCooking || m.Reason != "no cooking bill" {
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
	g, err := domain.NewStandard("routine-0000000000000000-MaintainHousing-0", 2, scope(), 10)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.SeedStandard(ctx, g); err != nil {
		t.Fatal(err)
	}
	gs, err := s.ReviewStandard(ctx, g.ID, 0, scope(), 10, domain.FindingUnmet)
	if err != nil {
		t.Fatal(err)
	}
	gid := domain.MintPlanID()
	if _, err = s.CommitMethod(ctx, gs.Standard.ID, gs.Revision, "shell", plan(t, gid, domain.ActionID(gid+"-0"))); err != nil {
		t.Fatal(err)
	}
	if blobs, err = s.GovernorStateBlobs(ctx); err != nil {
		t.Fatal(err)
	}
	passes := 0
	var seen []domain.PlanID
	if err = s.RebuildStandards(ctx, blobs, func(_ context.Context, plans []PlanState) error {
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
	if err = s.db.QueryRowContext(ctx, "SELECT (SELECT COUNT(*) FROM plan_methods)+(SELECT COUNT(*) FROM plan_owner)").Scan(&rows); err != nil || rows != 0 {
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
	if err = s.RebuildStandards(ctx, blobs, nil); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LoadProject(ctx, state.Project.ID); !errors.Is(err, ErrNotFound) {
		t.Fatal("absent project kept", err)
	}
	stale, _ := json.Marshal(GovernorProjectBlob{SchemaVersion: GovernorStateSchemaVersion - 1, Project: state.Project, Revision: 1})
	if err = s.RebuildStandards(ctx, map[string]string{key: string(stale)}, nil); err == nil {
		t.Fatal("stale project blob accepted")
	}
}

func TestProjectFinishInvalidateRetireAndOwnerKey(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, state := projectFixture(t)
	p := plan(t, "project-plan", "project-action")
	state, err := s.CommitProjectMethod(ctx, state.Project.ID, state.Revision, "stove", "", p)
	if err != nil {
		t.Fatal(err)
	}
	// A plan has exactly one owner: a second owner table cannot claim it.
	if _, err = s.db.ExecContext(ctx, "INSERT INTO standard_methods(standard_id,episode,method_id,plan_id,priority) VALUES('a','0','m','project-plan',1)"); err == nil {
		t.Fatal("second owner kind accepted for a bound plan")
	}
	if _, err = s.db.ExecContext(ctx, "INSERT INTO plan_owner(plan_id,kind) VALUES('project-plan','standard')"); err == nil {
		t.Fatal("plan_owner accepted a second row for a plan")
	}
	// The plan-to-owner lookup names the project.
	var kind, owner string
	if err = s.db.QueryRowContext(ctx, "SELECT kind,owner_id FROM plan_methods WHERE plan_id='project-plan'").Scan(&kind, &owner); err != nil || kind != "project" || owner != string(state.Project.ID) {
		t.Fatal("project plan lookup", kind, owner, err)
	}
	// A world change invalidates the project, cancelling its work; the row
	// stays at the new revision.
	if _, err = s.ReviewProject(ctx, state.Project.ID, state.Revision-1, otherMap(), 11, domain.FindingUnmet); !errors.Is(err, ErrConflict) {
		t.Fatal("stale revision reviewed", err)
	}
	cancelled, err := s.ReviewProject(ctx, state.Project.ID, state.Revision, otherMap(), 11, domain.FindingUnmet)
	if err != nil || cancelled.Project.Status != domain.ProjectVoided {
		t.Fatal(cancelled.Project, err)
	}
	loaded, err := s.LoadPlan(ctx, p.ID())
	if err != nil || domain.GoalWorkOpen(loaded.Progress) {
		t.Fatal("invalidation left the method open", err)
	}
	if _, err = s.CommitProjectMethod(ctx, state.Project.ID, cancelled.Revision, "late", "", plan(t, "late", "late-0")); err == nil {
		t.Fatal("invalidated project admitted a method")
	}
	// Retirement takes an invalidated project without open work.
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback()
	if err = retireProjects(ctx, tx, nil); err != nil {
		t.Fatal(err)
	}
	if kept, err := loadProject(ctx, tx, state.Project.ID); err != nil || !kept.Retired {
		t.Fatal("invalidated project not retired", err)
	}
}

func TestProjectInvalidatedByWorldChangeRetires(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, state := projectFixture(t)
	other := scope()
	other.Colony = "another-colony"
	invalid, err := s.ReviewProject(ctx, state.Project.ID, state.Revision, other, 20, domain.FindingUnmet)
	if err != nil || invalid.Project.Status != domain.ProjectVoided {
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
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM projects WHERE retired=0 AND id=?", invalid.Project.ID).Scan(&active); err != nil || active != 0 {
		t.Fatal(active, err)
	}
}

func TestProjectFinishesOnceAndBlobSkipsRetired(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, state := projectFixture(t)
	done, err := s.ReviewProject(ctx, state.Project.ID, state.Revision, scope(), 20, domain.FindingMet)
	if err != nil || done.Project.Status != domain.ProjectCompleted {
		t.Fatal(done.Project, err)
	}
	if !domain.ProjectRegressed(done.Project, domain.FindingUnmet, false) {
		t.Fatal("regress rule moved")
	}
	if _, err = s.ReviewProject(ctx, done.Project.ID, done.Revision, scope(), 30, domain.FindingUnmet); err == nil {
		t.Fatal("regressed project reviewed in place")
	}
	if blobs, err := s.GovernorStateBlobs(ctx); err != nil || blobs[GovernorProjectKeyPrefix+string(done.Project.ID)] == "" {
		t.Fatal("finished project is not saved", err)
	}
}
