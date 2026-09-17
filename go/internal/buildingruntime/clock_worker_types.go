package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"time"
)

type ClockEventNative interface {
	ReadClockEvents(context.Context, *k.EventsRequest) (*k.EventsReply, bridge.Result, error)
}

type ClockPollResult struct {
	Review                store.ClockReviewState
	Captured, Interrupted bool
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
}
