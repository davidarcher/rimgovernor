package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The master layout plan's reserved modules (#727) ride beside the colony
// grid on the same timeline segments. The grid never moves; a replan
// records a new module set at its tick, so a load sees the newest set its
// lineage recorded at or before the tick, and a same-load rewind past a
// replan forgets it.

// ColonyPlanRecord is a persisted module set with the tick that recorded
// it.
type ColonyPlanRecord struct {
	Radius  int32
	Modules []policy.PlanModule
	Tick    domain.Tick
}

func initializeColonyPlan(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE colony_master_plans(colony TEXT NOT NULL, map_id INTEGER NOT NULL, load_token TEXT NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), radius INTEGER NOT NULL CHECK(radius>0), modules TEXT NOT NULL, PRIMARY KEY(colony,map_id,load_token,tick)) STRICT;`)
	return err
}
func checkColonyPlanSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT colony,map_id,load_token,tick,radius,modules FROM colony_master_plans LIMIT 0")
	return err
}

func discardColonyPlan(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) error {
	_, err := tx.ExecContext(ctx, "DELETE FROM colony_master_plans WHERE colony=? AND map_id=? AND load_token=? AND tick>?", s.Colony, s.Map, s.Load, tick)
	return err
}

func colonyPlanValid(radius int32, modules []policy.PlanModule) bool {
	if radius < 1 || radius > 64 || len(modules) == 0 {
		return false
	}
	for _, m := range modules {
		if max(m.U, -m.U, m.V, -m.V) > radius || m.Role == "" {
			return false
		}
	}
	return true
}

// colonyPlan reads the newest module set visible through the lineage.
func colonyPlan(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) (ColonyPlanRecord, bool, error) {
	lineage, err := extentLineage(ctx, tx, s, tick)
	if err != nil {
		return ColonyPlanRecord{}, false, err
	}
	for _, segment := range lineage {
		var r ColonyPlanRecord
		var modules string
		err := tx.QueryRowContext(ctx, "SELECT tick,radius,modules FROM colony_master_plans WHERE colony=? AND map_id=? AND load_token=? AND tick<=? ORDER BY tick DESC LIMIT 1", s.Colony, s.Map, segment.load, segment.limit).Scan(&r.Tick, &r.Radius, &modules)
		if errors.Is(err, sql.ErrNoRows) {
			continue
		}
		if err != nil {
			return ColonyPlanRecord{}, false, err
		}
		if json.Unmarshal([]byte(modules), &r.Modules) != nil || !colonyPlanValid(r.Radius, r.Modules) {
			return ColonyPlanRecord{}, false, errors.New("invalid persisted colony plan")
		}
		return r, true, nil
	}
	return ColonyPlanRecord{}, false, nil
}

// RecordColonyPlan records a module set for the timeline at tick. The
// caller records only a new plan or a deliberate replan; the grid it lies
// on is EstablishColonyGrid's.
func (s *Store) RecordColonyPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, radius int32, modules []policy.PlanModule) error {
	if err := extentScope(snapshot, tick); err != nil {
		return err
	}
	if !colonyPlanValid(radius, modules) {
		return errors.New("invalid colony plan")
	}
	encoded, err := json.Marshal(modules)
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
	if _, err = tx.ExecContext(ctx, "INSERT OR REPLACE INTO colony_master_plans(colony,map_id,load_token,tick,radius,modules) VALUES(?,?,?,?,?,?)", snapshot.Colony, snapshot.Map, snapshot.Load, tick, radius, string(encoded)); err != nil {
		return err
	}
	return tx.Commit()
}

// ColonyPlan returns the module set the timeline sees at tick, if any.
func (s *Store) ColonyPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) (ColonyPlanRecord, bool, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return ColonyPlanRecord{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ColonyPlanRecord{}, false, err
	}
	defer tx.Rollback()
	if _, err = reconcileColonyExtent(ctx, tx, snapshot, tick); err != nil {
		return ColonyPlanRecord{}, false, err
	}
	record, ok, err := colonyPlan(ctx, tx, snapshot, tick)
	if err != nil {
		return ColonyPlanRecord{}, false, err
	}
	return record, ok, tx.Commit()
}
