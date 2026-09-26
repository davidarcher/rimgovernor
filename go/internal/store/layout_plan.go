package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The v2 layout plan (#777) rides the same timeline segments as the v1
// module rows it replaces: a load sees the newest plan its lineage
// recorded at or before the tick, and a same-load rewind past a replan
// forgets it.

// LayoutPlanRecord is a persisted layout plan with the tick that recorded
// it.
type LayoutPlanRecord struct {
	Plan policy.LayoutPlan
	Tick domain.Tick
	// Invalid marks a newest saved plan that no longer decodes or passes
	// Valid: the timeline reads as holding no plan, so the colony replans.
	Invalid bool
}

func initializeLayoutPlan(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE colony_layout_plans(colony TEXT NOT NULL, map_id INTEGER NOT NULL, load_token TEXT NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), plan TEXT NOT NULL, PRIMARY KEY(colony,map_id,load_token,tick)) STRICT;`)
	return err
}
func checkLayoutPlanSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT colony,map_id,load_token,tick,plan FROM colony_layout_plans LIMIT 0")
	return err
}

func discardLayoutPlan(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) error {
	_, err := tx.ExecContext(ctx, "DELETE FROM colony_layout_plans WHERE colony=? AND map_id=? AND load_token=? AND tick>?", s.Colony, s.Map, s.Load, tick)
	return err
}

// RecordLayoutPlan records a layout plan for the timeline at tick. The
// caller records only a new plan or a deliberate replan.
func (s *Store) RecordLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, plan policy.LayoutPlan) error {
	if err := extentScope(snapshot, tick); err != nil {
		return err
	}
	if !plan.Valid() {
		return errors.New("invalid layout plan")
	}
	encoded, err := json.Marshal(plan)
	if err != nil {
		return err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = reconcileColonyExtent(ctx, tx, snapshot, tick); err != nil {
		return err
	}
	if _, err = tx.ExecContext(ctx, "INSERT OR REPLACE INTO colony_layout_plans(colony,map_id,load_token,tick,plan) VALUES(?,?,?,?,?)", snapshot.Colony, snapshot.Map, snapshot.Load, tick, string(encoded)); err != nil {
		return err
	}
	return tx.Commit()
}

// LayoutPlan returns the layout plan the timeline sees at tick, if any.
func (s *Store) LayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) (LayoutPlanRecord, bool, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return LayoutPlanRecord{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return LayoutPlanRecord{}, false, err
	}
	defer tx.Rollback()
	if _, err = reconcileColonyExtent(ctx, tx, snapshot, tick); err != nil {
		return LayoutPlanRecord{}, false, err
	}
	lineage, err := extentLineage(ctx, tx, snapshot, tick)
	if err != nil {
		return LayoutPlanRecord{}, false, err
	}
	for _, segment := range lineage {
		var r LayoutPlanRecord
		var plan string
		err := tx.QueryRowContext(ctx, "SELECT tick,plan FROM colony_layout_plans WHERE colony=? AND map_id=? AND load_token=? AND tick<=? ORDER BY tick DESC LIMIT 1", snapshot.Colony, snapshot.Map, segment.load, segment.limit).Scan(&r.Tick, &plan)
		if errors.Is(err, sql.ErrNoRows) {
			continue
		}
		if err != nil {
			return LayoutPlanRecord{}, false, err
		}
		if json.Unmarshal([]byte(plan), &r.Plan) != nil || !r.Plan.Valid() {
			return LayoutPlanRecord{Tick: r.Tick, Invalid: true}, false, tx.Commit()
		}
		return r, true, tx.Commit()
	}
	return LayoutPlanRecord{}, false, tx.Commit()
}
