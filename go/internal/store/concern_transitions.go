package store

import (
	"context"
	"database/sql"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ConcernTransitionRetention is how many concern_transitions rows the table
// keeps: each insert drops the oldest rows beyond it, so the table is a ring
// by count (docs/developers/contracts/persistence-contracts.md).
const ConcernTransitionRetention = 8192

// ConcernTransition is one change of a concern's winning typed cause, the
// player-facing explanation history the launcher's Explain page reads. Every
// text field is a bounded vocabulary value (concern id or planner name,
// verdict outcome, policy.Cause wire value, bounded subject, method id), never
// free text.
type ConcernTransition struct {
	// Sequence is assigned on insert and only read back; it never repeats
	// across restarts of the same database.
	Sequence int64
	// WallMS is the wall clock, unix milliseconds.
	WallMS  int64
	Tick    int64
	Concern string
	Verdict string
	// Cause is the new policy.Cause, "" when the concern cleared.
	Cause   string
	Subject string
	Method  string
	// PreviousKnown is false for the first transition seen after a restart;
	// Previous is then "". A known empty Previous means the replaced state
	// was a clear.
	PreviousKnown bool
	Previous      string
	// Held is the game ticks the previous cause stood, nil when unknown.
	Held *int64
}

func initializeConcernTransitions(ctx context.Context, tx *Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE concern_transitions(sequence INTEGER PRIMARY KEY AUTOINCREMENT, wall_ms INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), concern TEXT NOT NULL CHECK(concern<>''), verdict TEXT NOT NULL, cause TEXT NOT NULL, subject TEXT NOT NULL, method TEXT NOT NULL, previous_cause TEXT, held_ticks INTEGER CHECK(held_ticks IS NULL OR held_ticks>=0)) STRICT;`)
	return err
}

const concernTransitionColumns = "sequence,wall_ms,tick,concern,verdict,cause,subject,method,previous_cause,held_ticks"

func checkConcernTransitionsSchema(ctx context.Context, tx *Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT "+concernTransitionColumns+" FROM concern_transitions LIMIT 0")
	return err
}

// RecordConcernTransitions appends rows in one transaction and trims the table
// to ConcernTransitionRetention. A cause outside the policy.Cause vocabulary is
// refused before anything is written.
func (s *Store) RecordConcernTransitions(ctx context.Context, rows []ConcernTransition) error {
	if len(rows) == 0 {
		return nil
	}
	for _, r := range rows {
		if r.Concern == "" || r.Tick < 0 || (r.Held != nil && *r.Held < 0) {
			return fmt.Errorf("concern transition %q: concern and a non-negative tick and hold are required", r.Concern)
		}
		for _, cause := range []string{r.Cause, r.Previous} {
			if cause != "" && policy.Cause(cause).Validate() != nil {
				return fmt.Errorf("concern transition %q: unknown cause %q", r.Concern, cause)
			}
		}
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	var last int64
	for _, r := range rows {
		var previous any
		if r.PreviousKnown {
			previous = r.Previous
		}
		var held any
		if r.Held != nil {
			held = *r.Held
		}
		res, err := tx.ExecContext(ctx, "INSERT INTO concern_transitions(wall_ms,tick,concern,verdict,cause,subject,method,previous_cause,held_ticks) VALUES(?,?,?,?,?,?,?,?,?)", r.WallMS, r.Tick, r.Concern, r.Verdict, r.Cause, r.Subject, r.Method, previous, held)
		if err != nil {
			return err
		}
		if last, err = res.LastInsertId(); err != nil {
			return err
		}
	}
	if _, err = tx.ExecContext(ctx, "DELETE FROM concern_transitions WHERE sequence<=?", last-ConcernTransitionRetention); err != nil {
		return err
	}
	return tx.Commit()
}

// ConcernTransitions is every retained row, oldest first.
func (s *Store) ConcernTransitions(ctx context.Context) ([]ConcernTransition, error) {
	return queryConcernTransitions(ctx, s.db)
}

// ReadConcernTransitions reads the retained rows of the database at path
// through a read-only handle, for a process that does not own the store (the
// launcher beside a running controller): it creates nothing, takes no write
// lock and refuses a database of another schema version. A missing file is
// os.ErrNotExist.
func ReadConcernTransitions(ctx context.Context, path string) ([]ConcernTransition, error) {
	if _, err := os.Stat(path); err != nil {
		return nil, err
	}
	abs, err := filepath.Abs(path)
	if err != nil {
		return nil, err
	}
	uriPath := filepath.ToSlash(abs)
	if !strings.HasPrefix(uriPath, "/") {
		uriPath = "/" + uriPath
	}
	u := &url.URL{Scheme: "file", Path: uriPath, RawQuery: "mode=ro&_busy_timeout=2000"}
	db, err := sql.Open(DriverName, u.String())
	if err != nil {
		return nil, err
	}
	defer db.Close()
	var version int
	if err = db.QueryRowContext(ctx, "PRAGMA user_version").Scan(&version); err != nil {
		return nil, err
	}
	if version != schemaVersion {
		return nil, fmt.Errorf("%w: schema version %d, want %d", ErrIncompatible, version, schemaVersion)
	}
	return queryConcernTransitions(ctx, db)
}

type rowQuerier interface {
	QueryContext(context.Context, string, ...any) (*sql.Rows, error)
}

func queryConcernTransitions(ctx context.Context, q rowQuerier) ([]ConcernTransition, error) {
	rows, err := q.QueryContext(ctx, "SELECT "+concernTransitionColumns+" FROM concern_transitions ORDER BY sequence")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []ConcernTransition
	for rows.Next() {
		var r ConcernTransition
		var previous sql.NullString
		var held sql.NullInt64
		if err = rows.Scan(&r.Sequence, &r.WallMS, &r.Tick, &r.Concern, &r.Verdict, &r.Cause, &r.Subject, &r.Method, &previous, &held); err != nil {
			return nil, err
		}
		if r.PreviousKnown = previous.Valid; previous.Valid {
			r.Previous = previous.String
		}
		if held.Valid {
			v := held.Int64
			r.Held = &v
		}
		out = append(out, r)
	}
	return out, rows.Err()
}
