package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// GoalState links methods to the existing plan catalog. Revision is a local CAS
// token, not a native generation or permission to run a method.
type GoalState struct {
	Goal     domain.Goal
	Revision uint64
	Methods  []domain.GoalMethod // Active plans; retired methods remain in LoadGoalMethod.
	// Admitted counts every method ever committed for the goal, retired
	// plans included: a monotonic salt for method identities that must not
	// collide with a retired plan's row (#214).
	Admitted int
	// History lists the current epoch's methods, retired plans included.
	// Planners that number attempts count it, not Methods: a retired plan
	// drops out of Methods, and re-deriving its ID fails plans.id's unique
	// constraint.
	History []domain.GoalMethod
	Retired bool
}

const maxActiveGoals = 512

// initializeGoals creates the goal lifecycle tables. goals and routine_review
// are session caches (#1011): RebuildGoals refills goals from the save and
// ResetRoutineReview empties routine_review on every world change, and the
// next review recomputes it. Goal-create request replay is in memory only.
func initializeGoals(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE goals(id TEXT PRIMARY KEY, revision TEXT NOT NULL, payload BLOB NOT NULL, retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN (0,1))) STRICT;
CREATE INDEX active_goals ON goals(id) WHERE retired=0;
CREATE TABLE incidents(id TEXT PRIMARY KEY, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, kind TEXT NOT NULL, subject TEXT NOT NULL, started_tick INTEGER NOT NULL, ended_tick INTEGER, payload BLOB NOT NULL) STRICT;
CREATE UNIQUE INDEX open_incidents ON incidents(colony,load_token,map_id,kind,subject) WHERE ended_tick IS NULL;
CREATE TABLE goal_methods(goal_id TEXT REFERENCES goals(id), incident_id TEXT REFERENCES incidents(id), epoch TEXT NOT NULL, method_id TEXT NOT NULL, plan_id TEXT NOT NULL UNIQUE REFERENCES plans(id), priority INTEGER NOT NULL, reason TEXT, CHECK((goal_id IS NULL) <> (incident_id IS NULL))) STRICT;
CREATE UNIQUE INDEX goal_method_keys ON goal_methods(goal_id,epoch,method_id) WHERE goal_id IS NOT NULL;
CREATE UNIQUE INDEX incident_method_keys ON goal_methods(incident_id,method_id) WHERE incident_id IS NOT NULL;
CREATE TABLE routine_review(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;
CREATE TABLE defense_layout(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;
CREATE TABLE production_ladder(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;
CREATE TABLE soldier_squad(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload BLOB NOT NULL) STRICT;`)
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
	if err = tx.Commit(); err != nil {
		return err
	}
	s.notifyGoalsWritten()
	return nil
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
	if err := tx.QueryRowContext(ctx, "SELECT COUNT(*) FROM goal_methods WHERE goal_id=?", id).Scan(&out.Admitted); err != nil {
		return GoalState{}, err
	}
	history, err := tx.QueryContext(ctx, "SELECT method_id,plan_id FROM goal_methods WHERE goal_id=? AND epoch=? ORDER BY method_id", id, strconv.FormatUint(out.Goal.Epoch, 10))
	if err != nil {
		return GoalState{}, err
	}
	for history.Next() {
		m := domain.GoalMethod{Goal: id, Epoch: out.Goal.Epoch}
		if err = history.Scan(&m.Method, &m.Plan); err != nil {
			history.Close()
			return GoalState{}, err
		}
		out.History = append(out.History, m)
	}
	err = history.Err()
	history.Close()
	if err != nil {
		return GoalState{}, err
	}
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

func goalOpenWork(ctx context.Context, tx *sql.Tx, owner methodOwner) (bool, error) {
	open := false
	for _, plan := range owner.ownerPlans() {
		p, err := load(ctx, tx, plan)
		if err != nil {
			return false, err
		}
		open = open || PlanOpen(p)
		if !open {
			// A fight's drafts are its open work (#910).
			if open, err = combatFightHolds(ctx, tx, plan); err != nil {
				return false, err
			}
		}
	}
	return open, nil
}

// planOpenWork is goalOpenWork without the fights: whether any of the
// owner's plans is still open.
func planOpenWork(ctx context.Context, tx *sql.Tx, owner methodOwner) (bool, error) {
	for _, plan := range owner.ownerPlans() {
		p, err := load(ctx, tx, plan)
		if err != nil || PlanOpen(p) {
			return err == nil, err
		}
	}
	return false, nil
}

func (s *Store) ReviewGoal(ctx context.Context, id domain.GoalID, revision uint64, current domain.GenerationSnapshot, tick domain.Tick, need domain.NeedState) (GoalState, error) {
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
	g, err := domain.ReviewGoal(state.Goal, current, tick, need, open, goalIsStandard(state.Goal.ID))
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
	return s.CommitGoalMethodReason(ctx, id, revision, method, "", plan)
}

// CommitGoalMethodReason is CommitGoalMethod with the planner's short reason
// for admitting it (runway, deficit, target); the dispatcher appends it to
// Operation.intent (#846). Empty stores none.
func (s *Store) CommitGoalMethodReason(ctx context.Context, id domain.GoalID, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec) (GoalState, error) {
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
	state, err := commitGoalMethod(ctx, tx, id, revision, method, reason, plan)
	if err != nil {
		return GoalState{}, err
	}
	if err = tx.Commit(); err != nil {
		return GoalState{}, err
	}
	return state, nil
}

func commitGoalMethod(ctx context.Context, tx *sql.Tx, id domain.GoalID, revision uint64, method domain.MethodID, reason string, plan domain.PlanSpec) (GoalState, error) {
	state, err := loadGoal(ctx, tx, id)
	if err != nil {
		return GoalState{}, err
	}
	if state.Revision != revision {
		return GoalState{}, fmt.Errorf("%w: goal %s is at revision %d, not %d", ErrConflict, id, state.Revision, revision)
	}
	g := state.Goal
	if g.Status != domain.GoalActive || g.Need != domain.NeedDeficit {
		return GoalState{}, errors.New("goal does not admit a method")
	}
	if err = admitRoutineRules(ctx, tx, state); err != nil {
		return GoalState{}, err
	}
	if err = admitRoutineDevelopment(ctx, tx, g, plan); err != nil {
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
			exempt = growerCropOpenWorkExempt(plan) || mealReplacementOpenWorkExempt(state, plan)
		}
		if !exempt {
			exempt, err = foodFacilityOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return GoalState{}, err
			}
		}
		if !exempt {
			exempt, err = reserveAccessOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return GoalState{}, err
			}
		}
		if !exempt {
			exempt, err = gearOpenWorkExempt(ctx, tx, state, plan)
			if err != nil {
				return GoalState{}, err
			}
		}
		if !exempt {
			exempt, err = shelterOpenWorkExempt(ctx, tx, state, plan)
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
	if state.Revision == ^uint64(0) {
		return GoalState{}, ErrCapacity
	}
	if err = bindOwnerMethod(ctx, tx, state, method, reason, plan); err != nil {
		return GoalState{}, err
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

// cancelUndispatchedGoalMethods cancels every open method of the goal that
// no step ever dispatched: each action is still Pending or Prepared on its
// first attempt. A review that observes the goal recovered runs it before
// counting open work, so a plan admitted for a deficit the colonists (or the
// player) cleared on their own settles here instead of holding the goal
// Active and never authorized: the worker refuses a recovered goal's fresh
// write on every step, and nothing else ever retires the plan (#290). Work
// already dispatched keeps its own settlement path.
func cancelUndispatchedGoalMethods(ctx context.Context, tx *sql.Tx, owner methodOwner) error {
	for _, plan := range owner.ownerPlans() {
		p, err := load(ctx, tx, plan)
		if err != nil {
			return err
		}
		if p.Retired || !domain.GoalWorkOpen(p.Progress) {
			continue
		}
		undispatched := true
		for _, progress := range p.Progress {
			v := progress.View()
			if v.Attempt != 0 || v.Stage != domain.Pending && v.Stage != domain.Prepared && v.Stage != domain.Cancelled {
				undispatched = false
			}
		}
		if !undispatched {
			continue
		}
		for _, progress := range p.Progress {
			v := progress.View()
			if v.Stage == domain.Cancelled {
				continue
			}
			if _, err = advanceInTransaction(ctx, tx, plan, v.Action, transition{Kind: "cancel"}); err != nil {
				return err
			}
		}
	}
	return nil
}

func cancelGoalMethods(ctx context.Context, tx *sql.Tx, owner methodOwner) error {
	for _, plan := range owner.ownerPlans() {
		p, err := load(ctx, tx, plan)
		if err != nil {
			return err
		}
		for _, progress := range p.Progress {
			v := progress.View()
			// A completed intent-mode order stands until cancelled; cancelling
			// it releases the draft it holds (workerPlanHoldsDraft).
			if v.Stage == domain.Completed && !progress.Action().Kind().IntentMode() || v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled {
				continue
			}
			if _, err = advanceInTransaction(ctx, tx, plan, v.Action, transition{Kind: "cancel"}); err != nil {
				return err
			}
		}
	}
	return nil
}

func guardGoalWork(ctx context.Context, tx *sql.Tx, floors *retirementFloors, plan domain.PlanID, current domain.GenerationSnapshot, tick domain.Tick) error {
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
	if err := floors.guard(current, tick); err != nil {
		return err
	}
	var id sql.NullString
	var incident sql.NullString
	var epoch string
	err := tx.QueryRowContext(ctx, "SELECT goal_id,incident_id,epoch FROM goal_methods WHERE plan_id=?", plan).Scan(&id, &incident, &epoch)
	if errors.Is(err, sql.ErrNoRows) {
		return nil
	}
	if err != nil {
		return err
	}
	if incident.Valid {
		return guardIncidentWork(ctx, tx, domain.IncidentID(incident.String), current, tick)
	}
	state, err := loadGoal(ctx, tx, domain.GoalID(id.String))
	if err != nil {
		return err
	}
	g := state.Goal
	s := g.Snapshot
	if g.Status != domain.GoalActive || g.Need == domain.NeedUnknown || epoch != strconv.FormatUint(g.Epoch, 10) ||
		s.Colony != current.Colony || s.Map != current.Map || tick < g.Tick {
		return errors.New("maintained goal does not admit current work")
	}
	// A prepared plan does not prepare or dispatch while a Rule vetoes its
	// goal (#1017).
	return admitRoutineRules(ctx, tx, state)
}
