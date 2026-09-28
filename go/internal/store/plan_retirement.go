package store

import (
	"context"
	"database/sql"
	"errors"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Retained floors prevent old resource observations from becoming spendable
// when their completed reservations leave the active catalog.
func guardRetirementFloor(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) error {
	var floor domain.Tick
	err := tx.QueryRowContext(ctx, "SELECT tick FROM retirement_floors WHERE colony=? AND load_token=? AND map_id=?", current.Colony, current.Load, current.Map).Scan(&floor)
	if errors.Is(err, sql.ErrNoRows) {
		return nil
	}
	if err != nil {
		return err
	}
	if floor < 0 || tick < floor {
		return errors.New("observation predates retired accounting evidence")
	}
	return nil
}

// settledRetirementOutcome is dispatched evidence that settles an action for
// retirement, so it also sets the retirement floor: a completed effect, a
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
func retireRoutinePlans(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick, census domain.Fact[policy.CurrentConstruction]) error {
	rows, err := tx.QueryContext(ctx, "SELECT p.id,m.goal_id FROM plans p INDEXED BY active_plans CROSS JOIN goal_methods m ON m.plan_id=p.id WHERE p.retired=0 AND m.goal_id IS NOT NULL ORDER BY p.id LIMIT 257")
	if err != nil {
		return err
	}
	type link struct {
		plan domain.PlanID
		goal domain.GoalID
	}
	var links []link
	for rows.Next() {
		var v link
		if err = rows.Scan(&v.plan, &v.goal); err != nil {
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
		g, err := loadGoal(ctx, tx, v.goal)
		if err != nil {
			return err
		}
		if g.Goal.Source != domain.AutopilotGoal {
			continue
		}
		p, err := load(ctx, tx, v.plan)
		if err != nil {
			return err
		}
		if PlanWorkOpen(p, census) {
			continue
		}
		// A fight (#852) owns its empty plan while open or holding a
		// draft claim (#910): retiring it would hide the fight from its
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
		if g.Revision == ^uint64(0) {
			return ErrCapacity
		}
		for _, progress := range p.Progress {
			w := progress.View()
			if settledRetirementOutcome(progress) {
				s := w.Snapshot
				if _, err = tx.ExecContext(ctx, "INSERT INTO retirement_floors(colony,load_token,map_id,tick) VALUES(?,?,?,?) ON CONFLICT(colony,load_token,map_id) DO UPDATE SET tick=max(tick,excluded.tick)", s.Colony, s.Load, s.Map, w.Tick); err != nil {
					return err
				}
			}
		}
		if _, err = tx.ExecContext(ctx, "UPDATE plans SET retired=1 WHERE id=?", v.plan); err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, "UPDATE goals SET revision=? WHERE id=?", strconv.FormatUint(g.Revision+1, 10), v.goal); err != nil {
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
	if err = tx.QueryRowContext(ctx, "SELECT plan_id FROM goal_methods WHERE goal_id=? AND epoch=? AND method_id=?", goal, strconv.FormatUint(epoch, 10), method).Scan(&m.Plan); err != nil {
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
	err := s.db.QueryRowContext(ctx, "SELECT plan_id FROM goal_methods WHERE goal_id=? AND method_id=? ORDER BY CAST(epoch AS INTEGER) DESC LIMIT 1", goal, method).Scan(&id)
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
	rows, err := tx.QueryContext(ctx, "SELECT method_id,plan_id FROM goal_methods WHERE goal_id=? AND epoch=? ORDER BY method_id LIMIT 257", goal, strconv.FormatUint(epoch, 10))
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
