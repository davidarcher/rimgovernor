// Package store persists fresh Go building plans and validated domain transitions.
package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/url"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"modernc.org/sqlite"
)

const schemaVersion = 1
const applicationID = 0x52474f31

var ErrConflict = errors.New("plan or action identity already exists")
var ErrNotFound = errors.New("plan or action not found")

type Store struct{ db *sql.DB }
type PlanState struct {
	Spec     domain.PlanSpec
	Progress []domain.Progress
}

// Open accepts a filesystem path, never a caller-supplied SQLite connection URI.
func Open(ctx context.Context, path string) (*Store, error) {
	if path == "" {
		return nil, errors.New("database path is empty")
	}
	abs, err := filepath.Abs(path)
	if err != nil {
		return nil, err
	}
	uriPath := filepath.ToSlash(abs)
	if !strings.HasPrefix(uriPath, "/") {
		uriPath = "/" + uriPath
	}
	u := url.URL{Scheme: "file", Path: uriPath}
	q := u.Query()
	q.Set("_txlock", "immediate")
	q.Set("_busy_timeout", "25")
	q.Add("_pragma", "foreign_keys(1)")
	q.Add("_pragma", "synchronous(FULL)")
	u.RawQuery = q.Encode()
	db, err := sql.Open("sqlite", u.String())
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	s := &Store{db: db}
	if err = s.initialize(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	return s, nil
}
func (s *Store) Close() error { return s.db.Close() }

func (s *Store) begin(ctx context.Context) (*sql.Tx, error) {
	for {
		tx, err := s.db.BeginTx(ctx, nil)
		if err == nil {
			return tx, nil
		}
		var sqliteErr *sqlite.Error
		if !errors.As(err, &sqliteErr) || (sqliteErr.Code()&255 != 5 && sqliteErr.Code()&255 != 6) {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(10 * time.Millisecond):
		}
	}
}
func (s *Store) initialize(ctx context.Context) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	var version, app int
	if err = tx.QueryRowContext(ctx, "PRAGMA user_version").Scan(&version); err != nil {
		return err
	}
	if err = tx.QueryRowContext(ctx, "PRAGMA application_id").Scan(&app); err != nil {
		return err
	}
	if version == 0 && app == 0 {
		var count int
		if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'").Scan(&count); err != nil {
			return err
		}
		if count != 0 {
			return errors.New("unversioned nonempty database is incompatible")
		}
		_, err = tx.ExecContext(ctx, `CREATE TABLE plans(id TEXT PRIMARY KEY, revision TEXT NOT NULL);
CREATE TABLE actions(id TEXT PRIMARY KEY, plan_id TEXT NOT NULL REFERENCES plans(id), ordinal INTEGER NOT NULL, definition TEXT NOT NULL, x INTEGER NOT NULL, z INTEGER NOT NULL, rotation TEXT NOT NULL, stuff TEXT NOT NULL, UNIQUE(plan_id,ordinal));
CREATE TABLE transitions(sequence INTEGER PRIMARY KEY, action_id TEXT NOT NULL REFERENCES actions(id), payload BLOB NOT NULL);
CREATE INDEX action_transitions ON transitions(action_id,sequence);`)
		if err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, fmt.Sprintf("PRAGMA user_version=%d", schemaVersion)); err != nil {
			return err
		}
		if _, err = tx.ExecContext(ctx, fmt.Sprintf("PRAGMA application_id=%d", applicationID)); err != nil {
			return err
		}
	} else if version != schemaVersion || app != applicationID {
		return fmt.Errorf("incompatible database application/version: %d/%d", app, version)
	}
	// A matching version marker alone does not establish the expected tables.
	if _, err = tx.ExecContext(ctx, "SELECT p.id,p.revision,a.id,a.ordinal,a.definition,a.x,a.z,a.rotation,a.stuff,t.sequence,t.payload FROM plans p LEFT JOIN actions a ON a.plan_id=p.id LEFT JOIN transitions t ON t.action_id=a.id LIMIT 0"); err != nil {
		return err
	}
	return tx.Commit()
}

