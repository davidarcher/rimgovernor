package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// maxActiveProjects bounds unretired project rows, as maxActiveGoals does
// goals.
const maxActiveProjects = 512

// ProjectState is one Project row and the methods bound to it (#1926, epic
// #1911). Its methods live in methods with project_id set instead of
// standard_id, epoch "0", and go through the same admission. Revision is a local
// CAS token, as a goal's.
type ProjectState struct {
	Project  domain.Project
	Revision uint64
	// Methods lists the open plans; retired plans remain in History.
	Methods []ProjectMethod
	// Admitted counts every method ever committed, retired plans included.
	Admitted int
	// History lists every method, retired plans included.
	History []ProjectMethod
	Retired bool
}

type ProjectMethod struct {
	Method domain.MethodID
	Plan   domain.PlanID
}

func (p ProjectState) ownerKey() (string, string, string) {
	return "project_id", string(p.Project.ID), "0"
}
func (p ProjectState) ownerSnapshot() domain.GenerationSnapshot { return p.Project.Snapshot }
func (p ProjectState) ownerNeed(r Rounds) (domain.ConcernID, bool) {
	return r.projectNeed(p.Project.ID)
}
func (p ProjectState) ownerPriority() int { return p.Project.Priority }
func (p ProjectState) ownerPlans() []domain.PlanID {
	out := make([]domain.PlanID, len(p.Methods))
	for i, m := range p.Methods {
		out[i] = m.Plan
	}
	return out
}
func (p ProjectState) ownerLabel() string { return "project " + string(p.Project.ID) }

func (s *Store) CreateProject(ctx context.Context, p domain.Project) error {
	if err := p.Validate(); err != nil {
		return err
	}
	if p.Status != domain.ProjectOpen || p.Finding != domain.FindingUnclear {
		return errors.New("new project must start without completion evidence")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if err = createProject(ctx, tx, p); err != nil {
		return err
	}
	if err = tx.Commit(); err != nil {
		return err
	}
	s.notifyGoalsWritten()
	return nil
}

func createProject(ctx context.Context, tx *sql.Tx, p domain.Project) error {
	if err := p.Validate(); err != nil {
		return err
	}
	var count int
	if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM projects WHERE retired=0").Scan(&count); err != nil {
		return err
	}
	if count >= maxActiveProjects {
		return ErrCapacity
	}
	data, err := json.Marshal(p)
	if err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO projects(id,revision,payload) VALUES(?,?,?)", p.ID, "0", data); err != nil {
		return conflict(err)
	}
	return nil
}

func (s *Store) LoadProject(ctx context.Context, id domain.ProjectID) (ProjectState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ProjectState{}, err
	}
	defer tx.Rollback()
	return loadProject(ctx, tx, id)
}

func loadProject(ctx context.Context, tx *sql.Tx, id domain.ProjectID) (ProjectState, error) {
	var out ProjectState
	var data []byte
	var revision string
	if err := tx.QueryRowContext(ctx, "SELECT revision,payload,retired FROM projects WHERE id=?", id).Scan(&revision, &data, &out.Retired); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			err = ErrNotFound
		}
		return out, err
	}
	n, err := strconv.ParseUint(revision, 10, 64)
	if err != nil || strconv.FormatUint(n, 10) != revision {
		return out, errors.New("invalid project revision")
	}
	if len(data) > 8192 {
		return out, errors.New("project payload exceeds bound")
	}
	if err = json.Unmarshal(data, &out.Project); err != nil {
		return ProjectState{}, err
	}
	canonical, err := json.Marshal(out.Project)
	if err != nil || !bytes.Equal(data, canonical) {
		return ProjectState{}, errors.New("noncanonical project state")
	}
	if err = out.Project.Validate(); err != nil {
		return ProjectState{}, err
	}
	if out.Project.ID != id {
		return ProjectState{}, errors.New("project identity mismatch")
	}
	if out.Retired && out.Project.Status != domain.ProjectVoided {
		return ProjectState{}, errors.New("invalid retired project")
	}
	out.Revision = n
	rows, err := tx.QueryContext(ctx, "SELECT m.method_id,m.plan_id,p.retired FROM methods m JOIN plans p ON p.id=m.plan_id WHERE m.project_id=? ORDER BY m.method_id LIMIT 257", id)
	if err != nil {
		return ProjectState{}, err
	}
	defer rows.Close()
	for rows.Next() {
		var m ProjectMethod
		var retired bool
		if err = rows.Scan(&m.Method, &m.Plan, &retired); err != nil {
			return ProjectState{}, err
		}
		out.History = append(out.History, m)
		if !retired {
			out.Methods = append(out.Methods, m)
		}
		if len(out.History) > 256 {
			return ProjectState{}, ErrCapacity
		}
	}
	if err = rows.Err(); err != nil {
		return ProjectState{}, err
	}
	out.Admitted = len(out.History)
	return out, nil
}

