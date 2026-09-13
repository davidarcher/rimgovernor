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
CREATE INDEX active_caravan_tracking ON caravan_tracking(caravan_id) WHERE resolved=0;`)
	return err
}
func checkCaravanTrackingSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT caravan_id,payload,resolved FROM caravan_tracking LIMIT 0")
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
// just listed as active.
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
	return tx.Commit()
}
