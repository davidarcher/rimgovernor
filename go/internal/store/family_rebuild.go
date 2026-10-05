package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RebuildFamilies replaces the family record tables with the save's family/*
// blobs on a world change (#1005, U1a): the tables are session caches of the
// save. A missing blob leaves its table empty.
func (s *Store) RebuildFamilies(ctx context.Context, saved map[string]string) error {
	blobs := map[string]GovernorFamilyBlob{}
	for _, key := range []string{GovernorLayoutPlanKey, GovernorDefenseLayoutKey, GovernorProductionLadderKey, GovernorSoldierSquadKey} {
		raw, ok := saved[key]
		if !ok {
			continue
		}
		var b GovernorFamilyBlob
		if err := json.Unmarshal([]byte(raw), &b); err != nil {
			return fmt.Errorf("%s: %w", key, err)
		}
		if b.SchemaVersion != GovernorStateSchemaVersion {
			return fmt.Errorf("%s: schema version %d", key, b.SchemaVersion)
		}
		if key == GovernorLayoutPlanKey && b.Scope == nil {
			return fmt.Errorf("%s: missing scope", key)
		}
		blobs[key] = b
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	for _, table := range []string{"colony_layout_plans", "defense_layout", "production_ladder", "soldier_squad", "colony_extent_events"} {
		if _, err = tx.ExecContext(ctx, "DELETE FROM "+table); err != nil {
			return err
		}
	}
	if b, ok := blobs[GovernorLayoutPlanKey]; ok {
		if err = rebuildLayoutPlan(ctx, tx, b); err != nil {
			return fmt.Errorf("%s: %w", GovernorLayoutPlanKey, err)
		}
	}
	if b, ok := blobs[GovernorDefenseLayoutKey]; ok {
		var r DefenseLayoutRecord
		if err = json.Unmarshal(b.Record, &r); err == nil {
			err = r.Validate()
		}
		if err == nil {
			err = putSingleton(ctx, tx, "defense_layout", r, maxDefenseLayoutBytes)
		}
		if err != nil {
			return fmt.Errorf("%s: %w", GovernorDefenseLayoutKey, err)
		}
	}
	if b, ok := blobs[GovernorProductionLadderKey]; ok {
		var r ProductionLadderRecord
		if err = json.Unmarshal(b.Record, &r); err == nil {
			sort.Strings(r.Research)
			err = r.Validate()
		}
		if err == nil {
			err = putSingleton(ctx, tx, "production_ladder", r, maxProductionLadderBytes)
		}
		if err != nil {
			return fmt.Errorf("%s: %w", GovernorProductionLadderKey, err)
		}
	}
	if b, ok := blobs[GovernorSoldierSquadKey]; ok {
		var r SoldierSquadRecord
		if err = json.Unmarshal(b.Record, &r); err == nil {
			err = r.Validate()
		}
		if err == nil {
			err = putSingleton(ctx, tx, "soldier_squad", r, maxSoldierSquadBytes)
		}
		if err != nil {
			return fmt.Errorf("%s: %w", GovernorSoldierSquadKey, err)
		}
	}
	return tx.Commit()
}

// ResetRounds empties the rounds session cache on a world
// change (#1011); the next review recomputes it from revision zero.
func (s *Store) ResetRounds(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, "DELETE FROM rounds")
	return err
}

func rebuildLayoutPlan(ctx context.Context, tx *sql.Tx, b GovernorFamilyBlob) error {
	var plan policy.LayoutPlan
	if err := json.Unmarshal(b.Record, &plan); err != nil {
		return err
	}
	if !plan.Valid() {
		return errors.New("invalid layout plan")
	}
	encoded, err := json.Marshal(plan)
	if err != nil {
		return err
	}
	_, err = tx.ExecContext(ctx, "INSERT INTO colony_layout_plans(colony,map_id,tick,plan) VALUES(?,?,?,?)", b.Scope.Colony, b.Scope.Map, b.Scope.Tick, string(encoded))
	return err
}

func putSingleton(ctx context.Context, tx *sql.Tx, table string, record any, limit int) error {
	data, err := json.Marshal(record)
	if err != nil {
		return err
	}
	if len(data) > limit {
		return ErrCapacity
	}
	_, err = tx.ExecContext(ctx, "INSERT INTO "+table+"(singleton,payload) VALUES(1,?)", data)
	return err
}