// CreatePlan initializes every action atomically. IDs remain unique across plans.
func (s *Store) CreatePlan(ctx context.Context, plan domain.PlanSpec) error {
	if _, err := domain.NewPlan(plan.ID(), plan.Revision(), plan.Actions()); err != nil {
		return err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = tx.ExecContext(ctx, "INSERT INTO plans(id,revision) VALUES(?,?)", plan.ID(), strconv.FormatUint(uint64(plan.Revision()), 10)); err != nil {
		return conflict(err)
	}
	for i, a := range plan.Actions() {
		b, ok := a.Building()
		if !ok {
			return errors.New("unsupported persisted action")
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO actions(id,plan_id,ordinal,definition,x,z,rotation,stuff) VALUES(?,?,?,?,?,?,?,?)", a.ID(), plan.ID(), i, b.Definition(), b.Cell().X, b.Cell().Z, b.Rotation(), b.Stuff()); err != nil {
			return conflict(err)
		}
	}
	return tx.Commit()
}
func conflict(err error) error {
	var e *sqlite.Error
	if errors.As(err, &e) && e.Code()&255 == 19 {
		return fmt.Errorf("%w: %v", ErrConflict, err)
	}
	return err
}

func (s *Store) LoadPlan(ctx context.Context, id domain.PlanID) (PlanState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return PlanState{}, err
	}
	defer tx.Rollback()
	state, err := load(ctx, tx, id)
	if err != nil {
		return PlanState{}, err
	}
	if err = tx.Commit(); err != nil {
		return PlanState{}, err
	}
	return state, nil
}
func load(ctx context.Context, tx *sql.Tx, id domain.PlanID) (PlanState, error) {
	var revision string
	if err := tx.QueryRowContext(ctx, "SELECT revision FROM plans WHERE id=?", id).Scan(&revision); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return PlanState{}, ErrNotFound
		}
		return PlanState{}, err
	}
	r, err := strconv.ParseUint(revision, 10, 64)
	if err != nil {
		return PlanState{}, err
	}
	rows, err := tx.QueryContext(ctx, "SELECT id,definition,x,z,rotation,stuff,ordinal FROM actions WHERE plan_id=? ORDER BY ordinal", id)
	if err != nil {
		return PlanState{}, err
	}
	var actions []domain.Action
	for rows.Next() {
		var aid domain.ActionID
		var def, stuff string
		var cell domain.Cell
		var rotation domain.Rotation
		var ordinal int
		if err = rows.Scan(&aid, &def, &cell.X, &cell.Z, &rotation, &stuff, &ordinal); err != nil {
			rows.Close()
			return PlanState{}, err
		}
		if ordinal != len(actions) {
			rows.Close()
			return PlanState{}, errors.New("noncontiguous action order")
		}
		b, e := domain.NewBuilding(def, cell, rotation, stuff)
		if e != nil {
			rows.Close()
			return PlanState{}, e
		}
		a, e := domain.NewBuildingAction(aid, b)
		if e != nil {
			rows.Close()
			return PlanState{}, e
		}
		actions = append(actions, a)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return PlanState{}, err
	}
	plan, err := domain.NewPlan(id, domain.PlanRevision(r), actions)
	if err != nil {
		return PlanState{}, err
	}
	state := PlanState{Spec: plan, Progress: make([]domain.Progress, 0, len(actions))}
	for _, a := range actions {
		p, e := domain.NewProgress(plan, a.ID())
		if e != nil {
			return PlanState{}, e
		}
		rows, e := tx.QueryContext(ctx, "SELECT payload FROM transitions WHERE action_id=? ORDER BY sequence", a.ID())
		if e != nil {
			return PlanState{}, e
		}
		for rows.Next() {
			var data []byte
			if e = rows.Scan(&data); e != nil {
				break
			}
			var event transition
			e = decode(data, &event)
			if e != nil {
				break
			}
			p, e = apply(p, event)
			if e != nil {
				break
			}
		}
		if e == nil {
			e = rows.Err()
		}
		rows.Close()
		if e != nil {
			return PlanState{}, fmt.Errorf("invalid action %q history: %w", a.ID(), e)
		}
		state.Progress = append(state.Progress, p)
	}
	return state, nil
}

