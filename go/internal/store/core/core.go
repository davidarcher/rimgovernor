// Package core holds the controller-identity and error primitives shared
// across internal/store and its family/subsystem packages. It stays
// dependency-free (aside from domain and the sqlite driver) so both
// internal/store and internal/store/clock can import it without a cycle.
package core

import (
	"context"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"modernc.org/sqlite"
)

var ErrConflict = errors.New("plan or action identity already exists")
var ErrNotFound = errors.New("plan or action not found")
var ErrCapacity = errors.New("plan catalog capacity reached")

// ControllerSessionID identifies one persistent controller execution namespace.
// It is independent of HTTP process sessions and survives controller restarts.
type ControllerSessionID string

func Identity(ctx context.Context, tx *sql.Tx) (ControllerSessionID, error) {
	rows, err := tx.QueryContext(ctx, "SELECT singleton,controller_session_id FROM metadata")
	if err != nil {
		return "", err
	}
	defer rows.Close()
	var id string
	var singleton int
	if !rows.Next() {
		return "", errors.Join(errors.New("controller identity metadata is missing"), rows.Err())
	}
	if err = rows.Scan(&singleton, &id); err != nil {
		return "", err
	}
	if rows.Next() {
		return "", errors.New("controller identity metadata has multiple rows")
	}
	if err = rows.Err(); err != nil {
		return "", err
	}
	decoded, err := hex.DecodeString(id)
	if singleton != 1 || err != nil || len(decoded) != 32 || hex.EncodeToString(decoded) != id {
		return "", errors.New("corrupt controller identity metadata")
	}
	return ControllerSessionID(id), nil
}

func Conflict(err error) error {
	var e *sqlite.Error
	if errors.As(err, &e) && e.Code()&255 == 19 {
		return fmt.Errorf("%w: %v", ErrConflict, err)
	}
	return err
}

// SubmissionID reuses the plan-ID format rules to validate an opaque token's
// character set and length.
func SubmissionID(id string) error { _, err := domain.NewPlan(domain.PlanID(id), 1, nil); return err }
