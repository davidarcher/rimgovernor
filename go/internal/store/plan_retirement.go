package store

import (
	"context"
	"database/sql"
	"errors"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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

// Only settled autopilot methods retire. Current control, unresolved effects,
// cleanup and unsuccessful work keep their complete catalog entries.
func retireRoutinePlans(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) error {
	control, err := currentControl(ctx, tx)
	if err != nil && !errors.Is(err, ErrNotFound) {
		return err
	}
	rows, err := tx.QueryContext(ctx, "SELECT p.id,m.goal_id FROM plans p INDEXED BY active_plans CROSS JOIN goal_methods m ON m.plan_id=p.id WHERE p.retired=0 ORDER BY p.id LIMIT 257")
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
		if v.plan == current.Plan || v.plan == control.Request.Plan {
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
		if domain.GoalWorkOpen(p.Progress) {
			continue
		}
		settled := true
		for _, progress := range p.Progress {
			w := progress.View()
			effect, known := w.Effect.Value()
			absent := w.Stage == domain.Cancelled && (w.Attempt == 0 || known && effect == domain.EffectAbsent)
			settledOutcome := known && ((effect == domain.EffectCompleted && (w.Stage == domain.Completed || w.Stage == domain.Cancelled)) || (progress.Action().Kind() == domain.BuildingAction && effect == domain.EffectUnsuccessful && (w.Stage == domain.Unsuccessful || w.Stage == domain.Cancelled)))
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
			if effect, known := w.Effect.Value(); known && (effect == domain.EffectCompleted || effect == domain.EffectUnsuccessful) {
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
