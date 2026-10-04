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

// StandardOrphanPass sees the plans a goal rebuild is about to retire, before
// they are retired (#998). The #1000 reconcile pass cancels their native
// side effects here; nil skips it.
type StandardOrphanPass func(context.Context, []PlanState) error

// RebuildStandards replaces the store's goals and projects with the save's goal/*
// and project/* blobs (#998, #1926): the save wins. Goals and projects absent
// from the save are deleted with their session-only create submissions; every
// goal and project method row is deleted and its plan retired after one
// orphans pass sees them all, so methods start empty and are re-planned. A
// save without goal blobs leaves no goals (D5), without project blobs no
// projects. An incident's method rows are not touched.
func (s *Store) RebuildStandards(ctx context.Context, saved map[string]string, orphans StandardOrphanPass) error {
	goals := map[domain.ConcernID]GovernorStandardBlob{}
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
			return fmt.Errorf("%s: goal identity mismatch", key)
		}
		goals[b.Standard.ID] = b
	}
	projects, err := parseProjectBlobs(saved)
	if err != nil {
		return err
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
		"UPDATE plans SET retired=1 WHERE retired=0 AND id IN (SELECT plan_id FROM plan_owner WHERE kind IN ('standard','project'))",
		"DELETE FROM standard_methods",
		"DELETE FROM project_methods",
		"DELETE FROM plan_owner WHERE kind IN ('standard','project')",
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
	if err = rebuildProjects(ctx, tx, projects); err != nil {
		return err
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
	rows, err := tx.QueryContext(ctx, "SELECT plan_id FROM plan_owner WHERE kind IN ('standard','project') ORDER BY plan_id")
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

func parseProjectBlobs(saved map[string]string) (map[domain.ProjectID]GovernorProjectBlob, error) {
	projects := map[domain.ProjectID]GovernorProjectBlob{}
	for key, blob := range saved {
		if !strings.HasPrefix(key, GovernorProjectKeyPrefix) {
			continue
		}
		var b GovernorProjectBlob
		if err := json.Unmarshal([]byte(blob), &b); err != nil {
			return nil, fmt.Errorf("%s: %w", key, err)
		}
		if b.SchemaVersion != GovernorStateSchemaVersion {
			return nil, fmt.Errorf("%s: schema version %d", key, b.SchemaVersion)
		}
		if err := b.Project.Validate(); err != nil {
			return nil, fmt.Errorf("%s: %w", key, err)
		}
		if string(b.Project.ID) != strings.TrimPrefix(key, GovernorProjectKeyPrefix) {
			return nil, fmt.Errorf("%s: project identity mismatch", key)
		}
		projects[b.Project.ID] = b
	}
	return projects, nil
}

// rebuildProjects is RebuildStandards' project half, in its transaction: its
// method rows are already deleted and their plans retired.
func rebuildProjects(ctx context.Context, tx *sql.Tx, projects map[domain.ProjectID]GovernorProjectBlob) error {
	rows, err := tx.QueryContext(ctx, "SELECT id FROM projects ORDER BY id")
	if err != nil {
		return err
	}
	var existing []domain.ProjectID
	for rows.Next() {
		var id domain.ProjectID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return err
		}
		existing = append(existing, id)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return err
	}
	for _, id := range existing {
		if _, ok := projects[id]; ok {
			continue
		}
		if _, err = tx.ExecContext(ctx, "DELETE FROM projects WHERE id=?", id); err != nil {
			return err
		}
	}
	for id, b := range projects {
		data, err := json.Marshal(b.Project)
		if err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO projects(id,revision,payload,retired) VALUES(?,?,?,0) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload,retired=0", id, strconv.FormatUint(b.Revision, 10), data); err != nil {
			return err
		}
	}
	return nil
}

func goalIDs(ctx context.Context, tx *sql.Tx) ([]domain.ConcernID, error) {
	rows, err := tx.QueryContext(ctx, "SELECT id FROM standards ORDER BY id")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []domain.ConcernID
	for rows.Next() {
		var id domain.ConcernID
		if err := rows.Scan(&id); err != nil {
			return nil, err
		}
		out = append(out, id)
	}
	return out, rows.Err()
}
