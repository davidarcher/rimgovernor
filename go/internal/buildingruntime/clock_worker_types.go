package buildingruntime

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ClockEventNative serves the event poll: the clock journal page after the
// review's cursor (an unheld read), and the bare step read that finds the
// world to ask for when none is held.
type ClockEventNative interface {
	ReadClockEvents(context.Context, *k.EventsRequest) (*k.EventsReply, bridge.Result, error)
	ReadStep(context.Context, bridge.StepRequest) (*o.BundleSnapshot, bridge.Result, error)
}

// clockSignalSource is the optional client signal of the rimgovernor.clock
// channel (bridge.Client.ClockSignal): the poll loop reads again when the mod
// announces a journal advance.
type clockSignalSource interface{ ClockSignal() *bridge.ClockSignal }

// ClockSignalWaitMax bounds ClockWorkerConfig.SignalWait: a journal advance
// whose announcement was lost is found by the next read at the latest.
const ClockSignalWaitMax = 5 * time.Second

type ClockPollResult struct {
	Review                store.ClockReviewState
	Captured, Interrupted bool
	// Wake and AuthorityChanged summarize evidence the journal committed in
	// this poll; they are empty when nothing was captured.
	Wake        []WakeOutcome
	Invalidated []bridge.FactFamily
	// InvalidatedSections are the store sections the same events
	// narrowed to (clockPageSections, #625).
	InvalidatedSections []facts.Section
	AuthorityChanged    bool
	// Stopped reports that the committed page stopped the clock: the game
	// is paused until the next window starts. StoppedAt is the earliest
	// stop's native stamp, zero when the event carried none.
	Stopped   bool
	StoppedAt time.Time
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
	// SignalWait is how long the poll loop waits for a clock-channel
	// announcement before reading the journal anyway: the read is never
	// held in native. Zero (or a native without the channel) polls at
	// PollInterval only.
	SignalWait time.Duration
	// RunningPollInterval, when set, is the cadence of the unheld poll
	// while the scheduler believes its window is running; zero keeps
	// PollInterval. A short cadence bounds how long a stop waits to be
	// seen where the clock channel is not available.
	RunningPollInterval time.Duration
	// Wake receives committed poll evidence and shortcuts the step loop's
	// backoff; nil keeps the timer cadence.
	Wake *WakeSignal
}