// transition is a private persistence boundary, not a second progress model.
type transition struct {
	Kind        string
	Snapshot    domain.GenerationSnapshot
	Tick        domain.Tick
	Attempt     domain.AttemptID
	Receipt     domain.Receipt
	Observation domain.Observation
}

func decode(data []byte, event *transition) error {
	d := json.NewDecoder(bytes.NewReader(data))
	d.DisallowUnknownFields()
	if err := d.Decode(event); err != nil {
		return err
	}
	if err := d.Decode(new(json.RawMessage)); err != io.EOF {
		return errors.New("trailing transition data")
	}
	// Store records are canonical, so duplicate fields and silently defaulted fields
	// are corruption rather than alternative input syntax.
	canonical, err := json.Marshal(event)
	if err != nil {
		return err
	}
	if !bytes.Equal(canonical, data) {
		return errors.New("noncanonical transition record")
	}
	return nil
}
func apply(p domain.Progress, e transition) (domain.Progress, error) {
	switch e.Kind {
	case "prepare":
		return p.Prepare(e.Snapshot, e.Tick)
	case "dispatch":
		return p.MarkDispatched(e.Snapshot, e.Tick)
	case "receipt":
		return p.RecordReceipt(e.Attempt, e.Receipt)
	case "observe":
		return p.Observe(e.Observation, e.Snapshot)
	case "cancel":
		return p.Cancel()
	default:
		return p, fmt.Errorf("unknown transition %q", e.Kind)
	}
}
func (s *Store) advance(ctx context.Context, plan domain.PlanID, action domain.ActionID, event transition) (domain.Progress, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Progress{}, err
	}
	defer tx.Rollback()
	state, err := load(ctx, tx, plan)
	if err != nil {
		return domain.Progress{}, err
	}
	var current domain.Progress
	found := false
	for _, p := range state.Progress {
		if p.View().Action == action {
			current = p
			found = true
			break
		}
	}
	if !found {
		return domain.Progress{}, ErrNotFound
	}
	next, err := apply(current, event)
	if err != nil {
		return domain.Progress{}, err
	}
	data, err := json.Marshal(event)
	if err != nil {
		return domain.Progress{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO transitions(action_id,payload) VALUES(?,?)", action, data); err != nil {
		return domain.Progress{}, err
	}
	if err = tx.Commit(); err != nil {
		return domain.Progress{}, err
	}
	return next, nil
}
func (s *Store) Prepare(ctx context.Context, plan domain.PlanID, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "prepare", Snapshot: snapshot, Tick: tick})
}

// Dispatch authorizes a native call only on success. Any persistence error means
// the caller has no authorization to write; reopen and inspect before continuing.
func (s *Store) Dispatch(ctx context.Context, plan domain.PlanID, action domain.ActionID, snapshot domain.GenerationSnapshot, tick domain.Tick) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "dispatch", Snapshot: snapshot, Tick: tick})
}
func (s *Store) RecordReceipt(ctx context.Context, plan domain.PlanID, action domain.ActionID, attempt domain.AttemptID, receipt domain.Receipt) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "receipt", Attempt: attempt, Receipt: receipt})
}
func (s *Store) Observe(ctx context.Context, plan domain.PlanID, observation domain.Observation, current domain.GenerationSnapshot) (domain.Progress, error) {
	return s.advance(ctx, plan, observation.Action, transition{Kind: "observe", Snapshot: current, Observation: observation})
}
func (s *Store) Cancel(ctx context.Context, plan domain.PlanID, action domain.ActionID) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "cancel"})
}
