package store

import (
	"context"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StandardOrphanPass sees the plans a goal rebuild is about to retire, before
// they are retired. The reconcile pass cancels their native
// side effects here; nil skips it.
type StandardOrphanPass func(context.Context, []PlanState) error

// RebuildStandards replaces the store's standards with the save's standard/*
// blobs: the save wins. Standards absent from the save are deleted with their
// session-only create submissions; every standard method row is deleted and
// its plan retired after one orphans pass sees them all, so methods start
// empty and are re-planned. A save without standard blobs leaves no
// standards (D5). An incident's method rows are not touched.
func (s *Store) RebuildStandards(ctx context.Context, saved map[string]string, orphans StandardOrphanPass) error {
	goals := map[domain.StandardID]GovernorStandardBlob{}
	for key, blob := range saved {
		if !strings.HasPrefix(key, GovernorStandardKeyPrefix) {
			continue
		}
		var b GovernorStandardBlob
		if err := json.Unmarshal([]byte(blob), &b); err != nil {
			return fmt.Errorf("%s: %w", key, err)
		}
		if b.SchemaVersion != GovernorStateSchemaVersion {
			return fmt.Errorf("%s: schema version %d", key, b.SchemaVersion)
		}
		if err := b.Standard.Validate(); err != nil {
			return fmt.Errorf("%s: %w", key, err)
		}
		if string(b.Standard.ID) != strings.TrimPrefix(key, GovernorStandardKeyPrefix) {
			return fmt.Errorf("%s: standard identity mismatch", key)
		}
		goals[b.Standard.ID] = b
	}
	if orphans != nil {
		plans, err := s.methodPlans(ctx)
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
		"UPDATE plans SET retired=1 WHERE retired=0 AND id IN (SELECT plan_id FROM plan_owner WHERE kind='standard')",
		"DELETE FROM standard_methods",
		"DELETE FROM plan_owner WHERE kind='standard'",
	} {
		if _, err = tx.ExecContext(ctx, statement); err != nil {
			return err
		}
	}
	existing, err := standardIDs(ctx, tx)
	if err != nil {
		return err
	}
	for _, id := range existing {
		if _, ok := goals[id]; ok {
			continue
		}
		if _, err = tx.ExecContext(ctx, "DELETE FROM standards WHERE id=?", id); err != nil {
			return err
		}
	}
	for id, b := range goals {
		data, err := json.Marshal(b.Standard)
		if err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO standards(id,revision,payload,retired) VALUES(?,?,?,0) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload,retired=0", id, strconv.FormatUint(b.Revision, 10), data); err != nil {
			return err
		}
	}
	if err = tx.Commit(); err != nil {
		return err
	}
	s.floors.reset()
	return nil
}

// methodPlans loads every plan a method row binds.
func (s *Store) methodPlans(ctx context.Context) ([]PlanState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT plan_id FROM plan_owner WHERE kind='standard' ORDER BY plan_id")
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

func standardIDs(ctx context.Context, tx *Tx) ([]domain.StandardID, error) {
	rows, err := tx.QueryContext(ctx, "SELECT id FROM standards ORDER BY id")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []domain.StandardID
	for rows.Next() {
		var id domain.StandardID
		if err := rows.Scan(&id); err != nil {
			return nil, err
		}
		out = append(out, id)
	}
	return out, rows.Err()
}
