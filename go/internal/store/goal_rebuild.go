package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// GoalOrphanPass sees the plans a goal rebuild is about to retire, before
// they are retired (#998). The #1000 reconcile pass cancels their native
// side effects here; nil skips it.
type GoalOrphanPass func(context.Context, []PlanState) error

// RebuildGoals replaces the store's goals with the save's goal/* blobs
// (#998): the save wins. Goals absent from the save are deleted with their
// session-only create submissions; every goal method row is deleted and its plan retired
// after orphans sees it, so methods start empty and are re-planned. A save
// without goal blobs leaves no goals (D5).
func (s *Store) RebuildGoals(ctx context.Context, saved map[string]string, orphans GoalOrphanPass) error {
	goals := map[domain.GoalID]GovernorGoalBlob{}
	for key, blob := range saved {
		if !strings.HasPrefix(key, GovernorGoalKeyPrefix) {
			continue
		}
		var b GovernorGoalBlob
		if err := json.Unmarshal([]byte(blob), &b); err != nil {
			return fmt.Errorf("%s: %w", key, err)
		}
		if b.SchemaVersion != GovernorStateSchemaVersion {
			return fmt.Errorf("%s: schema version %d", key, b.SchemaVersion)
		}
		if err := b.Goal.Validate(); err != nil {
			return fmt.Errorf("%s: %w", key, err)
		}
		if string(b.Goal.ID) != strings.TrimPrefix(key, GovernorGoalKeyPrefix) {
			return fmt.Errorf("%s: goal identity mismatch", key)
		}
		goals[b.Goal.ID] = b
	}
	if orphans != nil {
		plans, err := s.goalMethodPlans(ctx)
		if err != nil {
			return err
		}
		if len(plans) > 0 {
			if err = orphans(ctx, plans); err != nil {
				return err
			}
		}
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	for _, statement := range []string{
		"UPDATE plans SET retired=1 WHERE retired=0 AND id IN (SELECT plan_id FROM goal_methods WHERE goal_id IS NOT NULL)",
		"DELETE FROM goal_methods WHERE goal_id IS NOT NULL",
	} {
		if _, err = tx.ExecContext(ctx, statement); err != nil {
			return err
		}
	}
	existing, err := goalIDs(ctx, tx)
	if err != nil {
		return err
	}
	for _, id := range existing {
		if _, ok := goals[id]; ok {
			continue
		}
		for _, statement := range []string{"DELETE FROM goal_create_submissions WHERE goal_id=?", "DELETE FROM goals WHERE id=?"} {
			if _, err = tx.ExecContext(ctx, statement, id); err != nil {
				return err
			}
		}
	}
	for id, b := range goals {
		data, err := json.Marshal(b.Goal)
		if err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO goals(id,revision,payload,retired) VALUES(?,?,?,0) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload,retired=0", id, strconv.FormatUint(b.Revision, 10), data); err != nil {
			return err
		}
	}
	if err = tx.Commit(); err != nil {
		return err
	}
	s.floors.reset()
	return nil
}

// goalMethodPlans loads every plan a goal method row binds.
func (s *Store) goalMethodPlans(ctx context.Context) ([]PlanState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT plan_id FROM goal_methods WHERE goal_id IS NOT NULL ORDER BY plan_id")
	if err != nil {
		return nil, err
	}
	var ids []domain.PlanID
	for rows.Next() {
		var id domain.PlanID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return nil, err
		}
		ids = append(ids, id)
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return nil, err
	}
	out := make([]PlanState, 0, len(ids))
	for _, id := range ids {
		p, err := load(ctx, tx, id)
		if err != nil {
			return nil, fmt.Errorf("plan %s: %w", id, err)
		}
		out = append(out, p)
	}
	return out, nil
}

func goalIDs(ctx context.Context, tx *sql.Tx) ([]domain.GoalID, error) {
	rows, err := tx.QueryContext(ctx, "SELECT id FROM goals ORDER BY id")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []domain.GoalID
	for rows.Next() {
		var id domain.GoalID
		if err := rows.Scan(&id); err != nil {
			return nil, err
		}
		out = append(out, id)
	}
	return out, rows.Err()
}
