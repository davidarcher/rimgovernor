package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// GoalState links methods to the existing plan catalog. Revision is a local CAS
// token, not a native generation or permission to run a method.
type GoalState struct {
	Goal     domain.Goal
	Revision uint64
	Methods  []domain.GoalMethod // Active plans; retired methods remain in LoadGoalMethod.
	Retired  bool
}

const maxActiveGoals = 512

// initializeGoals creates the goal lifecycle tables.
//
// player_goals is the one per-world, per-kind binding for goals the player
// commands directly, shared by every such command rather than owned by one:
// CreateGoal binds a kind it force-activates, AdoptRoom binds
// EnsureInitialShelter when it completes it. Because request_id may name a row
// in either command's own request table, it carries no foreign key and the
// command column says which table to read it from. Sharing the binding is what
// makes the two commands agree: adopting a room completes the same shelter goal
// CreateGoal would have activated, instead of leaving a second, contradictory
// player goal for the same kind.
func initializeGoals(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE goals(id TEXT PRIMARY KEY, revision TEXT NOT NULL, payload BLOB NOT NULL, retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN (0,1))) STRICT;
CREATE INDEX active_goals ON goals(id) WHERE retired=0;
CREATE TABLE goal_methods(goal_id TEXT NOT NULL REFERENCES goals(id), epoch TEXT NOT NULL, method_id TEXT NOT NULL, plan_id TEXT NOT NULL UNIQUE REFERENCES plans(id), PRIMARY KEY(goal_id,epoch,method_id)) STRICT;
CREATE TABLE routine_review(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;
CREATE TABLE goal_create_submissions(request_id TEXT PRIMARY KEY, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, kind TEXT NOT NULL, goal_id TEXT NOT NULL REFERENCES goals(id), payload BLOB NOT NULL) STRICT;
CREATE TABLE player_goals(colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, kind TEXT NOT NULL, command TEXT NOT NULL CHECK(command IN ('create_goal','adopt_room')), request_id TEXT NOT NULL, goal_id TEXT NOT NULL REFERENCES goals(id), PRIMARY KEY(colony,load_token,map_id,kind)) STRICT;`)
	return err
}

func (s *Store) CreateGoal(ctx context.Context, g domain.Goal) error {
	if err := g.Validate(); err != nil {
		return err
	}
	if g.Status != domain.GoalActive || g.Epoch != 0 || g.Need != domain.NeedUnknown || g.RecoveryObserved {
		return errors.New("new goal must start without completion evidence")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if err = createGoal(ctx, tx, g); err != nil {
		return err
	}
	return tx.Commit()
}

func createGoal(ctx context.Context, tx *sql.Tx, g domain.Goal) error {
	if err := g.Validate(); err != nil {
		return err
	}
	var count int
	if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM goals WHERE retired=0").Scan(&count); err != nil {
		return err
	}
	if count >= maxActiveGoals {
		return ErrCapacity
	}
	data, err := json.Marshal(g)
	if err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO goals(id,revision,payload) VALUES(?,?,?)", g.ID, "0", data); err != nil {
		return conflict(err)
	}
	return nil
}

func loadGoal(ctx context.Context, tx *sql.Tx, id domain.GoalID) (GoalState, error) {
	var out GoalState
	var data []byte
	var revision string
	if err := tx.QueryRowContext(ctx, "SELECT revision,payload,retired FROM goals WHERE id=?", id).Scan(&revision, &data, &out.Retired); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			err = ErrNotFound
		}
		return out, err
	}
	n, err := strconv.ParseUint(revision, 10, 64)
	if err != nil || strconv.FormatUint(n, 10) != revision {
		return out, errors.New("invalid goal revision")
	}
	if len(data) > 8192 {
		return out, errors.New("goal payload exceeds bound")
	}
	if err = json.Unmarshal(data, &out.Goal); err != nil {
		return GoalState{}, err
	}
	canonical, err := json.Marshal(out.Goal)
	if err != nil || !bytes.Equal(data, canonical) {
		return GoalState{}, errors.New("noncanonical goal state")
	}
	if err = out.Goal.Validate(); err != nil {
		return GoalState{}, err
	}
	if out.Goal.ID != id {
		return GoalState{}, errors.New("goal identity mismatch")
	}
	if out.Retired && (out.Goal.Source != domain.AutopilotGoal || out.Goal.Status != domain.GoalInvalidated) {
		return GoalState{}, errors.New("invalid retired goal")
	}
	out.Revision = n
	rows, err := tx.QueryContext(ctx, "SELECT m.epoch,m.method_id,m.plan_id FROM plans p INDEXED BY active_plans CROSS JOIN goal_methods m ON m.plan_id=p.id WHERE p.retired=0 AND m.goal_id=? ORDER BY length(m.epoch),m.epoch,m.method_id", id)
	if err != nil {
		return GoalState{}, err
	}
	defer rows.Close()
	for rows.Next() {
		var m domain.GoalMethod
		var epoch string
		m.Goal = id
		if err = rows.Scan(&epoch, &m.Method, &m.Plan); err != nil {
			return GoalState{}, err
		}
		m.Epoch, err = strconv.ParseUint(epoch, 10, 64)
		if err != nil || strconv.FormatUint(m.Epoch, 10) != epoch || m.Epoch > out.Goal.Epoch || m.Validate() != nil {
			return GoalState{}, errors.New("invalid goal method record")
		}
		out.Methods = append(out.Methods, m)
		if len(out.Methods) > 256 {
			return GoalState{}, ErrCapacity
		}
	}
	return out, rows.Err()
}

func (s *Store) LoadGoal(ctx context.Context, id domain.GoalID) (GoalState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	out, err := loadGoal(ctx, tx, id)
	if err != nil {
		return GoalState{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return out, nil
}

func saveGoal(ctx context.Context, tx *sql.Tx, previous GoalState, g domain.Goal) (GoalState, error) {
	if previous.Retired {
		return GoalState{}, errors.New("retired goal is read-only")
	}
	if g == previous.Goal {
		return previous, nil
	}
	if previous.Revision == ^uint64(0) {
		return GoalState{}, ErrCapacity
	}
	if err := g.Validate(); err != nil {
		return GoalState{}, err
	}
	data, err := json.Marshal(g)
	if err != nil {
		return GoalState{}, err
	}
	next := previous.Revision + 1
	if _, err = tx.ExecContext(ctx, "UPDATE goals SET revision=?,payload=? WHERE id=?", strconv.FormatUint(next, 10), data, g.ID); err != nil {
		return GoalState{}, err
	}
	previous.Goal = g
	previous.Revision = next
	return previous, nil
}

func goalOpenWork(ctx context.Context, tx *sql.Tx, state GoalState) (bool, error) {
	open := false
	for _, m := range state.Methods {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		open = open || domain.GoalWorkOpen(p.Progress)
	}
	return open, nil
}

func (s *Store) ReviewGoal(ctx context.Context, id domain.GoalID, revision uint64, current domain.GenerationSnapshot, tick domain.Tick, need domain.NeedState, emergency bool) (GoalState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	state, err := loadGoal(ctx, tx, id)
	if err != nil {
		return GoalState{}, err
	}
	if state.Revision != revision {
		return GoalState{}, ErrConflict
	}
	open, err := goalOpenWork(ctx, tx, state)
	if err != nil {
		return GoalState{}, err
	}
	g, err := domain.ReviewGoal(state.Goal, current, tick, need, emergency, open)
	if err != nil {
		return GoalState{}, err
	}
	if g.Status == domain.GoalInvalidated {
		if err = cancelGoalMethods(ctx, tx, state); err != nil {
			return GoalState{}, err
		}
	}
	out, err := saveGoal(ctx, tx, state, g)
	if err != nil {
		return GoalState{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return out, nil
}

// CommitGoalMethod stores the method and its shared plan atomically. Admission
// and dispatch still belong to existing policy/Hands; this grants no authority.
func (s *Store) CommitGoalMethod(ctx context.Context, id domain.GoalID, revision uint64, method domain.MethodID, plan domain.PlanSpec) (GoalState, error) {
	if err := plan.Validate(); err != nil {
		return GoalState{}, err
	}
	if len(plan.Actions()) == 0 {
		return GoalState{}, errors.New("empty goal method")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	state, err := commitGoalMethod(ctx, tx, id, revision, method, plan)
	if err != nil {
		return GoalState{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return state, nil
}

func commitGoalMethod(ctx context.Context, tx *sql.Tx, id domain.GoalID, revision uint64, method domain.MethodID, plan domain.PlanSpec) (GoalState, error) {
	state, err := loadGoal(ctx, tx, id)
	if err != nil {
		return GoalState{}, err
	}
	if state.Revision != revision {
		return GoalState{}, ErrConflict
	}
	g := state.Goal
	if g.Status != domain.GoalActive || g.Need != domain.NeedDeficit || g.Source == domain.AdviserGoal {
		return GoalState{}, errors.New("goal does not admit a method")
	}
	if err = admitRoutineDevelopment(ctx, tx, g); err != nil {
		return GoalState{}, err
	}
	open, err := goalOpenWork(ctx, tx, state)
	if err != nil {
		return GoalState{}, err
	}
	if open {
		exempt, err := acquisitionOpenWorkExempt(ctx, tx, state, plan)
		if err != nil {
			return GoalState{}, err
		}
		if !exempt {
			exempt, err = fieldOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return GoalState{}, err
			}
		}
		if !exempt {
			return GoalState{}, errors.New("existing method requires observation")
		}
	}
	m := domain.GoalMethod{Goal: id, Epoch: g.Epoch, Method: method, Plan: plan.ID()}
	if err = m.Validate(); err != nil {
		return GoalState{}, err
	}
	if len(state.Methods) >= 256 || state.Revision == ^uint64(0) {
		return GoalState{}, ErrCapacity
	}
	if err = admitBillMethod(ctx, tx, state, plan); err != nil {
		return GoalState{}, err
	}
	if err = admitZoneMethod(ctx, tx, state, plan); err != nil {
		return GoalState{}, err
	}
	if err = admitAcquisitionMethod(ctx, tx, state, plan); err != nil {
		return GoalState{}, err
	}
	if err = admitWorkMethod(ctx, tx, state, plan); err != nil {
		return GoalState{}, err
	}
	if err = admitSupplyMethod(ctx, tx, state, plan); err != nil {
		return GoalState{}, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return GoalState{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO goal_methods(goal_id,epoch,method_id,plan_id) VALUES(?,?,?,?)", id, strconv.FormatUint(m.Epoch, 10), method, plan.ID()); err != nil {
		return GoalState{}, conflict(err)
	}
	state.Revision++
	if _, err = tx.ExecContext(ctx, "UPDATE goals SET revision=? WHERE id=?", strconv.FormatUint(state.Revision, 10), id); err != nil {
		return GoalState{}, err
	}
	state, err = loadGoal(ctx, tx, id)
	if err != nil {
		return GoalState{}, err
	}
	return state, nil
}

// CancelGoal invalidates unissued work and marks issued work cancelled through
// the normal progress journal, atomically with the goal. Effects still reconcile.
func (s *Store) CancelGoal(ctx context.Context, id domain.GoalID, revision uint64) (GoalState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return GoalState{}, err
	}
	defer tx.Rollback()
	state, err := loadGoal(ctx, tx, id)
	if err != nil {
		return GoalState{}, err
	}
	out, err := cancelGoalState(ctx, tx, state, revision)
	if err != nil {
		return GoalState{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return out, nil
}

// cancelGoalState is the shared cancellation body: CAS on the local revision,
// cancel the goal's captured work through the ordinary progress journal, and
// record the cancellation. CancelGoal and the player-facing CancelPlayerGoal
// both go through exactly this, so the player path adds a world bound and a
// reachable entry point, never different cancellation semantics.
func cancelGoalState(ctx context.Context, tx *sql.Tx, state GoalState, revision uint64) (GoalState, error) {
	if state.Revision != revision {
		return GoalState{}, ErrConflict
	}
	g, err := domain.CancelGoal(state.Goal)
	if err != nil {
		return GoalState{}, err
	}
	if err = cancelGoalMethods(ctx, tx, state); err != nil {
		return GoalState{}, err
	}
	return saveGoal(ctx, tx, state, g)
}

func cancelGoalMethods(ctx context.Context, tx *sql.Tx, state GoalState) error {
	for _, m := range state.Methods {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return err
		}
		for _, progress := range p.Progress {
			v := progress.View()
			if v.Stage == domain.Completed || v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled {
				continue
			}
			if _, err = advanceInTransaction(ctx, tx, m.Plan, v.Action, transition{Kind: "cancel"}); err != nil {
				return err
			}
		}
	}
	return nil
}

func guardGoalWork(ctx context.Context, tx *sql.Tx, plan domain.PlanID, current domain.GenerationSnapshot, tick domain.Tick) error {
	var retired bool
	if err := tx.QueryRowContext(ctx, "SELECT retired FROM plans WHERE id=?", plan).Scan(&retired); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return ErrNotFound
		}
		return err
	}
	if retired {
		return errors.New("retired plan does not admit work")
	}
	if err := guardRetirementFloor(ctx, tx, current, tick); err != nil {
		return err
	}
	var id domain.GoalID
	var epoch string
	err := tx.QueryRowContext(ctx, "SELECT goal_id,epoch FROM goal_methods WHERE plan_id=?", plan).Scan(&id, &epoch)
	if errors.Is(err, sql.ErrNoRows) {
		return nil
	}
	if err != nil {
		return err
	}
	state, err := loadGoal(ctx, tx, id)
	if err != nil {
		return err
	}
	g := state.Goal
	s := g.Snapshot
	if g.Status != domain.GoalActive || g.Need == domain.NeedUnknown || g.Source == domain.AdviserGoal || epoch != strconv.FormatUint(g.Epoch, 10) ||
		s.Colony != current.Colony || s.Map != current.Map || s.Load != current.Load || tick < g.Tick {
		return errors.New("maintained goal does not admit current work")
	}
	return nil
}
