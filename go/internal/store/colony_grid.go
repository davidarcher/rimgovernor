package store

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The colony grid (#605) is recorded once per world and load and never
// moves. A new load re-establishes it from the live world (#1009); another
// colony or map has its own grid.

// ColonyGridRecord is a persisted grid with the tick and generation that
// established it.
type ColonyGridRecord struct {
	Grid     policy.ColonyGrid
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
}

func initializeColonyGrid(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE colony_grids(colony TEXT NOT NULL, map_id INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), native_generation INTEGER NOT NULL, origin_x INTEGER NOT NULL, origin_z INTEGER NOT NULL, pitch INTEGER NOT NULL CHECK(pitch>0), axis0_x INTEGER NOT NULL, axis0_z INTEGER NOT NULL, axis1_x INTEGER NOT NULL, axis1_z INTEGER NOT NULL, source TEXT NOT NULL, PRIMARY KEY(colony,map_id)) STRICT;`)
	return err
}
func checkColonyGridSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT colony,map_id,tick,native_generation,origin_x,origin_z,pitch,axis0_x,axis0_z,axis1_x,axis1_z,source FROM colony_grids LIMIT 0")
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

// colonyGrid reads the world's grid established at or before tick.
func colonyGrid(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) (ColonyGridRecord, bool, error) {
	var r ColonyGridRecord
	var generation domain.NativeGeneration
	var source string
	err := tx.QueryRowContext(ctx, "SELECT tick,native_generation,origin_x,origin_z,pitch,axis0_x,axis0_z,axis1_x,axis1_z,source FROM colony_grids WHERE colony=? AND map_id=? AND tick<=?", s.Colony, s.Map, tick).Scan(
		&r.Tick, &generation, &r.Grid.Origin.X, &r.Grid.Origin.Z, &r.Grid.Pitch, &r.Grid.Axes[0].X, &r.Grid.Axes[0].Z, &r.Grid.Axes[1].X, &r.Grid.Axes[1].Z, &source)
	if errors.Is(err, sql.ErrNoRows) {
		return ColonyGridRecord{}, false, nil
	}
	if err != nil {
		return ColonyGridRecord{}, false, err
	}
	r.Grid.Source = policy.ColonyGridSource(source)
	if !colonyGridValid(r.Grid) {
		return ColonyGridRecord{}, false, errors.New("invalid persisted colony grid")
	}
	r.Snapshot = domain.GenerationSnapshot{Colony: s.Colony, Map: s.Map, Load: s.Load, Plan: s.Plan, Revision: s.Revision, Native: generation}
	return r, true, nil
}

// EstablishColonyGrid records grid for the world at tick unless one is
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
	if held, ok, err := colonyGrid(ctx, tx, snapshot, tick); err != nil || ok {
		return held, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO colony_grids(colony,map_id,tick,native_generation,origin_x,origin_z,pitch,axis0_x,axis0_z,axis1_x,axis1_z,source) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
		snapshot.Colony, snapshot.Map, tick, snapshot.Native, grid.Origin.X, grid.Origin.Z, grid.Pitch, grid.Axes[0].X, grid.Axes[0].Z, grid.Axes[1].X, grid.Axes[1].Z, string(grid.Source)); err != nil {
		return ColonyGridRecord{}, false, err
	}
	return ColonyGridRecord{Grid: grid, Snapshot: snapshot, Tick: tick}, true, tx.Commit()
}

// ColonyGrid returns the grid the world holds at tick, if any.
func (s *Store) ColonyGrid(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) (ColonyGridRecord, bool, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return ColonyGridRecord{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ColonyGridRecord{}, false, err
	}
	defer tx.Rollback()
	record, ok, err := colonyGrid(ctx, tx, snapshot, tick)
	if err != nil {
		return ColonyGridRecord{}, false, err
	}
	return record, ok, tx.Commit()
}
