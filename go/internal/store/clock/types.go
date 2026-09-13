// Package clock holds the controller-clock subsystem's admission, epoch,
// inbox, review, retirement and sequence logic, split out of internal/store
// for independent build/test caching. Unlike the action families, clock has
// no coupling to the plan-aggregate machinery (PlanState, transition/apply);
// its methods stay thin (s *Store) wrappers in internal/store because
// external packages (buildingruntime, httpapi) call them directly, but their
// bodies delegate here. Shared identity/error primitives (ControllerSessionID,
// ErrConflict, ErrNotFound, ErrCapacity) live in internal/store/core, which
// both internal/store and this package import, avoiding an import cycle.
package clock

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/core"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type ControllerSessionID = core.ControllerSessionID

var ErrConflict = core.ErrConflict
var ErrNotFound = core.ErrNotFound
var ErrCapacity = core.ErrCapacity
var ErrRetired = errors.New("clock request retired")

func identity(ctx context.Context, tx *sql.Tx) (ControllerSessionID, error) {
	return core.Identity(ctx, tx)
}
func submissionID(id string) error { return core.SubmissionID(id) }
func conflict(err error) error     { return core.Conflict(err) }

type Intent struct {
	RequestID string
	Key       string
	Snapshot  domain.GenerationSnapshot
	Command   bridge.ClockCommand
	Window    *WindowAdmission
}
type Phase string

const (
	Prepared   Phase = "prepared"
	Dispatched Phase = "dispatched"
	Uncertain  Phase = "uncertain"
	Applied    Phase = "applied"
	Refused    Phase = "refused"
)

type Attempt struct {
	Intent        Intent
	NativeAttempt *c.AttemptKey
	Phase         Phase
	Reply         *k.ControlReply
	// SupersededAt retires only the original world scope; Phase retains uncertainty.
	SupersededAt *c.ObservationContext
}

type EpochStage string

const (
	EpochRequired   EpochStage = "required"
	EpochPausing    EpochStage = "pausing"
	EpochUncertain  EpochStage = "uncertain"
	EpochPaused     EpochStage = "paused"
	EpochRetired    EpochStage = "retired"
	EpochSuperseded EpochStage = "superseded"
)

// EpochObligation derives ownership from an immutable applied Start receipt.
// Sequence is a local pause-dispatch fence, not a native attempt or a live lease.
type EpochObligation struct {
	StartRequestID string
	Epoch          *k.Epoch
	Stage          EpochStage
	Sequence       uint64
	Context        *c.ObservationContext
	Status         *k.Status
}

// SequenceState binds allocation and retirement to this journal namespace.
type SequenceState struct {
	Namespace                     ControllerSessionID
	LastAllocated, RetiredThrough uint64
}
type Retirement struct {
	State                                            SequenceState
	RemovedAttempts, RemovedEpochs, RetainedAttempts int
}

type HoldKind string

const (
	InterruptionHold HoldKind = "interruption"
	GapHold          HoldKind = "gap"
)

// Holds refer only to immutable captured cursor positions. They carry no grant
// and cannot suppress a current unsafe native observation.
type Hold struct {
	Kind                      HoldKind
	FromCursor, ThroughCursor int64
}
type ReviewState struct {
	Revision                                        uint64
	InboxCursor, ReviewedCursor, AcknowledgedCursor int64
	Holds                                           []Hold
}
type Acknowledgement struct {
	RequestID        string
	ExpectedRevision uint64
	ThroughCursor    int64
}

// WindowAdmission binds a finite start to the reviewed profile evidence. It
// grants no authority and is rechecked atomically at durable dispatch.
type WindowAdmission struct {
	Profile        string
	Snapshot       domain.GenerationSnapshot
	Tick           domain.Tick
	ReviewRevision uint64
	CapturedCursor int64
	MaxTicks       uint32
}
