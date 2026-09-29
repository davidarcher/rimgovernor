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
	for _, key := range []string{GovernorLayoutPlanKey, GovernorTidiesKey, GovernorDefenseLayoutKey, GovernorProductionLadderKey} {
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
		if (key == GovernorLayoutPlanKey || key == GovernorTidiesKey) && b.Scope == nil {
			return fmt.Errorf("%s: missing scope", key)
		}
		blobs[key] = b
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	for _, table := range []string{"colony_layout_plans", "layout_tidies", "defense_layout", "production_ladder", "colony_extent_events"} {
		if _, err = tx.ExecContext(ctx, "DELETE FROM "+table); err != nil {
			return err
		}
	}
	if b, ok := blobs[GovernorLayoutPlanKey]; ok {
		if err = rebuildLayoutPlan(ctx, tx, b); err != nil {
			return fmt.Errorf("%s: %w", GovernorLayoutPlanKey, err)
		}
	}
	if b, ok := blobs[GovernorTidiesKey]; ok {
		if err = rebuildLayoutTidies(ctx, tx, b); err != nil {
			return fmt.Errorf("%s: %w", GovernorTidiesKey, err)
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
	return tx.Commit()
}

// ResetRoutineReview empties the routine_review session cache on a world
// change (#1011); the next review recomputes it from revision zero.
func (s *Store) ResetRoutineReview(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, "DELETE FROM routine_review")
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

// rebuildLayoutTidies inserts the latest state per item oldest first, so the
// newest row carries the blob's scope tick.
func rebuildLayoutTidies(ctx context.Context, tx *sql.Tx, b GovernorFamilyBlob) error {
	var tidies []LayoutTidy
	if err := json.Unmarshal(b.Record, &tidies); err != nil {
		return err
	}
	sort.SliceStable(tidies, func(i, j int) bool {
		if tidies[i].Tick != tidies[j].Tick {
			return tidies[i].Tick < tidies[j].Tick
		}
		return tidies[i].Item < tidies[j].Item
	})
	sc := b.Scope
	for _, t := range tidies {
		if !layoutTidyValid(t) {
			return errors.New("invalid layout tidy")
		}
		if _, err := tx.ExecContext(ctx, "INSERT INTO layout_tidies(colony,map_id,tick,item,kind,status,from_x,from_z,from_w,from_h,to_x,to_z,to_w,to_h,crop,new_zone,plan_id,explanation) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
			sc.Colony, sc.Map, t.Tick, t.Item, string(t.Kind), string(t.Status), t.From.X, t.From.Z, t.From.Width, t.From.Height, t.To.X, t.To.Z, t.To.Width, t.To.Height, t.Crop, t.NewZone, t.PlanID, t.Explanation); err != nil {
			return err
		}
	}
	return nil
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