func saveProject(ctx context.Context, tx *sql.Tx, previous ProjectState, p domain.Project) (ProjectState, error) {
	if previous.Retired {
		return ProjectState{}, errors.New("retired project is read-only")
	}
	if p == previous.Project {
		return previous, nil
	}
	if previous.Revision == ^uint64(0) {
		return ProjectState{}, ErrCapacity
	}
	if err := p.Validate(); err != nil {
		return ProjectState{}, err
	}
	data, err := json.Marshal(p)
	if err != nil {
		return ProjectState{}, err
	}
	next := previous.Revision + 1
	if _, err = tx.ExecContext(ctx, "UPDATE projects SET revision=?,payload=? WHERE id=?", strconv.FormatUint(next, 10), data, p.ID); err != nil {
		return ProjectState{}, err
	}
	previous.Project, previous.Revision = p, next
	return previous, nil
}

// ReviewProject reviews a project against the current world under its CAS
// revision. A regressed finished project is not reviewed here: the caller
// opens a new row (domain.ProjectRegressed).
func (s *Store) ReviewProject(ctx context.Context, id domain.ProjectID, revision uint64, current domain.GenerationSnapshot, tick domain.Tick, need domain.Finding) (ProjectState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ProjectState{}, err
	}
	defer tx.Rollback()
	state, err := loadProject(ctx, tx, id)
	if err != nil {
		return ProjectState{}, err
	}
	if state.Revision != revision {
		return ProjectState{}, ErrConflict
	}
	open, err := goalOpenWork(ctx, tx, state)
	if err != nil {
		return ProjectState{}, err
	}
	p, err := domain.ReviewProject(state.Project, current, tick, need, open)
	if err != nil {
		return ProjectState{}, err
	}
	if p.Status == domain.ProjectVoided {
		if err = cancelMethods(ctx, tx, state); err != nil {
			return ProjectState{}, err
		}
	}
	out, err := saveProject(ctx, tx, state, p)
	if err != nil {
		return ProjectState{}, err
	}
	return out, tx.Commit()
}

// CommitProjectMethod stores a method for an open project and its shared plan
// atomically, through the Safeguards and per-family admission a goal's method
// takes. Like CommitMethod it grants no authority to dispatch.
func (s *Store) CommitProjectMethod(ctx context.Context, id domain.ProjectID, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec) (ProjectState, error) {
	if err := plan.Validate(); err != nil {
		return ProjectState{}, err
	}
	if len(plan.Actions()) == 0 {
		return ProjectState{}, errors.New("empty project method")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ProjectState{}, err
	}
	defer tx.Rollback()
	state, err := commitProjectMethod(ctx, tx, id, revision, method, reason, plan)
	if err != nil {
		return ProjectState{}, err
	}
	return state, tx.Commit()
}

func commitProjectMethod(ctx context.Context, tx *sql.Tx, id domain.ProjectID, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec) (ProjectState, error) {
	state, err := loadProject(ctx, tx, id)
	if err != nil {
		return ProjectState{}, err
	}
	if err = admitOwnerCommit(ctx, tx, state, revision, method, reason, plan); err != nil {
		return ProjectState{}, err
	}
	return loadProject(ctx, tx, id)
}

// guardProjectWork is guardGoalWork for a project's plan: the project is open
// in the current world and no Safeguard vetoes it.
func guardProjectWork(ctx context.Context, tx *sql.Tx, id domain.ProjectID, current domain.GenerationSnapshot, tick domain.Tick) error {
	state, err := loadProject(ctx, tx, id)
	if err != nil {
		return err
	}
	p := state.Project
	if p.Status != domain.ProjectOpen || p.Finding == domain.FindingUnclear || !p.Snapshot.SameWorld(current) || tick < p.Tick {
		return errors.New("project does not admit current work")
	}
	return admitRoutineSafeguards(ctx, tx, state)
}

// retireProjects takes invalidated autopilot projects without open work out of
// capacity, as retireRoutineGoals does goals.
func retireProjects(ctx context.Context, tx *sql.Tx, retained map[domain.ProjectID]bool) error {
	rows, err := tx.QueryContext(ctx, "SELECT id FROM projects WHERE retired=0 ORDER BY id LIMIT 257")
	if err != nil {
		return err
	}
	var ids []domain.ProjectID
	for rows.Next() {
		var id domain.ProjectID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return err
		}
		ids = append(ids, id)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return err
	}
	if len(ids) > maxActiveProjects {
		return ErrCapacity
	}
	for _, id := range ids {
		if retained[id] {
			continue
		}
		p, err := loadProject(ctx, tx, id)
		if err != nil {
			return err
		}
		if p.Project.Status != domain.ProjectVoided {
			continue
		}
		open, err := goalOpenWork(ctx, tx, p)
		if err != nil {
			return err
		}
		if open {
			continue
		}
		if _, err = tx.ExecContext(ctx, "UPDATE projects SET retired=1 WHERE id=?", id); err != nil {
			return err
		}
	}
	return nil
}

// projectIDPrefix starts every Project id: a Rounds pass's
// "project-<hex16 world digest>-<kind>-<gen>" and a player's
// "project-player-<hex32>-<kind>".
const projectIDPrefix = "project-"

// isProjectID reports whether id names a Project row, not a goal row.
func isProjectID(id string) bool {
	return strings.HasPrefix(id, projectIDPrefix)
}
