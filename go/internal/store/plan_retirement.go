package store

import (
	"context"
	"database/sql"
	"errors"
	"strconv"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// settledRetirementOutcome is dispatched evidence that settles an action for
// retirement: a completed effect, a
// building that ended unsuccessful, or a building intent native refused
// (#856: a refusal is terminal, Unsuccessful with an absent effect).
func settledRetirementOutcome(progress domain.Progress) bool {
	w := progress.View()
	effect, known := w.Effect.Value()
	if !known {
		return false
	}
	ended := w.Stage == domain.Unsuccessful || w.Stage == domain.Cancelled
	switch {
	case effect == domain.EffectCompleted:
		return w.Stage == domain.Completed || w.Stage == domain.Cancelled
	case progress.Action().Kind() != domain.BuildingAction:
		return false
	case effect == domain.EffectUnsuccessful:
		return ended
	case effect == domain.EffectAbsent:
		return ended && w.Attempt > 0
	}
	return false
}

// Only settled autopilot methods retire. The current root plan, unresolved effects,
// cleanup and unsuccessful work keep their complete catalog entries.
func retireRoutinePlans(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick, census domain.Fact[policy.CurrentConstruction], floors map[retirementWorld]domain.Tick) error {
	rows, err := tx.QueryContext(ctx, "SELECT p.id,m.goal_id,m.incident_id,m.project_id FROM plans p INDEXED BY active_plans CROSS JOIN goal_methods m ON m.plan_id=p.id WHERE p.retired=0 ORDER BY p.id LIMIT 257")
	if err != nil {
		return err
	}
	type link struct {
		plan     domain.PlanID
		goal     sql.NullString
		incident sql.NullString
		project  sql.NullString
	}
	var links []link
	for rows.Next() {
		var v link
		if err = rows.Scan(&v.plan, &v.goal, &v.incident, &v.project); err != nil {
			rows.Close()
			return err
		}
		links = append(links, v)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return err
	}
	if len(links) > 256 {
		return ErrCapacity
	}
	for _, v := range links {
		if v.plan == current.Plan {
			continue
		}
		// Incidents are autopilot-owned (#1020); a goal or project is only
		// when the autopilot sourced it.
		var g GoalState
		var owned ProjectState
		switch {
		case v.project.Valid:
			if owned, err = loadProject(ctx, tx, domain.ProjectID(v.project.String)); err != nil {
				return err
			}
			if owned.Project.Source != domain.AutopilotGoal {
				continue
			}
		case !v.incident.Valid:
			if g, err = loadGoal(ctx, tx, domain.GoalID(v.goal.String)); err != nil {
				return err
			}
			if g.Goal.Source != domain.AutopilotGoal {
				continue
			}
		}
		p, err := load(ctx, tx, v.plan)
		if err != nil {
			return err
		}
		if PlanWorkOpen(p, census) {
			continue
		}
		// A fight (#852) owns its empty plan while open or rostering a
		// drafted defender (#939): retiring it would hide the fight from its
		// goal, and the clock scheduler would stop admitting ticks
		// mid-fight (#869).
		held, err := combatFightHolds(ctx, tx, v.plan)
		if err != nil {
			return err
		}
		if held {
			continue
		}
		settled := true
		for _, progress := range p.Progress {
			w := progress.View()
			effect, known := w.Effect.Value()
			absent := w.Stage == domain.Cancelled && (w.Attempt == 0 || known && effect == domain.EffectAbsent)
			settledOutcome := settledRetirementOutcome(progress)
			if !absent && !settledOutcome {
				settled = false
				break
			}
			if settledOutcome && w.Snapshot.Colony == current.Colony && w.Snapshot.Load == current.Load && w.Snapshot.Map == current.Map && tick < w.Tick {
				settled = false
				break
			}
		}
		if !settled {
			continue
		}
		if g.Revision == ^uint64(0) || owned.Revision == ^uint64(0) {
			return ErrCapacity
		}
		for _, progress := range p.Progress {
			if w := progress.View(); settledRetirementOutcome(progress) {
				k := retirementWorld{w.Snapshot.Colony, w.Snapshot.Map}
				floors[k] = max(floors[k], w.Tick)
			}
		}
		if _, err = tx.ExecContext(ctx, "UPDATE plans SET retired=1 WHERE id=?", v.plan); err != nil {
			return err
		}
		if v.incident.Valid {
			continue
		}
		if v.project.Valid {
			if _, err = tx.ExecContext(ctx, "UPDATE projects SET revision=? WHERE id=?", strconv.FormatUint(owned.Revision+1, 10), v.project.String); err != nil {
				return err
			}
			continue
		}
		if _, err = tx.ExecContext(ctx, "UPDATE goals SET revision=? WHERE id=?", strconv.FormatUint(g.Revision+1, 10), v.goal.String); err != nil {
			return err
		}
	}
	return nil
}

// LoadGoalMethod reads an exact historical binding, including retired plans.
// History is never fed back into active capacity or dependency accounting.
func (s *Store) LoadGoalMethod(ctx context.Context, goal domain.GoalID, epoch uint64, method domain.MethodID) (domain.GoalMethod, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.GoalMethod{}, err
	}
	defer tx.Rollback()
	g, err := loadGoal(ctx, tx, goal)
	if err != nil {
		return domain.GoalMethod{}, err
	}
	m := domain.GoalMethod{Goal: goal, Epoch: epoch, Method: method}
	column, key := "goal_id", strconv.FormatUint(epoch, 10)
	if err = tx.QueryRowContext(ctx, "SELECT plan_id FROM goal_methods WHERE "+column+"=? AND epoch=? AND method_id=?", goal, key, method).Scan(&m.Plan); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			err = ErrNotFound
		}
		return domain.GoalMethod{}, err
	}
	if err = m.Validate(); err != nil {
		return domain.GoalMethod{}, err
	}
	if m.Epoch > g.Goal.Epoch {
		return domain.GoalMethod{}, errors.New("invalid historical method epoch")
	}
	if err = tx.Commit(); err != nil {
		return domain.GoalMethod{}, err
	}
	return m, nil
}

