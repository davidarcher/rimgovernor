package clock

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// The functions and type aliases below exist only so internal/store's
// white-box tests (which exercise this subsystem through *Store, but also
// poke at a few of its internals) can reach otherwise-unexported clock
// package internals without duplicating them. They cannot live in a _test
// file: Go exports test-only code to its own package's tests alone, and
// those tests need *Store, which imports this package.

func Expectation(v Attempt) bridge.ClockExpectation { return clockExpectation(v) }
func ActionAvailable(ctx context.Context, tx *sql.Tx, action string) error {
	return clockActionAvailable(ctx, tx, action)
}
func EncodeIntent(v Attempt) ([]byte, error) { return encodeClockIntent(v) }

type SequenceHead = clockSequenceHead
type IntentRecord = clockIntentRecord

func SaveSequence(ctx context.Context, tx *sql.Tx, h SequenceHead) error {
	return saveClockSequence(ctx, tx, h)
}
func LoadSequence(ctx context.Context, tx *sql.Tx) (ControllerSessionID, SequenceHead, error) {
	return loadClockSequence(ctx, tx)
}
