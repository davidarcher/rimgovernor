package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"io"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CaravanTracking records one caravan RimGovernor formed and is still
// waiting to see return home. It is created once, from confirmed
// FormCaravan completion evidence (CaravanDepartureBoundary.ObserveCaravanDeparture's
// domain.EffectCompleted outcome), and resolved once, when
// policy.ClassifyCaravanJourney reports CaravanJourneyReturnedHome for it.
// It is deliberately independent of the departure plan/action's own
// admission and progress records: those exist to gate one FormCaravan
// dispatch and are retired with the plan, while a caravan can remain away
// for an unbounded number of ticks after its departure action completes.
type CaravanTracking struct {
	CaravanID string
	Crew      []domain.PawnID
	Resolved  bool
}

func initializeCaravanTracking(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE caravan_tracking(caravan_id TEXT PRIMARY KEY, payload BLOB NOT NULL, resolved INTEGER NOT NULL DEFAULT 0 CHECK(resolved IN (0,1))) STRICT;
CREATE INDEX active_caravan_tracking ON caravan_tracking(caravan_id) WHERE resolved=0;
CREATE TABLE caravan_stuck(caravan_id TEXT PRIMARY KEY REFERENCES caravan_tracking(caravan_id), since_tick INTEGER NOT NULL CHECK(since_tick>=0), status TEXT NOT NULL CHECK(status IN ('stopped','on_foreign_map','unknown'))) STRICT;`)
	return err
}
func checkCaravanTrackingSchema(ctx context.Context, tx *sql.Tx) error {
	if _, err := tx.ExecContext(ctx, "SELECT caravan_id,payload,resolved FROM caravan_tracking LIMIT 0"); err != nil {
		return err
	}
	_, err := tx.ExecContext(ctx, "SELECT caravan_id,since_tick,status FROM caravan_stuck LIMIT 0")
	return err
}

type caravanTrackingPayload struct {
	Crew []domain.PawnID
}

func caravanTrackingEncode(crew []domain.PawnID) ([]byte, error) {
	if len(crew) == 0 || len(crew) > 64 {
		return nil, errors.New("invalid caravan tracking crew")
	}
	seen := make(map[domain.PawnID]bool, len(crew))
	for _, pawn := range crew {
		if pawn == "" || seen[pawn] {
			return nil, errors.New("invalid caravan tracking crew")
		}
		seen[pawn] = true
	}
	return json.Marshal(caravanTrackingPayload{Crew: crew})
}
func caravanTrackingDecode(data []byte) ([]domain.PawnID, error) {
	if len(data) > 8192 {
		return nil, errors.New("caravan tracking payload exceeds bound")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	var payload caravanTrackingPayload
	if err := decoder.Decode(&payload); err != nil {
		return nil, err
	}
	if err := decoder.Decode(new(json.RawMessage)); err != io.EOF {
		return nil, errors.New("trailing caravan tracking data")
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return nil, err
	}
	if !bytes.Equal(data, canonical) {
		return nil, errors.New("noncanonical caravan tracking record")
	}
	return payload.Crew, nil
}

// StartCaravanTracking begins tracking one departed caravan. It is
// idempotent on caravanID: re-observing the same FormCaravan completion
// (e.g. after a restart replays reconciliation) leaves the existing record
// untouched rather than erroring or duplicating it, but a caravanID already
// tracked with a different crew is rejected as conflicting evidence.
func (s *Store) StartCaravanTracking(ctx context.Context, caravanID string, crew []domain.PawnID) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if err = startCaravanTrackingInTransaction(ctx, tx, caravanID, crew); err != nil {
		return err
	}
	return tx.Commit()
}

func startCaravanTrackingInTransaction(ctx context.Context, tx *sql.Tx, caravanID string, crew []domain.PawnID) error {
	if submissionID(caravanID) != nil {
		return errors.New("invalid caravan tracking id")
	}
	data, err := caravanTrackingEncode(crew)
	if err != nil {
		return err
	}
	var existing []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM caravan_tracking WHERE caravan_id=?", caravanID).Scan(&existing)
	switch {
	case errors.Is(err, sql.ErrNoRows):
		if _, err = tx.ExecContext(ctx, "INSERT INTO caravan_tracking(caravan_id,payload,resolved) VALUES(?,?,0)", caravanID, data); err != nil {
			return err
		}
	case err != nil:
		return err
	default:
		if !bytes.Equal(existing, data) {
			return errors.New("caravan tracking crew differs from existing record")
		}
	}
	return nil
}

// ListActiveCaravanTracking returns every caravan not yet resolved, ordered
// by caravan_id for deterministic polling order.
func (s *Store) ListActiveCaravanTracking(ctx context.Context) ([]CaravanTracking, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT caravan_id,payload FROM caravan_tracking WHERE resolved=0 ORDER BY caravan_id LIMIT 257")
	if err != nil {
		return nil, err
	}
	var out []CaravanTracking
	for rows.Next() {
		var id string
		var data []byte
		if err = rows.Scan(&id, &data); err != nil {
			rows.Close()
			return nil, err
		}
		crew, err := caravanTrackingDecode(data)
		if err != nil {
			rows.Close()
			return nil, err
		}
		out = append(out, CaravanTracking{CaravanID: id, Crew: crew})
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return nil, err
	}
	if len(out) > 256 {
		return nil, ErrCapacity
	}
	return out, tx.Commit()
}

// ResolveCaravanTracking marks one tracked caravan resolved: its crew is
// confirmed home and its cargo is now ordinary home-map inventory subject
// to existing haul/storage routines. Resolving an already-resolved or
// unknown caravanID is an error; callers only ever resolve a record they
// just listed as active. Any caravan_stuck record for it is cleared in the
// same commit: a resolved caravan is no longer stuck anywhere.
func (s *Store) ResolveCaravanTracking(ctx context.Context, caravanID string) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	result, err := tx.ExecContext(ctx, "UPDATE caravan_tracking SET resolved=1 WHERE caravan_id=? AND resolved=0", caravanID)
	if err != nil {
		return err
	}
	affected, err := result.RowsAffected()
	if err != nil {
		return err
	}
	if affected != 1 {
		return ErrNotFound
	}
	if _, err = tx.ExecContext(ctx, "DELETE FROM caravan_stuck WHERE caravan_id=?", caravanID); err != nil {
		return err
	}
	return tx.Commit()
}

// StuckCaravanStatus is the caravan_stuck.status text for one tracked
// caravan's most recently observed non-InFlight, non-ReturnedHome
// classification, kept independently of policy.CaravanJourneyStatus so a
// storage-layer schema change never depends on policy's Go type identity.
type StuckCaravanStatus string

const (
	StuckCaravanStopped      StuckCaravanStatus = "stopped"
	StuckCaravanOnForeignMap StuckCaravanStatus = "on_foreign_map"
	StuckCaravanUnknown      StuckCaravanStatus = "unknown"
)

// StuckCaravan is one caravan_stuck row: a caravan still tracked (see
// CaravanTracking) that CaravanJourneyTracker has been unable to prove home
// or in flight since SinceTick, with its most recent classification.
type StuckCaravan struct {
	CaravanID string
	SinceTick int64
	Status    StuckCaravanStatus
}

// MarkCaravanStuck records (or updates) that caravanID is currently
// Stopped, OnForeignMap or Unknown. It is idempotent on caravanID: the
// first call establishes SinceTick, and later calls only refresh Status,
// so SinceTick always reflects when the caravan was first observed stuck,
// not when it was last polled. caravanID must already be an active
// caravan_tracking row (enforced by foreign key); tick must be
// non-negative.
func (s *Store) MarkCaravanStuck(ctx context.Context, caravanID string, tick int64, status StuckCaravanStatus) error {
	if tick < 0 {
		return errors.New("invalid caravan stuck tick")
	}
	switch status {
	case StuckCaravanStopped, StuckCaravanOnForeignMap, StuckCaravanUnknown:
	default:
		return errors.New("invalid caravan stuck status")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = tx.ExecContext(ctx, "INSERT INTO caravan_stuck(caravan_id,since_tick,status) VALUES(?,?,?) ON CONFLICT(caravan_id) DO UPDATE SET status=excluded.status", caravanID, tick, string(status)); err != nil {
		return err
	}
	return tx.Commit()
}

// ClearCaravanStuck removes any caravan_stuck record for caravanID. It is a
// no-op when no record exists, since a caravan reported InFlight (recovered
// from Stopped) or already resolved should not be considered stuck, whether
// or not it was ever marked so.
func (s *Store) ClearCaravanStuck(ctx context.Context, caravanID string) error {
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = tx.ExecContext(ctx, "DELETE FROM caravan_stuck WHERE caravan_id=?", caravanID); err != nil {
		return err
	}
	return tx.Commit()
}

// ListStuckCaravanTracking returns every caravan_stuck record whose
// SinceTick is at least minTicksStuck ticks before currentTick, ordered by
// caravan_id for deterministic inspection. This is the surface a future
// round or an operator inspects to decide whether a caravan needs manual
// attention; nothing in this package acts on it automatically.
func (s *Store) ListStuckCaravanTracking(ctx context.Context, currentTick int64, minTicksStuck int64) ([]StuckCaravan, error) {
	if currentTick < 0 || minTicksStuck < 0 {
		return nil, errors.New("invalid stuck caravan query")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT caravan_id,since_tick,status FROM caravan_stuck WHERE ?-since_tick>=? ORDER BY caravan_id LIMIT 257", currentTick, minTicksStuck)
	if err != nil {
		return nil, err
	}
	var out []StuckCaravan
	for rows.Next() {
		var row StuckCaravan
		var status string
		if err = rows.Scan(&row.CaravanID, &row.SinceTick, &status); err != nil {
			rows.Close()
			return nil, err
		}
		row.Status = StuckCaravanStatus(status)
		out = append(out, row)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return nil, err
	}
	if len(out) > 256 {
		return nil, ErrCapacity
	}
	return out, tx.Commit()
}