// LatestMethodPlan finds the plan a goal last bound to method in any
// epoch (#985): plan ids are minted, so planners whose work outlives an
// epoch turnover (tidy re-sites, zone shelves) load it by this stored key.
func (s *Store) LatestMethodPlan(ctx context.Context, goal domain.GoalID, method domain.MethodID) (domain.PlanID, error) {
	var id domain.PlanID
	column := "goal_id"
	err := s.db.QueryRowContext(ctx, "SELECT plan_id FROM goal_methods WHERE "+column+"=? AND method_id=? ORDER BY CAST(epoch AS INTEGER) DESC LIMIT 1", goal, method).Scan(&id)
	if errors.Is(err, sql.ErrNoRows) {
		return "", ErrNotFound
	}
	return id, err
}

// LoadGoalMethods includes retired bindings for one epoch. The bounded history
// is evidence only; it cannot restore retired work to execution or accounting.
func (s *Store) LoadGoalMethods(ctx context.Context, goal domain.GoalID, epoch uint64) ([]domain.GoalMethod, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	g, err := loadGoal(ctx, tx, goal)
	if err != nil {
		return nil, err
	}
	if epoch > g.Goal.Epoch {
		return nil, errors.New("invalid historical method epoch")
	}
	column, key := "goal_id", strconv.FormatUint(epoch, 10)
	rows, err := tx.QueryContext(ctx, "SELECT method_id,plan_id FROM goal_methods WHERE "+column+"=? AND epoch=? ORDER BY method_id LIMIT 257", goal, key)
	if err != nil {
		return nil, err
	}
	var result []domain.GoalMethod
	for rows.Next() {
		m := domain.GoalMethod{Goal: goal, Epoch: epoch}
		if err = rows.Scan(&m.Method, &m.Plan); err != nil {
			rows.Close()
			return nil, err
		}
		if err = m.Validate(); err != nil {
			rows.Close()
			return nil, err
		}
		result = append(result, m)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return nil, err
	}
	if len(result) > 256 {
		return nil, errors.New("goal method history exceeds bound")
	}
	if err = tx.Commit(); err != nil {
		return nil, err
	}
	return result, nil
}

// retirementWorld keys a retirement floor: the load is not part of it (U4b).
type retirementWorld struct {
	colony domain.ColonyID
	mapID  domain.MapID
}

// retirementFloors holds, per world, the newest tick of settled evidence a
// retired plan carried (#1008 follow-up): stock observed before it was
// already spent by that plan, so it does not buy a second method. It is
// session-only, shared by every handle on one database path, and resets on a
// goal rebuild (#998) and a process restart.
type retirementFloors struct {
	mu    sync.Mutex
	ticks map[retirementWorld]domain.Tick
}

var retirementFloorRegistry sync.Map // database path -> *retirementFloors

func floorsFor(path string) *retirementFloors {
	f, _ := retirementFloorRegistry.LoadOrStore(path, &retirementFloors{})
	return f.(*retirementFloors)
}

func (f *retirementFloors) raise(settled map[retirementWorld]domain.Tick) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for w, t := range settled {
		if f.ticks == nil {
			f.ticks = map[retirementWorld]domain.Tick{}
		}
		if t > f.ticks[w] {
			f.ticks[w] = t
		}
	}
}

func (f *retirementFloors) reset() {
	f.mu.Lock()
	f.ticks = nil
	f.mu.Unlock()
}

// guard refuses an observation older than its world's floor.
func (f *retirementFloors) guard(current domain.GenerationSnapshot, tick domain.Tick) error {
	if f == nil {
		return nil
	}
	f.mu.Lock()
	floor, ok := f.ticks[retirementWorld{current.Colony, current.Map}]
	f.mu.Unlock()
	if ok && tick < floor {
		return errors.New("observation predates retired accounting evidence")
	}
	return nil
}
