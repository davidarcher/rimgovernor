package store

import (
	"context"
	"database/sql"
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Layout tidies (#611) are the re-sites TidyLayout has moved or is moving,
// journaled per world (colony, map) so a tidied item never loops. The table
// is a session cache rebuilt from the save on a world change (#1005,
// #1009); each status change is its own row, and the latest row per item
// at or before the tick is the item's state.

// LayoutTidyStatus is the state of one re-site.
type LayoutTidyStatus string

const (
	// LayoutTidyMoving marks a re-site in flight: the new zone is admitted
	// and the old one still stands.
	LayoutTidyMoving LayoutTidyStatus = "moving"
	// LayoutTidyDone marks a finished re-site: the old zone or shell is gone.
	LayoutTidyDone LayoutTidyStatus = "done"
	// LayoutTidyAbandoned marks a re-site given up (the new zone's method
	// was refused or retired); the item is still never proposed again.
	LayoutTidyAbandoned LayoutTidyStatus = "abandoned"
)

// LayoutTidy is one recorded re-site. A furniture row (#809) is one piece
// by thing id; its PlanID is the batch plan's id, which the moving rows
// of one room share.
type LayoutTidy struct {
	Item        string
	Kind        policy.TidyKind
	Status      LayoutTidyStatus
	From        policy.Rectangle
	To          policy.Rectangle
	Crop        string
	NewZone     string
	PlanID      string
	Explanation string
	Tick        domain.Tick
}

func initializeLayoutTidies(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE layout_tidies(id INTEGER PRIMARY KEY, colony TEXT NOT NULL, map_id INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), item TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('field','stockpile','shell','furniture')), status TEXT NOT NULL CHECK(status IN ('moving','done','abandoned')), from_x INTEGER NOT NULL, from_z INTEGER NOT NULL, from_w INTEGER NOT NULL, from_h INTEGER NOT NULL, to_x INTEGER NOT NULL, to_z INTEGER NOT NULL, to_w INTEGER NOT NULL, to_h INTEGER NOT NULL, crop TEXT NOT NULL, new_zone TEXT NOT NULL, plan_id TEXT NOT NULL, explanation TEXT NOT NULL) STRICT;
CREATE INDEX layout_tidies_scope ON layout_tidies(colony,map_id,tick);`)
	return err
}
func checkLayoutTidySchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT id,colony,map_id,tick,item,kind,status,from_x,from_z,from_w,from_h,to_x,to_z,to_w,to_h,crop,new_zone,plan_id,explanation FROM layout_tidies LIMIT 0")
	return err
}

func layoutTidyValid(t LayoutTidy) bool {
	switch t.Kind {
	case policy.TidyFurniture:
	default:
		return false
	}
	switch t.Status {
	case LayoutTidyMoving, LayoutTidyDone, LayoutTidyAbandoned:
	default:
		return false
	}
	return t.Item != "" && len(t.Item) <= 256 && len(t.Crop) <= 256 && len(t.NewZone) <= 256 && len(t.PlanID) <= 256 && len(t.Explanation) <= 1024 && t.From.Width >= 0 && t.From.Height >= 0 && t.To.Width >= 0 && t.To.Height >= 0
}

// layoutTidies reads the latest row per item at or before tick, sorted by
// item.
func layoutTidies(ctx context.Context, tx *sql.Tx, s domain.GenerationSnapshot, tick domain.Tick) ([]LayoutTidy, error) {
	// Stockpile rows predate #725 and are ignored (#933).
	rows, err := tx.QueryContext(ctx, "SELECT tick,item,kind,status,from_x,from_z,from_w,from_h,to_x,to_z,to_w,to_h,crop,new_zone,plan_id,explanation FROM layout_tidies WHERE colony=? AND map_id=? AND tick<=? AND kind<>'stockpile' ORDER BY id DESC", s.Colony, s.Map, tick)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	latest := map[string]LayoutTidy{}
	for rows.Next() {
		var t LayoutTidy
		var kind, status string
		if err := rows.Scan(&t.Tick, &t.Item, &kind, &status, &t.From.X, &t.From.Z, &t.From.Width, &t.From.Height, &t.To.X, &t.To.Z, &t.To.Width, &t.To.Height, &t.Crop, &t.NewZone, &t.PlanID, &t.Explanation); err != nil {
			return nil, err
		}
		t.Kind, t.Status = policy.TidyKind(kind), LayoutTidyStatus(status)
		if !layoutTidyValid(t) {
			return nil, errors.New("invalid persisted layout tidy")
		}
		if _, seen := latest[t.Item]; !seen {
			latest[t.Item] = t
		}
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	out := make([]LayoutTidy, 0, len(latest))
	for _, t := range latest {
		out = append(out, t)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Item < out[j].Item })
	return out, nil
}

// RecordLayoutTidy appends a tidy's state for the world at tick.
func (s *Store) RecordLayoutTidy(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, t LayoutTidy) error {
	if err := extentScope(snapshot, tick); err != nil {
		return err
	}
	if !layoutTidyValid(t) {
		return errors.New("invalid layout tidy")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = tx.ExecContext(ctx, "INSERT INTO layout_tidies(colony,map_id,tick,item,kind,status,from_x,from_z,from_w,from_h,to_x,to_z,to_w,to_h,crop,new_zone,plan_id,explanation) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
		snapshot.Colony, snapshot.Map, tick, t.Item, string(t.Kind), string(t.Status), t.From.X, t.From.Z, t.From.Width, t.From.Height, t.To.X, t.To.Z, t.To.Width, t.To.Height, t.Crop, t.NewZone, t.PlanID, t.Explanation); err != nil {
		return err
	}
	return tx.Commit()
}

// LayoutTidies returns the tidies the world holds at tick, latest state
// per item, sorted by item.
func (s *Store) LayoutTidies(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) ([]LayoutTidy, error) {
	if err := extentScope(snapshot, tick); err != nil {
		return nil, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	out, err := layoutTidies(ctx, tx, snapshot, tick)
	if err != nil {
		return nil, err
	}
	return out, tx.Commit()
}
