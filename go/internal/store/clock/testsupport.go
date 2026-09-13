package clock

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// The functions and type aliases below exist only so internal/store's
// white-box tests (which exercise this subsystem through *Store, but also
// poke at a few of its internals) can reach otherwise-unexported clock
// package internals without duplicating them.

func Expectation(v Attempt) bridge.ClockExpectation { return clockExpectation(v) }
func ActionAvailable(ctx context.Context, tx *sql.Tx, action string) error {
	return clockActionAvailable(ctx, tx, action)
}
func EncodeIntent(v Attempt) ([]byte, error)         { return encodeClockIntent(v) }
func Binary(m proto.Message) ([]byte, error)         { return clockBinary(m) }
func CanonicalBytes(m proto.Message) ([]byte, error) { return canonicalClockBytes(m) }
func EventInterrupts(e *k.Event) bool                { return clockEventInterrupts(e) }

type SequenceHead = clockSequenceHead
type IntentRecord = clockIntentRecord

func SaveSequence(ctx context.Context, tx *sql.Tx, h SequenceHead) error {
	return saveClockSequence(ctx, tx, h)
}
func LoadSequence(ctx context.Context, tx *sql.Tx) (ControllerSessionID, SequenceHead, error) {
	return loadClockSequence(ctx, tx)
}
