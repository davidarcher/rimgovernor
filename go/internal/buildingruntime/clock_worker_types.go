package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"time"
)

// ClockEventNative serves the event poll: one bundle read carrying the
// current scope and the events page after the review's cursor.
type ClockEventNative interface {
	ReadBundle(context.Context, *o.BundleRequest) (*o.BundleReply, bridge.Result, error)
}

type ClockPollResult struct {
	Review                store.ClockReviewState
	Captured, Interrupted bool
	// Wake and AuthorityChanged summarize evidence the journal committed in
	// this poll; they are empty when nothing was captured.
	Wake             []WakeOutcome
	Invalidated      []bridge.FactFamily
	AuthorityChanged bool
}

type ClockRenewResult struct {
	Attempt             *store.ClockAttempt
	Renewed, Reconciled bool
}

// ClockWorkerConfig sizes the three loops independently. PollTimeout and
// RenewTimeout stay under a quarter of the native epoch lease so a late poll
// or renew call can never let the lease lapse. StepTimeout has no lease
// constraint: a step is the routine census plus every composed planner's
// native reads, and it is bounded only by the Player's own call timeout.
type ClockWorkerConfig struct {
	PollInterval, RenewInterval, StepInterval, MaxBackoff time.Duration
	PollTimeout, RenewTimeout, StepTimeout                time.Duration
	PageLimit                                             uint32
	// PollWait is the long-poll bound passed to the native journal read: the
	// call returns as soon as an event lands or after PollWait. Zero polls at
	// PollInterval only. It must leave a second of PollTimeout for the read.
	PollWait time.Duration
	// Wake receives committed poll evidence and shortcuts the step loop's
	// backoff; nil keeps the timer cadence.
	Wake *WakeSignal
}
