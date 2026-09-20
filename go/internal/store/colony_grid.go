package store

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The colony grid (#605) is recorded once per world and timeline and never
// moves: it shares the colony extent's timeline segments, so a load sees
// the grid its own lineage established at or before its fork, an older
// save restores the grid that save knew (or none), and a same-load tick
// rewind past the grid's tick forgets it. Another colony or map has its
// own grid.

// ColonyGridRecord is a persisted grid with the tick and generation that
// established it.
type ColonyGridRecord struct {
	Grid     policy.ColonyGrid
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
}

func initializeColonyGrid(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE colony_grids(colony TEXT NOT NULL, map_id INTEGER NOT NULL, load_token TEXT NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), native_generation INTEGER NOT NULL, origin_x INTEGER NOT NULL, origin_z INTEGER NOT NULL, pitch INTEGER NOT NULL CHECK(pitch>0), axis0_x INTEGER NOT NULL, axis0_z INTEGER NOT NULL, axis1_x INTEGER NOT NULL, axis1_z INTEGER NOT NULL, source TEXT NOT NULL, PRIMARY KEY(colony,map_id,load_token)) STRICT;`)
	return err
}
func checkColonyGridSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT colony,map_id,load_token,tick,native_generation,origin_x,origin_z,pitch,axis0_x,axis0_z,axis1_x,axis1_z,source FROM colony_grids LIMIT 0")
	return err
}

func colonyGridValid(g policy.ColonyGrid) bool {
	if !g.Valid() || g.Origin.X < 0 || g.Origin.Z < 0 || g.Origin.X >= 4096 || g.Origin.Z >= 4096 {
		return false
	}
	switch g.Source {
	case policy.ColonyGridFromStarter, policy.ColonyGridFromRoom:
		return true
	}
	return false
}

// discardColonyGrid forgets a load's grid established after tick, the
// same-load rewind rule the extent journal applies to its events.
func discardColonyGrid(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) error {
	_, err := tx.ExecContext(ctx, "DELETE FROM colony_grids WHERE colony=? AND map_id=? AND load_token=? AND tick>?", s.Colony, s.Map, s.Load, tick)
	return err
}

// colonyGrid reads the grid visible through the lineage at tick: the
// oldest ancestor's first, since a grid never moves once established.
func colonyGrid(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) (ColonyGridRecord, bool, error) {
	lineage, err := extentLineage(ctx, tx, s, tick)
	if err != nil {
		return ColonyGridRecord{}, false, err
	}
	for i := len(lineage) - 1; i >= 0; i-- {
		segment := lineage[i]
		var r ColonyGridRecord
		var generation domain.NativeGeneration
		var source string
		err := tx.QueryRowContext(ctx, "SELECT tick,native_generation,origin_x,origin_z,pitch,axis0_x,axis0_z,axis1_x,axis1_z,source FROM colony_grids WHERE colony=? AND map_id=? AND load_token=? AND tick<=?", s.Colony, s.Map, segment.load, segment.limit).Scan(
			&r.Tick, &generation, &r.Grid.Origin.X, &r.Grid.Origin.Z, &r.Grid.Pitch, &r.Grid.Axes[0].X, &r.Grid.Axes[0].Z, &r.Grid.Axes[1].X, &r.Grid.Axes[1].Z, &source)
		if errors.Is(err, sql.ErrNoRows) {
			continue
		}
		if err != nil {
			return ColonyGridRecord{}, false, err
		}
		r.Grid.Source = policy.ColonyGridSource(source)
		if !colonyGridValid(r.Grid) {
			return ColonyGridRecord{}, false, errors.New("invalid persisted colony grid")
		}
		r.Snapshot = domain.GenerationSnapshot{Colony: s.Colony, Map: s.Map, Load: segment.load, Plan: s.Plan, Revision: s.Revision, Native: generation}
		return r, true, nil
	}
	return ColonyGridRecord{}, false, nil
}

// EstablishColonyGrid records grid for the timeline at tick unless one is
// already visible, in which case the visible grid is returned unchanged
// and established reports false.
func (s *Store) EstablishColonyGrid(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, grid policy.ColonyGrid) (ColonyGridRecord, bool, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return ColonyGridRecord{}, false, err
	}
	if !colonyGridValid(grid) {
		return ColonyGridRecord{}, false, errors.New("invalid colony grid")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ColonyGridRecord{}, false, err
	}
	defer tx.Rollback()
	if _, err = reconcileColonyExtent(ctx, tx, snapshot, tick); err != nil {
		return ColonyGridRecord{}, false, err
	}
	if held, ok, err := colonyGrid(ctx, tx, snapshot, tick); err != nil || ok {
		return held, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO colony_grids(colony,map_id,load_token,tick,native_generation,origin_x,origin_z,pitch,axis0_x,axis0_z,axis1_x,axis1_z,source) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)",
		snapshot.Colony, snapshot.Map, snapshot.Load, tick, snapshot.Native, grid.Origin.X, grid.Origin.Z, grid.Pitch, grid.Axes[0].X, grid.Axes[0].Z, grid.Axes[1].X, grid.Axes[1].Z, string(grid.Source)); err != nil {
		return ColonyGridRecord{}, false, err
	}
	return ColonyGridRecord{Grid: grid, Snapshot: snapshot, Tick: tick}, true, tx.Commit()
}

// ColonyGrid returns the grid the timeline sees at tick, if any. The read
// binds the load to its timeline first (ReconcileColonyExtent), so a
// freshly loaded save finds the grid its origin segment established.
func (s *Store) ColonyGrid(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) (ColonyGridRecord, bool, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return ColonyGridRecord{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ColonyGridRecord{}, false, err
	}
	defer tx.Rollback()
	if _, err = reconcileColonyExtent(ctx, tx, snapshot, tick); err != nil {
		return ColonyGridRecord{}, false, err
	}
	record, ok, err := colonyGrid(ctx, tx, snapshot, tick)
	if err != nil {
		return ColonyGridRecord{}, false, err
	}
	return record, ok, tx.Commit()
}
